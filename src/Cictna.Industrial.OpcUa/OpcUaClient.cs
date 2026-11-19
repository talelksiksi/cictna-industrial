using System.Collections.Concurrent;
using System.Threading.Channels;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;
using Cictna.Industrial.OpcUa.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cictna.Industrial.OpcUa;

/// <summary>
/// OPC UA client: connect, read, write, browse, discover and subscribe, on top of open62541.
/// </summary>
/// <remarks>
/// <para>
/// Threading: all methods are safe to call from any thread and concurrently. Internally one
/// dedicated thread owns the native client and serializes every native call (see
/// docs/native-layer.md); requests from several callers are pipelined on the same session.
/// </para>
/// <para>
/// Errors: per-item problems are reported in the results (a <see cref="DataValue"/> with a Bad
/// status, a Bad <see cref="StatusCode"/> for a write); an <see cref="OpcUaException"/> is thrown
/// only when a whole call fails (not connected, timeout, connection lost, request rejected).
/// Every call has a timeout (<see cref="OpcUaClientOptions.RequestTimeout"/>) and honours its
/// <see cref="CancellationToken"/>.
/// </para>
/// <para>
/// Batches: reads, writes and browses of any size are split into requests that respect the
/// server's operation limits (read from the server at connect time), and results come back in
/// the order of the input. Browse follows continuation points until all references are in.
/// </para>
/// <para>
/// Connections: every connection gets a fresh native client (open62541 keeps per-connection state,
/// such as the namespace table, that must not leak into the next one). When an established
/// connection is lost, the client reconnects by itself with exponential backoff
/// (<see cref="OpcUaClientOptions.Reconnect"/>); <see cref="State"/> is Reconnecting meanwhile and
/// calls fail with BadNotConnected. Subscriptions survive: they are transferred to the new session,
/// or recreated when the server cannot transfer them.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var client = await OpcUaClient.CreateAsync(new OpcUaClientOptions());
/// await client.ConnectAsync("opc.tcp://plc01:4840");
/// var speed = await client.ReadAsync(NodeId.Parse("ns=2;s=Pump01.Speed"));
/// if (speed.Status.IsGood) Console.WriteLine(speed.Value);
/// await client.WriteAsync(NodeId.Parse("ns=2;s=Pump01.Setpoint"), 1450.0);
/// </code>
/// </example>
public sealed partial class OpcUaClient : IAsyncDisposable, ISubscriptionHost
{
    private readonly OpcUaClientOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _eventPump;
    private readonly SubscriptionEngine _engine;
    private readonly ConcurrentDictionary<OpcUaSubscription, byte> _subscriptions = new();
    private readonly Lock _gate = new();
    private readonly Random _random = new();
    private ClientWorker _worker = null!; // the native client of the current or last connection; replaced per connection
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disconnectRequested;
    private CancellationTokenSource? _reconnect;
    private long _generation;
    private int _nextClientHandle;
    private OperationLimits _limits = OperationLimits.Default;
    private string[] _namespaceUris = [];
    private int _disposed;

    private OpcUaClient(OpcUaClientOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _eventPump = Task.Run(PumpEventsAsync);
        _engine = new SubscriptionEngine(this);
    }

