using System.Collections.Concurrent;
using System.Threading.Channels;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;
using Cictna.Industrial.OpcUa.Internal;

namespace Cictna.Industrial.OpcUa;

/// <summary>
/// A subscription: the server watches monitored items and sends their changes, which arrive in
/// <see cref="DataChanges"/>. Create one with <see cref="OpcUaClient.CreateSubscriptionAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Delivery: changes are written, in the order the server sent them, to a bounded channel
/// (<see cref="SubscriptionOptions.BufferCapacity"/>). Read it with
/// <c>await foreach (var change in subscription.DataChanges.ReadAllAsync())</c>. A slow reader slows
/// the client's Publish requests down rather than losing changes on the client side.
/// </para>
/// <para>
/// Reconnects: the subscription object survives them. After a new session the client first tries
/// to transfer the subscription and fetch the messages it missed (Republish); if the server cannot
/// do that it recreates the subscription and all items. <see cref="EventOccurred"/> reports which
/// happened, and any notification messages that could not be recovered.
/// </para>
/// <para>Threading: all members are safe to call from any thread.</para>
/// </remarks>
/// <example>
/// <code>
/// await using var subscription = await client.CreateSubscriptionAsync(new SubscriptionOptions { PublishingInterval = TimeSpan.FromMilliseconds(500) });
/// await subscription.AddItemsAsync([new MonitoredItemRequest(NodeId.Parse("ns=2;s=Pump01.Speed"))]);
/// await foreach (var change in subscription.DataChanges.ReadAllAsync(stoppingToken))
///     Console.WriteLine($"{change.Item.NodeId} = {change.Value.Value} ({change.Value.Status})");
/// </code>
/// </example>
public sealed class OpcUaSubscription : IAsyncDisposable
{
    private readonly SubscriptionEngine _engine;
    private readonly ISubscriptionHost _host;
    private readonly ConcurrentDictionary<uint, MonitoredItem> _items = new();
    private readonly Channel<DataChange> _changes;
    private readonly CancellationTokenSource _closing = new();
    private long _lostMessages;
    private int _disposed;

