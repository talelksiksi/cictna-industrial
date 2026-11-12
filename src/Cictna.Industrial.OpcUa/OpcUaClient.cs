using System.Threading.Channels;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;
using Cictna.Industrial.OpcUa.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cictna.Industrial.OpcUa;

/// <summary>
/// OPC UA client: connect, read, write, browse and discover, on top of open62541.
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
/// Phase 1 scope: no automatic reconnection yet (Phase 2). After a lost connection
/// <see cref="State"/> becomes Disconnected and <see cref="StateChanged"/> reports the reason;
/// call <see cref="ConnectAsync"/> again.
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
public sealed partial class OpcUaClient : IAsyncDisposable
{
    private readonly ClientWorker _worker;
    private readonly OpcUaClientOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<ConnectionStateChange> _events = Channel.CreateUnbounded<ConnectionStateChange>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _eventPump;
    private OperationLimits _limits = OperationLimits.Default;
    private string[] _namespaceUris = [];
    private int _disposed;

    private OpcUaClient(ClientWorker worker, OpcUaClientOptions options, ILogger logger)
    {
        _worker = worker;
        _options = options;
        _logger = logger;
        _eventPump = Task.Run(PumpEventsAsync);
    }

    /// <summary>Creates a client (not connected yet).</summary>
    /// <param name="options">Settings; defaults when null.</param>
    /// <exception cref="OpcUaException">The native client could not be created (for example an invalid certificate).</exception>
    public static async Task<OpcUaClient> CreateAsync(OpcUaClientOptions? options = null)
    {
        options ??= new OpcUaClientOptions();
        Native.OpcUaNativeLibrary.EnsureCompatible();
        var logger = (ILogger?)options.LoggerFactory?.CreateLogger<OpcUaClient>() ?? NullLogger.Instance;
        OpcUaClient? client = null;
        var worker = await ClientWorker.CreateAsync(options, logger, change => client?._events.Writer.TryWrite(change)).ConfigureAwait(false);
        client = new OpcUaClient(worker, options, logger);
        return client;
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
        return Decode(bytes, GetEndpointsResponse.Decode).Endpoints;
    }

    /// <summary>Current connection state.</summary>
    public ConnectionState State => _worker.State;

    /// <summary>
    /// Raised on every state change, in order, on a thread-pool thread (never on the native worker,
    /// so a slow handler cannot stall the connection). Handlers must not throw.
    /// </summary>
    public event EventHandler<ConnectionStateChange>? StateChanged;

    /// <summary>The endpoint URL of the current or last connection.</summary>
    public string? EndpointUrl { get; private set; }

    /// <summary>The server's namespace URIs, indexed by namespace index. Read at connect.</summary>
    public IReadOnlyList<string> NamespaceUris => _namespaceUris;

    /// <summary>Effective operation limits used to split batches. Read at connect.</summary>
    public OperationLimits Limits => _limits;

    // ---- connection --------------------------------------------------------------------

    /// <summary>
    /// Opens a secure channel and activates a session, then reads the server's namespace array and
    /// operation limits. Completes when the session is usable.
    /// </summary>
    /// <param name="endpointUrl">Server URL, for example <c>opc.tcp://plc01:4840</c>.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <exception cref="OpcUaException">The server is unreachable, refused the security or identity, or timed out.</exception>
    /// <exception cref="InvalidOperationException">The client is already connected or connecting.</exception>
    public async Task ConnectAsync(string endpointUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointUrl);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EndpointUrl = endpointUrl;
        await _worker.ConnectAsync(endpointUrl, cancellationToken).ConfigureAwait(false);
        await ReadServerInfoAsync(cancellationToken).ConfigureAwait(false);
        LogConnected(_logger, endpointUrl, _namespaceUris.Length, _limits.MaxNodesPerRead);
    }

    /// <summary>Closes the session and the channel. Does nothing when not connected.</summary>
    /// <param name="cancellationToken">Stops waiting for the server's answer.</param>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _worker.DisconnectAsync(cancellationToken);
    }

    private async Task ReadServerInfoAsync(CancellationToken cancellationToken)
    {
        var results = await ReadChunkAsync(
            [
                new ReadValueId(WellKnownNodes.NamespaceArray),
                new ReadValueId(WellKnownNodes.MaxNodesPerRead),
                new ReadValueId(WellKnownNodes.MaxNodesPerWrite),
                new ReadValueId(WellKnownNodes.MaxNodesPerBrowse),
            ], TimestampsToReturn.Neither, 0, cancellationToken).ConfigureAwait(false);

        _namespaceUris = results[0].Value.TryGetValue(out string?[] uris) ? uris.Select(u => u ?? string.Empty).ToArray() : [];

        // Server limit 0 or unreadable means "no limit"; keep request sizes reasonable anyway.
        static int Limit(DataValue v, int fallback) =>
            v.Status.IsGood && v.Value.TryGetValue(out uint n) && n > 0 ? (int)Math.Min(n, int.MaxValue) : fallback;
        var d = OperationLimits.Default;
        _limits = new OperationLimits(
            _options.MaxNodesPerRead ?? Limit(results[1], d.MaxNodesPerRead),
            _options.MaxNodesPerWrite ?? Limit(results[2], d.MaxNodesPerWrite),
            _options.MaxNodesPerBrowse ?? Limit(results[3], d.MaxNodesPerBrowse));
    }

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
        var response = Decode(bytes, ReadResponse.Decode);
        CheckServiceResult("Read", response.Header);
        CheckCount("Read", response.Results.Length, chunk.Count);
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
            var response = Decode(bytes, WriteResponse.Decode);
            CheckServiceResult("Write", response.Header);
            CheckCount("Write", response.Results.Length, chunk.Count);
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
            var response = Decode(bytes, BrowseResponse.Decode);
            CheckServiceResult("Browse", response.Header);
            CheckCount("Browse", response.Results.Length, chunk.Count);
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
                var response = Decode(bytes, BrowseResponse.Decode);
                CheckServiceResult("BrowseNext", response.Header);
                CheckCount("BrowseNext", response.Results.Length, indexes.Length);
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

    private static T Decode<T>(byte[] bytes, Func<BinaryDecoder, T> read)
    {
        try
        {
            var decoder = new BinaryDecoder(bytes);
            var value = read(decoder);
            decoder.EnsureFullyConsumed();
            return value;
        }
        catch (OpcUaDecodingException ex)
        {
            throw new OpcUaException(StatusCodes.BadDecodingError, "Response could not be decoded", ex);
        }
    }

    private static void CheckServiceResult(string service, ResponseHeader header)
    {
        if (header.ServiceResult.IsBad)
        {
            throw new OpcUaException(header.ServiceResult, $"{service} failed");
        }
    }

    private static void CheckCount(string service, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new OpcUaException(StatusCodes.BadUnexpectedError, $"{service} returned {actual} results for {expected} items");
        }
    }

    private async Task PumpEventsAsync()
    {
        await foreach (var change in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                StateChanged?.Invoke(this, change);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _worker.DisconnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OpcUaException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing anyway.
        }
        await _worker.StopWorkerAsync().ConfigureAwait(false);
        _worker.Dispose();
        _events.Writer.TryComplete();
        await _eventPump.ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {EndpointUrl}: {NamespaceCount} namespaces, {MaxNodesPerRead} nodes per read")]
    private static partial void LogConnected(ILogger logger, string endpointUrl, int namespaceCount, int maxNodesPerRead);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release browse continuation points")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A StateChanged handler threw")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