    /// <summary>Creates a client (not connected yet).</summary>
    /// <param name="options">Settings; defaults when null.</param>
    /// <exception cref="OpcUaException">The native client could not be created (for example an invalid certificate).</exception>
    public static async Task<OpcUaClient> CreateAsync(OpcUaClientOptions? options = null)
    {
        options ??= new OpcUaClientOptions();
        Native.OpcUaNativeLibrary.EnsureCompatible();
        var logger = (ILogger?)options.LoggerFactory?.CreateLogger<OpcUaClient>() ?? NullLogger.Instance;
        var client = new OpcUaClient(options, logger);
        try
        {
            // Created now (not at connect) so a bad option, such as an invalid certificate, fails here.
            client._worker = await client.CreateWorkerAsync().ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return client;
    }

    private async Task<ClientWorker> CreateWorkerAsync()
    {
        ClientWorker? created = null;
        // The callback runs on that worker's thread; it only matters while the worker is the current one.
        created = await ClientWorker.CreateAsync(_options, _logger, change => OnWorkerStateChange(created, change)).ConfigureAwait(false);
        return created;
    }

    /// <summary>
    /// Lists the endpoints a server offers (URL, security mode and policy, accepted user tokens),
    /// without opening a session. Use it to choose how to connect.
    /// </summary>
    /// <param name="discoveryUrl">The server URL, for example <c>opc.tcp://plc01:4840</c>.</param>
    /// <param name="options">Client settings (only timeouts and certificate matter here).</param>
    /// <param name="cancellationToken">Cancels waiting (the native call finishes in the background).</param>
    public static async Task<IReadOnlyList<EndpointDescription>> GetEndpointsAsync(string discoveryUrl,
        OpcUaClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(discoveryUrl);
        await using var client = await CreateAsync(options).ConfigureAwait(false);
        var bytes = await client._worker.GetEndpointsAsync(discoveryUrl).WaitAsync(cancellationToken).ConfigureAwait(false);
        return ServiceCodec.Decode(bytes, GetEndpointsResponse.Decode).Endpoints;
    }

    /// <summary>Current connection state.</summary>
    public ConnectionState State => _state;

    /// <summary>
    /// Raised on every state change, in order, on a thread-pool thread (never on the native worker,
    /// so a slow handler cannot stall the connection). Handlers must not throw.
    /// </summary>
    public event EventHandler<ConnectionStateChange>? StateChanged;

    /// <summary>The endpoint URL of the current or last connection.</summary>
    public string? EndpointUrl { get; private set; }

    /// <summary>The server endpoint chosen for the current or last connection (security mode, policy, certificate).</summary>
    public EndpointDescription? Endpoint { get; private set; }

    /// <summary>The server's namespace URIs, indexed by namespace index. Read at connect.</summary>
    public IReadOnlyList<string> NamespaceUris => _namespaceUris;

    /// <summary>Effective operation limits used to split batches. Read at connect.</summary>
    public OperationLimits Limits => _limits;

    // ---- connection --------------------------------------------------------------------

    /// <summary>
    /// Asks the server for its endpoints, chooses one that matches the options (see
    /// <see cref="Endpoint"/>), opens the secure channel and activates the session, then reads the
    /// server's namespace array and operation limits. Completes when the session is usable.
    /// </summary>
    /// <remarks>
    /// The TCP connection always goes to <paramref name="endpointUrl"/>, even if the server
    /// advertises another host name in its endpoints (common on plant networks where that name
    /// does not resolve). When no endpoint fits, the exception message lists what the server offers.
    /// Once connected, a lost connection is restored automatically (see
    /// <see cref="OpcUaClientOptions.Reconnect"/>); a failed first connection is not retried.
    /// </remarks>
    /// <param name="endpointUrl">Server URL, for example <c>opc.tcp://plc01:4840</c>.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <exception cref="OpcUaException">The server is unreachable, refused the security or identity, or timed out.</exception>
    /// <exception cref="InvalidOperationException">The client is already connected, connecting or reconnecting.</exception>
    public async Task ConnectAsync(string endpointUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointUrl);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_gate)
        {
            if (_state != ConnectionState.Disconnected)
            {
                throw new InvalidOperationException($"The client is {_state}; disconnect first.");
            }
            _disconnectRequested = false;
            EndpointUrl = endpointUrl;
            SetState(ConnectionState.Connecting, StatusCodes.Good);
        }
        try
        {
            await ConnectCoreAsync(endpointUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_state == ConnectionState.Connecting)
                {
                    SetState(ConnectionState.Disconnected, ex is OpcUaException o ? o.StatusCode : StatusCodes.BadRequestCancelledByClient);
                }
            }
            throw;
        }
    }

    /// <summary>
    /// One connection attempt on a new native client. On success the new client becomes the
    /// current one, the state becomes Connected and subscriptions are moved to the new session.
    /// </summary>
    private async Task ConnectCoreAsync(string endpointUrl, CancellationToken cancellationToken)
    {
        var worker = await CreateWorkerAsync().ConfigureAwait(false);
        try
        {
            var discovery = await worker.GetEndpointsAsync(endpointUrl).WaitAsync(_options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var endpoint = EndpointSelector.Select(ServiceCodec.Decode(discovery, GetEndpointsResponse.Decode).Endpoints, _options);
            var encoder = new BinaryEncoder();
            endpoint.Encode(encoder);
            var tokenType = _options.Identity is UserIdentity.UserNameIdentity ? UserTokenType.UserName : UserTokenType.Anonymous;
            Endpoint = endpoint;
            LogEndpoint(_logger, endpoint.SecurityMode, endpoint.SecurityPolicyUri ?? string.Empty, tokenType);

            await worker.ConnectAsync(endpointUrl, encoder.ToArray(), tokenType, cancellationToken).ConfigureAwait(false);
            var (namespaces, limits) = await ReadServerInfoAsync(worker, cancellationToken).ConfigureAwait(false);

            ClientWorker previous;
            long generation;
            lock (_gate)
            {
                if (_disconnectRequested || _disposed != 0)
                {
                    throw new OpcUaException(StatusCodes.BadConnectionClosed, "Disconnect was requested while connecting");
                }
                previous = _worker;
                _worker = worker;
                _namespaceUris = namespaces;
                _limits = limits;
                generation = ++_generation;
                SetState(ConnectionState.Connected, StatusCodes.Good);
            }
            _engine.SessionUp(generation);
            if (previous != worker)
            {
                _ = RetireAsync(previous, abandonSession: true);
            }
            LogConnected(_logger, endpointUrl, namespaces.Length, limits.MaxNodesPerRead);
            if (worker.State != ConnectionState.Connected)
            {
                OnConnectionLost(StatusCodes.BadConnectionClosed); // died in the short gap above
            }
        }
        catch
        {
            if (worker != _worker)
            {
                await RetireAsync(worker, abandonSession: false).ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// Closes the session and the channel. Stops automatic reconnection. Subscriptions are kept on
    /// the client and created again on the next <see cref="ConnectAsync"/>. Does nothing when not connected.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for the server's answer.</param>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        CancellationTokenSource? reconnect;
        bool closeSession;
        lock (_gate)
        {
            _disconnectRequested = true;
            reconnect = _reconnect;
            _reconnect = null;
            closeSession = _state == ConnectionState.Connected;
            if (_state is ConnectionState.Connected or ConnectionState.Reconnecting)
            {
                SetState(closeSession ? ConnectionState.Disconnecting : ConnectionState.Disconnected, StatusCodes.Good);
            }
        }
        if (reconnect is not null)
        {
            await reconnect.CancelAsync().ConfigureAwait(false);
        }
        _engine.SessionDown();
        if (closeSession)
        {
            try
            {
                await _worker.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (_state == ConnectionState.Disconnecting)
                    {
                        SetState(ConnectionState.Disconnected, StatusCodes.Good);
                    }
                }
            }
        }
    }

    private async Task<(string[] Namespaces, OperationLimits Limits)> ReadServerInfoAsync(ClientWorker worker, CancellationToken cancellationToken)
    {
        var request = new ReadRequest(Header(), 0, TimestampsToReturn.Neither,
        [
            new ReadValueId(WellKnownNodes.NamespaceArray),
            new ReadValueId(WellKnownNodes.MaxNodesPerRead),
            new ReadValueId(WellKnownNodes.MaxNodesPerWrite),
            new ReadValueId(WellKnownNodes.MaxNodesPerBrowse),
            new ReadValueId(WellKnownNodes.MaxMonitoredItemsPerCall),
        ]).Encode();
        var bytes = await worker.SendAsync("Read", EncodingIds.ReadRequest, EncodingIds.ReadResponse, request, cancellationToken).ConfigureAwait(false);
        var response = ServiceCodec.Decode(bytes, ReadResponse.Decode);
        ServiceCodec.CheckServiceResult("Read", response.Header);
        ServiceCodec.CheckCount("Read", response.Results.Length, 5);
        var results = response.Results;

        var namespaces = results[0].Value.TryGetValue(out string?[] uris) ? uris.Select(u => u ?? string.Empty).ToArray() : [];

        // Server limit 0 or unreadable means "no limit"; keep request sizes reasonable anyway.
        static int Limit(DataValue v, int fallback) =>
            v.Status.IsGood && v.Value.TryGetValue(out uint n) && n > 0 ? (int)Math.Min(n, int.MaxValue) : fallback;
        var d = OperationLimits.Default;
        var limits = new OperationLimits(
            _options.MaxNodesPerRead ?? Limit(results[1], d.MaxNodesPerRead),
            _options.MaxNodesPerWrite ?? Limit(results[2], d.MaxNodesPerWrite),
            _options.MaxNodesPerBrowse ?? Limit(results[3], d.MaxNodesPerBrowse))
        {
            MaxMonitoredItemsPerCall = Limit(results[4], d.MaxMonitoredItemsPerCall),
        };
        return (namespaces, limits);
    }

    // ---- connection loss and reconnect --------------------------------------------------------

    private void OnWorkerStateChange(ClientWorker? worker, ConnectionStateChange change)
    {
        if (worker is not null && worker == _worker && change.State == ConnectionState.Disconnected)
        {
            OnConnectionLost(change.Reason);
        }
    }

    /// <summary>The current connection died (or is presumed dead). Runs on any thread; never blocks.</summary>
    private void OnConnectionLost(StatusCode reason)
    {
        lock (_gate)
        {
            if (_state != ConnectionState.Connected || _disconnectRequested || _disposed != 0)
            {
                return;
            }
            LogConnectionLost(_logger, reason.Name);
            if (!_options.Reconnect.Enabled)
            {
                SetState(ConnectionState.Disconnected, reason);
            }
            else
            {
                SetState(ConnectionState.Reconnecting, reason);
                _reconnect = new CancellationTokenSource();
                _ = ReconnectLoopAsync(_reconnect.Token);
            }
        }
        _engine.SessionDown();
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var url = EndpointUrl!;
        for (var attempt = 1; !cancellationToken.IsCancellationRequested; attempt++)
        {
            TimeSpan delay;
            lock (_gate)
            {
                delay = _options.Reconnect.DelayFor(attempt, _random);
            }
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                LogReconnectAttempt(_logger, attempt, url);
                await ConnectCoreAsync(url, cancellationToken).ConfigureAwait(false);
                OpcUaTelemetry.ReconnectAttempts.Add(1, new KeyValuePair<string, object?>("result", "success"));
                LogReconnected(_logger, url, attempt);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is OpcUaException or TimeoutException or OperationCanceledException)
            {
                OpcUaTelemetry.ReconnectAttempts.Add(1, new KeyValuePair<string, object?>("result", "failed"));
                LogReconnectFailed(_logger, attempt, ex.Message);
            }
        }
    }

    /// <summary>Shuts a worker down. With <paramref name="abandonSession"/> the session is left on the server for transfer.</summary>
    private async Task RetireAsync(ClientWorker worker, bool abandonSession)
    {
        try
        {
            if (abandonSession && worker.State == ConnectionState.Connected)
            {
                await worker.AbandonSessionAsync().ConfigureAwait(false);
            }
            await worker.StopWorkerAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OpcUaException or ObjectDisposedException or InvalidOperationException)
        {
            LogRetireFailed(_logger, ex);
        }
        finally
        {
            worker.Dispose();
        }
    }

    /// <summary>Changes the state and queues the event. Caller holds <see cref="_gate"/>.</summary>
    private void SetState(ConnectionState state, StatusCode reason)
    {
        if (state == _state)
        {
            return;
        }
        _state = state;
        OpcUaTelemetry.StateChanges.Add(1, new KeyValuePair<string, object?>("state", state.ToString()));
        LogStateChange(_logger, state, reason.Name);
        var change = new ConnectionStateChange(state, reason);
        _events.Writer.TryWrite(() => StateChanged?.Invoke(this, change));
    }

    // ---- subscriptions -----------------------------------------------------------------------

    /// <summary>
    /// Creates a subscription. Add monitored items to it with <see cref="OpcUaSubscription.AddItemsAsync"/>
    /// and read the changes from <see cref="OpcUaSubscription.DataChanges"/>.
    /// </summary>
    /// <param name="options">Settings; defaults when null (1 s publishing interval).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="OpcUaException">Not connected, or the server refused (for example BadTooManySubscriptions).</exception>
    public async Task<OpcUaSubscription> CreateSubscriptionAsync(SubscriptionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var subscription = new OpcUaSubscription(_engine, this, options ?? new SubscriptionOptions());
        await subscription.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = Interlocked.Read(ref _generation);
            await subscription.CreateOnServerAsync(cancellationToken).ConfigureAwait(false);
            subscription.Generation = generation;
        }
        catch
        {
            subscription.Gate.Release();
            await subscription.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        subscription.Gate.Release();
        _subscriptions[subscription] = 0;
        _engine.Register(subscription);
        return subscription;
    }

    /// <summary>The subscriptions of this client that are not disposed.</summary>
    public IReadOnlyCollection<OpcUaSubscription> Subscriptions => _subscriptions.Keys.ToArray();

    void ISubscriptionHost.Forget(OpcUaSubscription subscription) => _subscriptions.TryRemove(subscription, out _);

    Task<byte[]> ISubscriptionHost.SendAsync(string service, uint requestEncodingId, uint responseEncodingId, byte[] request, TimeSpan? timeout, CancellationToken cancellationToken) =>
        _worker.SendAsync(service, requestEncodingId, responseEncodingId, request, cancellationToken, timeout);

    RequestHeader ISubscriptionHost.Header() => Header();

    TimeSpan ISubscriptionHost.RequestTimeout => _options.RequestTimeout;

    int ISubscriptionHost.MaxMonitoredItemsPerCall => _limits.MaxMonitoredItemsPerCall;

    long ISubscriptionHost.SessionGeneration => Interlocked.Read(ref _generation);

    bool ISubscriptionHost.IsConnected => _state == ConnectionState.Connected;

    uint ISubscriptionHost.NextClientHandle() => unchecked((uint)Interlocked.Increment(ref _nextClientHandle));

    void ISubscriptionHost.RaiseEvent(Action raise) => _events.Writer.TryWrite(raise);

    void ISubscriptionHost.ReportKeepAliveMissed() => OnConnectionLost(StatusCodes.BadTimeout);

    ILogger ISubscriptionHost.Logger => _logger;

    // ---- read ----------------------------------------------------------------------------

    /// <summary>Reads the value of one node, with quality and timestamps.</summary>
    /// <param name="nodeId">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value; check <see cref="DataValue.Status"/> before using it.</returns>
    public async Task<DataValue> ReadAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        (await ReadAsync([new ReadValueId(nodeId)], cancellationToken: cancellationToken).ConfigureAwait(false))[0];

    /// <summary>
    /// Reads many attributes in as few requests as the server allows. Results are in input order;
    /// a node that cannot be read gets a DataValue with a Bad status, it does not fail the call.
    /// </summary>
    /// <param name="nodes">What to read.</param>
    /// <param name="timestamps">Which timestamps to return.</param>
    /// <param name="maxAge">Accept cached values up to this age (0: read from the device).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<DataValue>> ReadAsync(IReadOnlyList<ReadValueId> nodes,
        TimestampsToReturn timestamps = TimestampsToReturn.Both, TimeSpan maxAge = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            return [];
        }
        var results = new DataValue[nodes.Count];
        await ForEachChunkAsync(nodes, () => _limits.MaxNodesPerRead, async (offset, chunk) =>
        {
            var values = await ReadChunkAsync(chunk, timestamps, maxAge.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            values.CopyTo(results, offset);
        }, limit => _limits = _limits with { MaxNodesPerRead = limit }).ConfigureAwait(false);
        return results;
    }

    private async Task<DataValue[]> ReadChunkAsync(IReadOnlyList<ReadValueId> chunk, TimestampsToReturn timestamps, double maxAgeMs, CancellationToken cancellationToken)
    {
        var request = new ReadRequest(Header(), maxAgeMs, timestamps, chunk).Encode();
        var bytes = await _worker.SendAsync("Read", EncodingIds.ReadRequest, EncodingIds.ReadResponse, request, cancellationToken).ConfigureAwait(false);
        var response = ServiceCodec.Decode(bytes, ReadResponse.Decode);
        ServiceCodec.CheckServiceResult("Read", response.Header);
        ServiceCodec.CheckCount("Read", response.Results.Length, chunk.Count);
        return response.Results;
    }

    // ---- write ---------------------------------------------------------------------------

    /// <summary>Writes the value of one node.</summary>
    /// <param name="nodeId">The node.</param>
    /// <param name="value">The value. Its type must match the node's data type exactly (for example Double, not Float).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Good, or why the server refused (BadTypeMismatch, BadNotWritable, BadUserAccessDenied...).</returns>
    public async Task<StatusCode> WriteAsync(NodeId nodeId, Variant value, CancellationToken cancellationToken = default) =>
        (await WriteAsync([new WriteValue(nodeId, new DataValue(value))], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Writes many attributes in as few requests as the server allows. Results are in input order.</summary>
    /// <param name="values">What to write.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<StatusCode>> WriteAsync(IReadOnlyList<WriteValue> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return [];
        }
        var results = new StatusCode[values.Count];
        await ForEachChunkAsync(values, () => _limits.MaxNodesPerWrite, async (offset, chunk) =>
        {
            var request = new WriteRequest(Header(), chunk).Encode();
            var bytes = await _worker.SendAsync("Write", EncodingIds.WriteRequest, EncodingIds.WriteResponse, request, cancellationToken).ConfigureAwait(false);
            var response = ServiceCodec.Decode(bytes, WriteResponse.Decode);
            ServiceCodec.CheckServiceResult("Write", response.Header);
            ServiceCodec.CheckCount("Write", response.Results.Length, chunk.Count);
            response.Results.CopyTo(results, offset);
        }, limit => _limits = _limits with { MaxNodesPerWrite = limit }).ConfigureAwait(false);
        return results;
    }

    // ---- browse ------------------------------------------------------------------------

    /// <summary>Lists the children of a node (hierarchical references, forward), all of them.</summary>
    /// <param name="nodeId">The node, for example <see cref="WellKnownNodes.ObjectsFolder"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<BrowseResult> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        (await BrowseAsync([new BrowseDescription(nodeId)], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>
    /// Browses many nodes. Continuation points are followed until every reference has been
    /// returned, and released if the call is cancelled. Results are in input order.
    /// </summary>
    /// <param name="nodes">What to browse.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<BrowseResult>> BrowseAsync(IReadOnlyList<BrowseDescription> nodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            return [];
        }
        var results = new BrowseResult[nodes.Count];
        await ForEachChunkAsync(nodes, () => _limits.MaxNodesPerBrowse, async (offset, chunk) =>
        {
            var request = new BrowseRequest(Header(), _options.MaxReferencesPerNode, chunk).Encode();
            var bytes = await _worker.SendAsync("Browse", EncodingIds.BrowseRequest, EncodingIds.BrowseResponse, request, cancellationToken).ConfigureAwait(false);
            var response = ServiceCodec.Decode(bytes, BrowseResponse.Decode);
            ServiceCodec.CheckServiceResult("Browse", response.Header);
            ServiceCodec.CheckCount("Browse", response.Results.Length, chunk.Count);
            var collected = await FollowContinuationPointsAsync(response.Results, cancellationToken).ConfigureAwait(false);
            collected.CopyTo(results, offset);
        }, limit => _limits = _limits with { MaxNodesPerBrowse = limit }).ConfigureAwait(false);
        return results;
    }

    private async Task<BrowseResult[]> FollowContinuationPointsAsync(RawBrowseResult[] first, CancellationToken cancellationToken)
    {
        var references = first.Select(r => new List<ReferenceDescription>(r.References)).ToArray();
        var status = first.Select(r => r.Status).ToArray();
        var open = new Dictionary<int, byte[]>();
        for (var i = 0; i < first.Length; i++)
        {
            if (first[i].ContinuationPoint is { Length: > 0 } cp)
            {
                open[i] = cp;
            }
        }

        try
        {
            while (open.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var indexes = open.Keys.ToArray();
                var request = new BrowseNextRequest(Header(), false, indexes.Select(i => open[i]).ToArray()).Encode();
                var bytes = await _worker.SendAsync("BrowseNext", EncodingIds.BrowseNextRequest, EncodingIds.BrowseNextResponse, request, cancellationToken).ConfigureAwait(false);
                var response = ServiceCodec.Decode(bytes, BrowseResponse.Decode);
                ServiceCodec.CheckServiceResult("BrowseNext", response.Header);
                ServiceCodec.CheckCount("BrowseNext", response.Results.Length, indexes.Length);
                for (var k = 0; k < indexes.Length; k++)
                {
                    var i = indexes[k];
                    var r = response.Results[k];
                    references[i].AddRange(r.References);
                    if (r.Status.IsBad)
                    {
                        status[i] = r.Status; // e.g. BadContinuationPointInvalid: keep what we have
                    }
                    if (r.ContinuationPoint is { Length: > 0 } next && r.Status.IsGood)
                    {
                        open[i] = next;
                    }
                    else
                    {
                        open.Remove(i);
                    }
                }
            }
        }
        catch when (open.Count > 0)
        {
            // Release server-side continuation points so they do not pile up (best effort).
            await ReleaseContinuationPointsAsync(open.Values.ToArray()).ConfigureAwait(false);
            throw;
        }

        return references.Select((r, i) => new BrowseResult(status[i], r)).ToArray();
    }

    private async Task ReleaseContinuationPointsAsync(byte[][] points)
    {
        try
        {
            var request = new BrowseNextRequest(Header(), true, points).Encode();
            await _worker.SendAsync("BrowseNext", EncodingIds.BrowseNextRequest, EncodingIds.BrowseNextResponse, request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OpcUaException or ObjectDisposedException)
        {
            LogReleaseFailed(_logger, ex);
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="action"/> over chunks of at most the current limit. If the server answers
    /// BadTooManyOperations (its real limit is lower than advertised), the chunk size is halved,
    /// remembered through <paramref name="lowerLimit"/>, and the chunk retried.
    /// </summary>
    private static async Task ForEachChunkAsync<T>(IReadOnlyList<T> items, Func<int> limit,
        Func<int, IReadOnlyList<T>, Task> action, Action<int> lowerLimit)
    {
        var offset = 0;
        while (offset < items.Count)
        {
            var size = Math.Max(1, Math.Min(limit(), items.Count - offset));
            var chunk = new ArraySegment<T>(items as T[] ?? items.ToArray(), offset, size);
            try
            {
                await action(offset, chunk).ConfigureAwait(false);
                offset += size;
            }
            catch (OpcUaException ex) when (ex.StatusCode == StatusCodes.BadTooManyOperations && size > 1)
            {
                lowerLimit(size / 2);
            }
        }
    }

    private RequestHeader Header() => new((uint)Math.Clamp(_options.RequestTimeout.TotalMilliseconds, 0, uint.MaxValue));

    private async Task PumpEventsAsync()
    {
        await foreach (var raise in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                LogHandlerFailed(_logger, ex);
            }
        }
    }

    /// <summary>Disconnects (briefly waiting for the server) and releases the native client.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions.Keys)
        {
            await subscription.DisposeAsync().ConfigureAwait(false); // deletes it on the server while still connected
        }
        CancellationTokenSource? reconnect;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }
            _disposed = 1;
            _disconnectRequested = true;
            reconnect = _reconnect;
            _reconnect = null;
        }
        if (reconnect is not null)
        {
            await reconnect.CancelAsync().ConfigureAwait(false);
        }
        await _engine.DisposeAsync().ConfigureAwait(false);
        if (_worker is { } worker)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await worker.DisconnectAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OpcUaException or OperationCanceledException or ObjectDisposedException)
            {
                // Closing anyway.
            }
            await worker.StopWorkerAsync().ConfigureAwait(false);
            worker.Dispose();
        }
        lock (_gate)
        {
            if (_state != ConnectionState.Disconnected)
            {
                SetState(ConnectionState.Disconnected, StatusCodes.Good);
            }
        }
        _events.Writer.TryComplete();
        await _eventPump.ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {EndpointUrl}: {NamespaceCount} namespaces, {MaxNodesPerRead} nodes per read")]
    private static partial void LogConnected(ILogger logger, string endpointUrl, int namespaceCount, int maxNodesPerRead);

    [LoggerMessage(Level = LogLevel.Information, Message = "Using endpoint {SecurityMode} {SecurityPolicy} with {TokenType} identity")]
    private static partial void LogEndpoint(ILogger logger, MessageSecurityMode securityMode, string securityPolicy, UserTokenType tokenType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release browse continuation points")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "An event handler threw")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "OPC UA client {State} ({Reason})")]
    private static partial void LogStateChange(ILogger logger, ConnectionState state, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection lost: {Reason}")]
    private static partial void LogConnectionLost(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnect attempt {Attempt} to {EndpointUrl}")]
    private static partial void LogReconnectAttempt(ILogger logger, int attempt, string endpointUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to {EndpointUrl} after {Attempts} attempt(s)")]
    private static partial void LogReconnected(ILogger logger, string endpointUrl, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnect attempt {Attempt} failed: {Error}")]
    private static partial void LogReconnectFailed(ILogger logger, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closing a previous native client failed")]
    private static partial void LogRetireFailed(ILogger logger, Exception exception);
}