    internal OpcUaSubscription(SubscriptionEngine engine, ISubscriptionHost host, SubscriptionOptions options)
    {
        _engine = engine;
        _host = host;
        Options = options;
        _changes = Channel.CreateBounded<DataChange>(new BoundedChannelOptions(Math.Max(1, options.BufferCapacity))
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>The settings it was created with.</summary>
    public SubscriptionOptions Options { get; }

    /// <summary>The server's id of the subscription in the current session (changes when it is recreated).</summary>
    public uint Id { get; private set; }

    /// <summary>The publishing interval the server actually uses.</summary>
    public TimeSpan RevisedPublishingInterval { get; private set; }

    /// <summary>The keep-alive count the server actually uses.</summary>
    public uint RevisedKeepAliveCount { get; private set; }

    /// <summary>The lifetime count the server actually uses.</summary>
    public uint RevisedLifetimeCount { get; private set; }

    /// <summary>The monitored items, including any the server refused (see <see cref="MonitoredItem.Status"/>).</summary>
    public IReadOnlyCollection<MonitoredItem> Items => _items.Values.ToArray();

    /// <summary>Value changes, in the order the server sent them. Completes when the subscription is disposed.</summary>
    public ChannelReader<DataChange> DataChanges => _changes.Reader;

    /// <summary>Notification messages that could not be recovered after an outage or a gap, since creation.</summary>
    public long LostNotificationMessages => Interlocked.Read(ref _lostMessages);

    /// <summary>
    /// Raised when the subscription was transferred, recreated, lost messages or missed keep-alives.
    /// Runs on a thread-pool thread, in order; handlers must not throw.
    /// </summary>
    public event EventHandler<SubscriptionEvent>? EventOccurred;

    // ---- state used by the engine (single-threaded inside its loop) ----------------------

    /// <summary>Session generation in which the subscription exists on the server.</summary>
    internal long Generation { get; set; }

    /// <summary>Sequence number of the last notification message delivered; 0 before the first.</summary>
    internal uint LastSequenceNumber { get; set; }

    /// <summary>Acknowledgements to send with the next Publish request.</summary>
    internal List<uint> PendingAcks { get; } = [];

    /// <summary>Last time a keep-alive or data message arrived (monotonic ticks).</summary>
    internal long LastActivity { get; set; }

    /// <summary>Whether a keep-alive miss was already reported for the current silence.</summary>
    internal bool KeepAliveMissReported { get; set; }

    /// <summary>Serializes changes of the server-side state (create, recreate, items).</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Cancelled when the subscription is disposed; stops a blocked delivery.</summary>
    internal CancellationToken Closing => _closing.Token;

    /// <summary>The time after which a missing keep-alive means trouble.</summary>
    internal TimeSpan KeepAlivePeriod => RevisedPublishingInterval * Math.Max(1, RevisedKeepAliveCount);

    internal ChannelWriter<DataChange> Writer => _changes.Writer;

    internal bool TryGetItem(uint clientHandle, out MonitoredItem item) => _items.TryGetValue(clientHandle, out item!);

    internal void AddLost(long count) => Interlocked.Add(ref _lostMessages, count);

    internal void Raise(SubscriptionEvent e) => _host.RaiseEvent(() => EventOccurred?.Invoke(this, e));

    // ---- monitored items -------------------------------------------------------------------

    /// <summary>Adds one monitored item.</summary>
    /// <param name="request">What to monitor.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The item; check its <see cref="MonitoredItem.Status"/>.</returns>
    public async Task<MonitoredItem> AddItemAsync(MonitoredItemRequest request, CancellationToken cancellationToken = default) =>
        (await AddItemsAsync([request], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>
    /// Adds monitored items, in as few requests as the server allows. An item the server refuses
    /// (unknown node, wrong attribute) is still returned, with a Bad <see cref="MonitoredItem.Status"/>;
    /// it does not fail the call. Each item sends its current value first.
    /// </summary>
    /// <param name="requests">What to monitor.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="OpcUaException">The whole call failed (not connected, timeout, subscription gone).</exception>
    public async Task<IReadOnlyList<MonitoredItem>> AddItemsAsync(IReadOnlyList<MonitoredItemRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var items = requests.Select(r => new MonitoredItem(r ?? throw new ArgumentException("Null request.", nameof(requests)), _host.NextClientHandle())).ToArray();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Registered first, so a notification that arrives right after creation finds its item.
            foreach (var item in items)
            {
                _items[item.ClientHandle] = item;
            }
            try
            {
                await CreateItemsOnServerAsync(items, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                foreach (var item in items)
                {
                    _items.TryRemove(item.ClientHandle, out _);
                }
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
        return items;
    }

    /// <summary>Stops monitoring items. Unknown or already removed items are ignored.</summary>
    /// <param name="items">Items of this subscription.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task RemoveItemsAsync(IReadOnlyList<MonitoredItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = items.Where(i => _items.TryRemove(i.ClientHandle, out _)).ToArray();
            var ids = removed.Where(i => i.Status.IsGood && i.ServerId != 0).Select(i => i.ServerId).ToArray();
            for (var offset = 0; offset < ids.Length; offset += _host.MaxMonitoredItemsPerCall)
            {
                var chunk = ids.Skip(offset).Take(_host.MaxMonitoredItemsPerCall).ToArray();
                var request = new DeleteMonitoredItemsRequest(_host.Header(), Id, chunk).Encode();
                var bytes = await _host.SendAsync("DeleteMonitoredItems", SubscriptionEncodingIds.DeleteMonitoredItemsRequest,
                    SubscriptionEncodingIds.DeleteMonitoredItemsResponse, request, null, cancellationToken).ConfigureAwait(false);
                var response = ServiceCodec.Decode(bytes, StatusResultsResponse.Decode);
                ServiceCodec.CheckServiceResult("DeleteMonitoredItems", response.Header);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---- server-side creation (first time and after a lost subscription) --------------------

    /// <summary>Creates the subscription on the server in the current session. Caller holds <see cref="Gate"/>.</summary>
    internal async Task CreateOnServerAsync(CancellationToken cancellationToken)
    {
        var o = Options;
        var request = new CreateSubscriptionRequest(_host.Header(), o.PublishingInterval.TotalMilliseconds,
            Math.Max(o.LifetimeCount, o.KeepAliveCount * 3), o.KeepAliveCount, o.MaxNotificationsPerPublish, true, o.Priority).Encode();
        var bytes = await _host.SendAsync("CreateSubscription", SubscriptionEncodingIds.CreateSubscriptionRequest,
            SubscriptionEncodingIds.CreateSubscriptionResponse, request, null, cancellationToken).ConfigureAwait(false);
        var response = ServiceCodec.Decode(bytes, CreateSubscriptionResponse.Decode);
        ServiceCodec.CheckServiceResult("CreateSubscription", response.Header);
        Id = response.SubscriptionId;
        RevisedPublishingInterval = TimeSpan.FromMilliseconds(response.RevisedPublishingInterval);
        RevisedKeepAliveCount = response.RevisedMaxKeepAliveCount;
        RevisedLifetimeCount = response.RevisedLifetimeCount;
        LastSequenceNumber = 0;
        PendingAcks.Clear();
        LastActivity = Environment.TickCount64;
        KeepAliveMissReported = false;
    }

    /// <summary>Creates all known items again, in a new server-side subscription. Caller holds <see cref="Gate"/>.</summary>
    internal Task RecreateItemsAsync(CancellationToken cancellationToken) =>
        CreateItemsOnServerAsync(_items.Values.OrderBy(i => i.ClientHandle).ToArray(), cancellationToken);

    private async Task CreateItemsOnServerAsync(MonitoredItem[] items, CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, _host.MaxMonitoredItemsPerCall);
        for (var offset = 0; offset < items.Length; offset += limit)
        {
            var chunk = items.Skip(offset).Take(limit).ToArray();
            var request = new CreateMonitoredItemsRequest(_host.Header(), Id, Options.Timestamps, chunk.Select(ToCreateRequest).ToArray()).Encode();
            var bytes = await _host.SendAsync("CreateMonitoredItems", SubscriptionEncodingIds.CreateMonitoredItemsRequest,
                SubscriptionEncodingIds.CreateMonitoredItemsResponse, request, null, cancellationToken).ConfigureAwait(false);
            var response = ServiceCodec.Decode(bytes, CreateMonitoredItemsResponse.Decode);
            ServiceCodec.CheckServiceResult("CreateMonitoredItems", response.Header);
            ServiceCodec.CheckCount("CreateMonitoredItems", response.Results.Length, chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var result = response.Results[i];
                chunk[i].Status = result.Status;
                chunk[i].ServerId = result.Status.IsGood ? result.MonitoredItemId : 0;
                chunk[i].RevisedSamplingInterval = TimeSpan.FromMilliseconds(Math.Max(0, result.RevisedSamplingInterval));
                chunk[i].RevisedQueueSize = result.RevisedQueueSize;
            }
        }
    }

    private static MonitoredItemCreateRequest ToCreateRequest(MonitoredItem item)
    {
        var r = item.Request;
        var filter = r.Trigger is null && r.DeadbandType == DeadbandType.None
            ? ExtensionObject.Empty
            : new DataChangeFilterMessage(r.Trigger ?? DataChangeTrigger.StatusValue, r.DeadbandType, r.DeadbandValue).ToExtensionObject();
        // -1 asks the server to sample at the publishing interval (Part 4, 5.12.1.2).
        var sampling = r.SamplingInterval is { } s ? s.TotalMilliseconds : -1;
        return new MonitoredItemCreateRequest(new ReadValueId(r.NodeId, r.AttributeId, r.IndexRange), r.MonitoringMode,
            new MonitoringParametersMessage(item.ClientHandle, sampling, filter, r.QueueSize, r.DiscardOldest));
    }

    // ---- disposal ------------------------------------------------------------------------------

    /// <summary>
    /// Deletes the subscription on the server (best effort, when connected) and completes
    /// <see cref="DataChanges"/>. Changes already buffered can still be read.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _closing.CancelAsync().ConfigureAwait(false);
        _host.Forget(this);
        await _engine.UnregisterAsync(this).ConfigureAwait(false);
        if (Generation == _host.SessionGeneration && _host.IsConnected && Id != 0)
        {
            try
            {
                var request = new DeleteSubscriptionsRequest(_host.Header(), [Id]).Encode();
                await _host.SendAsync("DeleteSubscriptions", SubscriptionEncodingIds.DeleteSubscriptionsRequest,
                    SubscriptionEncodingIds.DeleteSubscriptionsResponse, request, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OpcUaException or ObjectDisposedException or OperationCanceledException)
            {
                // The server deletes it anyway when its lifetime runs out or the session closes.
            }
        }
        _changes.Writer.TryComplete();
        _closing.Dispose();
        Gate.Dispose();
    }

    /// <summary>Id and item count, for logs.</summary>
    public override string ToString() => $"OpcUaSubscription(id {Id}, {_items.Count} items)";
}
