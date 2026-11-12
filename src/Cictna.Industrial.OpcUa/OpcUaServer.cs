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
    /// Server certificate. Without one only SecurityPolicy None is offered; with one, None plus
    /// Basic256Sha256 and the newer policies, in Sign and SignAndEncrypt modes.
    /// </summary>
    public ApplicationCertificate? Certificate { get; init; }

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
public sealed class OpcUaServer : IAsyncDisposable
{
    private readonly ServerWorker _worker;
    private int _disposed;

    private OpcUaServer(ServerWorker worker, int port)
    {
        _worker = worker;
        Port = port;
    }

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
        var worker = await ServerWorker.CreateAsync(options, logger).ConfigureAwait(false);
        return new OpcUaServer(worker, options.Port);
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
        await _worker.StopWorkerAsync().ConfigureAwait(false);
        _worker.Dispose();
    }

    /// <summary>Owns the native server on its own thread.</summary>
    private sealed class ServerWorker : NativeWorker
    {
        private readonly OpcUaServerOptions _options;
        private ServerHandle? _handle;

        private ServerWorker(OpcUaServerOptions options, ILogger logger) : base("cictna-opcua-server", logger) => _options = options;

        public static async Task<ServerWorker> CreateAsync(OpcUaServerOptions options, ILogger logger)
        {
            var worker = new ServerWorker(options, logger);
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
                    LogLevel = NativeLog.ToNativeLevel(_options.NativeLogLevel),
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
