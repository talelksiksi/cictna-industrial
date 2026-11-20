using System.Runtime.InteropServices;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;
using Cictna.Industrial.OpcUa.Internal;
using Cictna.Industrial.OpcUa.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cictna.Industrial.OpcUa;

/// <summary>Settings of an <see cref="OpcUaServer"/>.</summary>
public sealed record OpcUaServerOptions
{
    /// <summary>TCP port to listen on. Default 4840, the OPC UA standard port.</summary>
    public int Port { get; init; } = 4840;

    /// <summary>Name shown to clients.</summary>
    public string ApplicationName { get; init; } = "Cictna OPC UA Server";

    /// <summary>Globally unique URI; must match the certificate's URI when one is used.</summary>
    public string ApplicationUri { get; init; } = $"urn:{Environment.MachineName}:cictna:opcua-server";

    /// <summary>
    /// Server certificate. Without one (and without <see cref="CertificateStore"/>) only
    /// SecurityPolicy None is offered; with one, None plus Basic256Sha256 and the newer policies,
    /// in Sign and SignAndEncrypt modes.
    /// </summary>
    public ApplicationCertificate? Certificate { get; init; }

    /// <summary>
    /// The server's certificate store. Provides the server certificate when
    /// <see cref="Certificate"/> is null (created on first start, renewed before it expires) and the
    /// trust list for client certificates. While the server runs, changes to the trusted and issuer
    /// folders take effect within <see cref="TrustListRefreshInterval"/>, and refused client
    /// certificates are saved to rejected/certs for review.
    /// </summary>
    public CertificateStore? CertificateStore { get; init; }

    /// <summary>
    /// Accept every client certificate without checking it. For development and tests only: any
    /// client could open a secure channel. Without it, client certificates are checked against the
    /// <see cref="CertificateStore"/>'s trust list (no store: all are rejected). SecurityPolicy None
    /// is not affected.
    /// </summary>
    public bool AcceptAnyClientCertificateForDevelopment { get; init; }

    /// <summary>How often the store is checked for trust list changes and refused clients. Default 1 s.</summary>
    public TimeSpan TrustListRefreshInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Allow sessions without a user. Default true.</summary>
    public bool AllowAnonymous { get; init; } = true;

    /// <summary>One user name/password login, or null. Phase 3 brings proper user management.</summary>
    public UserIdentity.UserNameIdentity? User { get; init; }

    /// <summary>OperationLimits advertised and enforced; 0 keeps open62541's default.</summary>
    public int MaxNodesPerRead { get; init; }

    /// <summary>OperationLimits for Write; 0 keeps the default.</summary>
    public int MaxNodesPerWrite { get; init; }

    /// <summary>OperationLimits for Browse; 0 keeps the default.</summary>
    public int MaxNodesPerBrowse { get; init; }

    /// <summary>Most references returned per node before a continuation point; 0 keeps the default.</summary>
    public int MaxReferencesPerNode { get; init; }

    /// <summary>
    /// Most values one monitored item may queue between two Publish requests; 0 keeps open62541's
    /// default of 100. Clients that must ride out longer outages without losing values need more:
    /// at 10 changes per second, 100 covers only 10 seconds.
    /// </summary>
    public int MaxMonitoredItemQueueSize { get; init; }

    /// <summary>Where log lines go. Null disables logging.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Lowest native log level forwarded. Default Warning.</summary>
    public LogLevel NativeLogLevel { get; init; } = LogLevel.Warning;
}

/// <summary>
/// An OPC UA server on open62541 with an address space built from code: folders and variables of
/// any built-in type, values updated locally at any time.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 uses it as the reference server for the client tests; the PLC simulator (Phase 5) is
/// built on it. Like the client, one dedicated thread owns the native server and runs its event
/// loop; all methods are safe to call from any thread.
/// </para>
/// <para>
/// Data types: a variable's data type, value rank and array dimensions are taken from its initial
/// value (an Int32 scalar gives DataType Int32 and ValueRank -1, a 2x3 Double matrix gives
/// ValueRank 2 and ArrayDimensions [2, 3]). Clients must then write values of exactly that type.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var server = await OpcUaServer.StartAsync(new OpcUaServerOptions { Port = 4840 });
/// var ns = await server.AddNamespaceAsync("urn:demo:plant");
/// var pumps = await server.AddFolderAsync(WellKnownNodes.ObjectsFolder, new NodeId("Pumps", ns), "Pumps");
/// var speed = await server.AddVariableAsync(pumps, new NodeId("Pump01.Speed", ns), "Speed", 0.0);
/// await server.WriteValueAsync(speed, new DataValue(1450.5));
/// </code>
/// </example>
public sealed partial class OpcUaServer : IAsyncDisposable
{
    private readonly ServerWorker _worker;
    private readonly OpcUaServerOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _trustWatcher;
    private string? _trustListVersion;
    private int _disposed;

    private OpcUaServer(ServerWorker worker, OpcUaServerOptions options, ILogger logger, string? trustListVersion)
    {
        _worker = worker;
        _options = options;
        _logger = logger;
        _trustListVersion = trustListVersion;
        Port = options.Port;
        ServerCertificate = options.Certificate?.Certificate;
        _trustWatcher = ChecksClients ? Task.Run(WatchTrustListAsync) : Task.CompletedTask;
    }

    /// <summary>Whether client certificates are checked against a store's trust list (and refused ones recorded).</summary>
    private bool ChecksClients => _options.Certificate is not null && !_options.AcceptAnyClientCertificateForDevelopment && _options.CertificateStore is not null;

    /// <summary>Creates the server and opens its listen socket.</summary>
    /// <param name="options">Settings; defaults when null.</param>
    /// <exception cref="OpcUaException">The server could not start (port in use, invalid certificate...).</exception>
    public static async Task<OpcUaServer> StartAsync(OpcUaServerOptions? options = null)
    {
        options ??= new OpcUaServerOptions();
        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Port, "Port must be 1 to 65535.");
        }
        OpcUaNativeLibrary.EnsureCompatible();
        var logger = (ILogger?)options.LoggerFactory?.CreateLogger<OpcUaServer>() ?? NullLogger.Instance;

        // Own certificate from the store when none is given (created, or renewed when expiring).
        var store = options.CertificateStore;
        if (options.Certificate is null && store is not null)
        {
            var certificate = store.GetOrCreateApplicationCertificate(new OwnCertificateSettings(options.ApplicationUri, options.ApplicationName)
            {
                DnsNames = [Environment.MachineName, "localhost"],
            }, out var renewed);
            if (renewed)
            {
                LogRenewed(logger, certificate.Thumbprint, certificate.NotAfter);
            }
            options = options with { Certificate = certificate };
        }
        if (options.Certificate is { } own && own.ExpiresWithin(TimeSpan.FromDays(30)))
        {
            LogExpiringSoon(logger, own.Thumbprint, own.NotAfter);
        }
        if (options.Certificate is not null && options.AcceptAnyClientCertificateForDevelopment)
        {
            LogAcceptAny(logger);
        }

        string? version = null;
        byte[]? trustList = null;
        if (options.Certificate is not null && !options.AcceptAnyClientCertificateForDevelopment)
        {
            version = store?.TrustListVersion();
            trustList = (store?.ReadTrustList() ?? TrustList.Empty).Encode();
        }
        var worker = await ServerWorker.CreateAsync(options, trustList, logger).ConfigureAwait(false);
        return new OpcUaServer(worker, options, logger, version);
    }

    /// <summary>The server's certificate (DER), or null when only SecurityPolicy None is offered. Clients trust this.</summary>
    public byte[]? ServerCertificate { get; }

    /// <summary>
    /// Reads the trust list from the <see cref="OpcUaServerOptions.CertificateStore"/> now and
    /// applies it to new connections, instead of waiting for the next automatic check.
    /// </summary>
    public async Task ReloadTrustListAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!ChecksClients)
        {
            return;
        }
        var store = _options.CertificateStore!;
        var version = store.TrustListVersion();
        var bytes = store.ReadTrustList().Encode();
        await _worker.InvokeAsync(() => _worker.SetTrustList(bytes)).ConfigureAwait(false);
        _trustListVersion = version;
        LogTrustListReloaded(_logger, store.Trusted.Count, store.Issuers.Count);
    }

    /// <summary>Applies trust list changes and records refused clients, until the server stops.</summary>
    private async Task WatchTrustListAsync()
    {
        var store = _options.CertificateStore!;
        using var timer = new PeriodicTimer(_options.TrustListRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    if (store.TrustListVersion() != _trustListVersion)
                    {
                        await ReloadTrustListAsync().ConfigureAwait(false);
                    }
                    var rejected = await _worker.InvokeAsync(_worker.GetRejected).ConfigureAwait(false);
                    foreach (var certificate in rejected)
                    {
                        if (store.Reject(certificate))
                        {
                            var c = new ApplicationCertificate(certificate, []);
                            LogClientRejected(_logger, c.Subject, c.Thumbprint, store.RejectedCertsPath);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpcUaException or System.Security.Cryptography.CryptographicException)
                {
                    LogTrustListError(_logger, ex); // keep watching; the next tick tries again
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>The listening port.</summary>
    public int Port { get; }

    /// <summary>URL clients on this machine can use: <c>opc.tcp://localhost:port</c>.</summary>
    public string LocalEndpointUrl => $"opc.tcp://localhost:{Port}";

    /// <summary>Registers a namespace URI and returns its index (idempotent).</summary>
    /// <param name="uri">The namespace URI, for example <c>urn:demo:plant</c>.</param>
    public Task<ushort> AddNamespaceAsync(string uri)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        return _worker.InvokeAsync(() => _worker.AddNamespace(uri));
    }

    /// <summary>Adds a folder (FolderType object) under a parent.</summary>
    /// <param name="parent">Parent node, for example <see cref="WellKnownNodes.ObjectsFolder"/>.</param>
    /// <param name="nodeId">NodeId of the new folder; NodeId.Null lets the server choose.</param>
    /// <param name="name">Browse and display name (browse name in the NodeId's namespace).</param>
    /// <returns>The NodeId of the folder.</returns>
    public Task<NodeId> AddFolderAsync(NodeId parent, NodeId nodeId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var attributes = new ObjectAttributes(LocalizedText.FromText(name), default).Encode();
        var item = new AddNodesItem(parent, WellKnownNodes.Organizes, nodeId, new QualifiedName(nodeId.NamespaceIndex, name),
            NodeClass.Object, ExtensionObject.FromBinary(new NodeId(EncodingIds.ObjectAttributes), attributes),
            WellKnownNodes.FolderType).Encode();
        return _worker.InvokeAsync(() => _worker.AddNode(item));
    }

    /// <summary>Adds a variable whose data type, value rank and dimensions follow its initial value.</summary>
    /// <param name="parent">Parent folder or object.</param>
    /// <param name="nodeId">NodeId of the new variable; NodeId.Null lets the server choose.</param>
    /// <param name="name">Browse and display name.</param>
    /// <param name="initialValue">Initial value; must not be <see cref="Variant.Null"/>.</param>
    /// <param name="writable">Whether clients may write the value.</param>
    /// <returns>The NodeId of the variable.</returns>
    public Task<NodeId> AddVariableAsync(NodeId parent, NodeId nodeId, string name, Variant initialValue, bool writable = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (initialValue.IsNull)
        {
            throw new ArgumentException("The initial value defines the data type and cannot be null.", nameof(initialValue));
        }
        // Built-in type ids double as the NodeIds of their DataType nodes in namespace 0
        // (Boolean = i=1 ... DiagnosticInfo = i=25), with ExtensionObject meaning Structure (i=22)
        // and Variant meaning BaseDataType (i=24).
        var dataType = new NodeId((uint)initialValue.Type);
        var valueRank = !initialValue.IsArray ? -1 : initialValue.IsMatrix ? initialValue.ArrayDimensions!.Count : 1;
        var dims = initialValue.ArrayDimensions?.Select(d => (uint)d).ToArray()
            ?? (initialValue.IsArray ? [0u] : null);
        const byte read = 0x01, write = 0x02;
        var attributes = new VariableAttributes(LocalizedText.FromText(name), default, initialValue, dataType, valueRank, dims,
            writable ? (byte)(read | write) : read).Encode();
        var item = new AddNodesItem(parent, WellKnownNodes.HasComponent, nodeId, new QualifiedName(nodeId.NamespaceIndex, name),
            NodeClass.Variable, ExtensionObject.FromBinary(new NodeId(EncodingIds.VariableAttributes), attributes),
            WellKnownNodes.BaseDataVariableType).Encode();
        return _worker.InvokeAsync(() => _worker.AddNode(item));
    }

    /// <summary>Sets a variable's value locally (no session, no access checks), with quality and timestamps.</summary>
    /// <param name="nodeId">The variable.</param>
    /// <param name="value">The value. A Bad status with no value is allowed (to simulate a failed sensor).</param>
    /// <exception cref="OpcUaException">The node does not exist or the type does not match.</exception>
    public Task WriteValueAsync(NodeId nodeId, DataValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = EncodeWith(e => new WriteValue(nodeId, value).Encode(e));
        return _worker.InvokeAsync(() =>
        {
            _worker.Write(bytes);
            return true;
        });
    }

    /// <summary>Reads a variable's current value locally.</summary>
    /// <param name="nodeId">The variable.</param>
    public async Task<DataValue> ReadValueAsync(NodeId nodeId)
    {
        var bytes = EncodeWith(e => new ReadValueId(nodeId).Encode(e));
        var result = await _worker.InvokeAsync(() => _worker.Read(bytes)).ConfigureAwait(false);
        var decoder = new BinaryDecoder(result);
        return decoder.ReadDataValue();
    }

    private static byte[] EncodeWith(Action<BinaryEncoder> write)
    {
        var e = new BinaryEncoder();
        write(e);
        return e.ToArray();
    }

    /// <summary>Closes all sessions, stops listening and releases the native server. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await _trustWatcher.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _worker.StopWorkerAsync().ConfigureAwait(false);
        _worker.Dispose();
        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server certificate renewed: {Thumbprint}, valid until {NotAfter:u}. Clients that trust the old one need to trust this one (or the CA that signed it).")]
    private static partial void LogRenewed(ILogger logger, string thumbprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server certificate {Thumbprint} expires on {NotAfter:u}")]
    private static partial void LogExpiringSoon(ILogger logger, string thumbprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Accepting every client certificate (development mode). Never use this on a plant network.")]
    private static partial void LogAcceptAny(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trust list reloaded: {Trusted} trusted, {Issuers} issuer certificates")]
    private static partial void LogTrustListReloaded(ILogger logger, int trusted, int issuers);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client certificate refused: {Subject} [{Thumbprint}]. Saved to {Folder}; move it to trusted/certs to allow this client.")]
    private static partial void LogClientRejected(ILogger logger, string subject, string thumbprint, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the certificate store")]
    private static partial void LogTrustListError(ILogger logger, Exception exception);

    /// <summary>Owns the native server on its own thread.</summary>
    private sealed class ServerWorker : NativeWorker
    {
        private readonly OpcUaServerOptions _options;
        private readonly byte[]? _trustList;
        private ServerHandle? _handle;

        private ServerWorker(OpcUaServerOptions options, byte[]? trustList, ILogger logger) : base("cictna-opcua-server", logger)
        {
            _options = options;
            _trustList = trustList;
        }

        public static async Task<ServerWorker> CreateAsync(OpcUaServerOptions options, byte[]? trustList, ILogger logger)
        {
            var worker = new ServerWorker(options, trustList, logger);
            try
            {
                await worker.StartAsync().ConfigureAwait(false);
            }
            catch
            {
                worker.Dispose();
                throw;
            }
            return worker;
        }

        public Task StopWorkerAsync() => StopAsync();

        protected override unsafe void OnStart()
        {
            using var strings = new Utf8Strings();
            fixed (byte* cert = _options.Certificate?.Certificate)
            fixed (byte* key = _options.Certificate?.PrivateKey)
            fixed (byte* trust = _trustList)
            {
                var native = new ServerOptionsNative
                {
                    StructSize = (uint)sizeof(ServerOptionsNative),
                    Port = (ushort)_options.Port,
                    ApplicationUri = strings.Add(_options.ApplicationUri),
                    ApplicationName = strings.Add(_options.ApplicationName),
                    Certificate = cert,
                    CertificateLength = (nuint)(_options.Certificate?.Certificate.Length ?? 0),
                    PrivateKey = key,
                    PrivateKeyLength = (nuint)(_options.Certificate?.PrivateKey.Length ?? 0),
                    AllowAnonymous = _options.AllowAnonymous ? 1u : 0u,
                    Username = strings.Add(_options.User?.Name),
                    Password = strings.Add(_options.User?.Password),
                    MaxNodesPerRead = (uint)Math.Max(0, _options.MaxNodesPerRead),
                    MaxNodesPerWrite = (uint)Math.Max(0, _options.MaxNodesPerWrite),
                    MaxNodesPerBrowse = (uint)Math.Max(0, _options.MaxNodesPerBrowse),
                    MaxReferencesPerNode = (uint)Math.Max(0, _options.MaxReferencesPerNode),
                    MaxMonitoredItemQueueSize = (uint)Math.Max(0, _options.MaxMonitoredItemQueueSize),
                    LogLevel = NativeLog.ToNativeLevel(_options.NativeLogLevel),
                    AcceptAnyClientCertificate = _options.AcceptAnyClientCertificateForDevelopment ? 1u : 0u,
                    TrustList = trust,
                    TrustListLength = (nuint)(_trustList?.Length ?? 0),
                };
                var status = NativeMethods.ServerNew(&native, out var handle);
                if (status != 0)
                {
                    handle.Dispose();
                    throw new OpcUaException(new StatusCode(status), "Could not create the OPC UA server");
                }
                _handle = handle;
            }
            var started = NativeMethods.ServerStart(_handle);
            if (started != 0)
            {
                DrainLogs(); // the reason (e.g. port in use) is usually in the log
                _handle.Dispose();
                throw new OpcUaException(new StatusCode(started), $"Could not listen on port {_options.Port}");
            }
        }

        /// <summary>Replaces the trust list of the running server. Worker thread only.</summary>
        public unsafe bool SetTrustList(byte[] trustList)
        {
            fixed (byte* p = trustList)
            {
                var status = NativeMethods.ServerSetTrustList(_handle!, p, (nuint)trustList.Length);
                if (status != 0)
                {
                    throw new OpcUaException(new StatusCode(status), "Could not apply the trust list");
                }
            }
            return true;
        }

        /// <summary>The client certificates the server refused so far. Worker thread only.</summary>
        public byte[][] GetRejected()
        {
            var status = NativeMethods.ServerGetRejected(_handle!, out var buffer, out var length);
            var bytes = NativeMethods.TakeBuffer(buffer, length);
            if (status != 0)
            {
                throw new OpcUaException(new StatusCode(status), "Could not read the refused client certificates");
            }
            return TrustList.Decode(new BinaryDecoder(bytes)).Trusted.ToArray();
        }

        protected override void Iterate()
        {
            NativeMethods.ServerIterate(_handle!, out var nextMs);
            DrainLogs();
            // Sleep until the server's next timer or a posted command, at most 10 ms so new
            // connections and requests are picked up promptly.
            WaitForWork((int)Math.Min(nextMs, 10));
        }

        protected override void OnStop()
        {
            if (_handle is { IsInvalid: false, IsClosed: false })
            {
                NativeMethods.ServerStop(_handle);
                DrainLogs();
                _handle.Dispose();
            }
        }

        public ushort AddNamespace(string uri)
        {
            Check(NativeMethods.ServerAddNamespace(_handle!, uri, out var index), $"AddNamespace {uri}");
            return index;
        }

        public unsafe NodeId AddNode(byte[] item)
        {
            uint status;
            nint id;
            nuint length;
            fixed (byte* p = item)
            {
                status = NativeMethods.ServerAddNode(_handle!, p, (nuint)item.Length, out id, out length);
            }
            var bytes = NativeMethods.TakeBuffer(id, length);
            Check(status, "AddNode");
            return new BinaryDecoder(bytes).ReadNodeId();
        }

        public unsafe void Write(byte[] writeValue)
        {
            fixed (byte* p = writeValue)
            {
                Check(NativeMethods.ServerWrite(_handle!, p, (nuint)writeValue.Length), "Write");
            }
        }

        public unsafe byte[] Read(byte[] readValueId)
        {
            uint status;
            nint dv;
            nuint length;
            fixed (byte* p = readValueId)
            {
                status = NativeMethods.ServerRead(_handle!, p, (nuint)readValueId.Length, (int)TimestampsToReturn.Both, out dv, out length);
            }
            var bytes = NativeMethods.TakeBuffer(dv, length);
            Check(status, "Read");
            return bytes;
        }

        private static void Check(uint status, string operation)
        {
            if (status != 0)
            {
                throw new OpcUaException(new StatusCode(status), operation);
            }
        }

        private unsafe void DrainLogs()
        {
            while (true)
            {
                var more = NativeMethods.ServerPollLog(_handle!, out var level, out var category, out var message, out var dropped);
                if (dropped > 0)
                {
                    NativeLog.Dropped(Logger, dropped, "server");
                }
                if (more != 1)
                {
                    return;
                }
                NativeLog.Write(Logger, level, category, Marshal.PtrToStringUTF8((nint)message) ?? string.Empty, "server");
            }
        }
    }
}
