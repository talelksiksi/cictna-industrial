using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa;

/// <summary>Settings of a subscription (Part 4, 5.13.2). The server may revise them; see <see cref="OpcUaSubscription"/>.</summary>
public sealed record SubscriptionOptions
{
    /// <summary>How often the server sends collected changes. Default 1 s.</summary>
    public TimeSpan PublishingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Publishing intervals without changes after which the server sends an empty keep-alive message.
    /// Keep-alives prove the subscription and the connection are alive. Default 10.
    /// </summary>
    public uint KeepAliveCount { get; init; } = 10;

    /// <summary>
    /// Publishing intervals without any Publish request from the client after which the server deletes
    /// the subscription. Must be at least three times <see cref="KeepAliveCount"/>. The default (60
    /// intervals of 1 s) lets a subscription survive a one-minute outage and be transferred back.
    /// </summary>
    public uint LifetimeCount { get; init; } = 60;

    /// <summary>Most notifications per Publish response; 0 means no limit.</summary>
    public uint MaxNotificationsPerPublish { get; init; }

    /// <summary>Relative priority among this session's subscriptions (higher is served first).</summary>
    public byte Priority { get; init; }

    /// <summary>
    /// Value changes buffered for the reader of <see cref="OpcUaSubscription.DataChanges"/>. When the
    /// buffer is full, the client stops asking the server for more until the reader catches up, so
    /// changes queue on the server instead of being dropped here. Default 10 000.
    /// </summary>
    public int BufferCapacity { get; init; } = 10_000;

    /// <summary>Which timestamps the server attaches to value changes. Default both.</summary>
    public TimestampsToReturn Timestamps { get; init; } = TimestampsToReturn.Both;
}

/// <summary>What to monitor and how (Part 4, 5.12.2 and 7.21).</summary>
/// <param name="NodeId">The node, usually a variable.</param>
public sealed record MonitoredItemRequest(NodeId NodeId)
{
    /// <summary>The attribute to monitor; Value by default.</summary>
    public AttributeId AttributeId { get; init; } = AttributeId.Value;

    /// <summary>Part of an array value to monitor, for example <c>"2:5"</c>; null for all.</summary>
    public string? IndexRange { get; init; }

    /// <summary>
    /// How often the server samples the value. Null uses the subscription's publishing interval;
    /// <see cref="TimeSpan.Zero"/> asks for the fastest rate the server supports.
    /// </summary>
    public TimeSpan? SamplingInterval { get; init; }

    /// <summary>Changes the server keeps per item between two publishes. 1 keeps only the latest.</summary>
    public uint QueueSize { get; init; } = 1;

    /// <summary>When the queue is full, drop the oldest change (true) or the newest (false).</summary>
    public bool DiscardOldest { get; init; } = true;

    /// <summary>Whether the item reports, only samples, or is off.</summary>
    public MonitoringMode MonitoringMode { get; init; } = MonitoringMode.Reporting;

    /// <summary>What counts as a change. Null leaves the server default (status or value).</summary>
    public DataChangeTrigger? Trigger { get; init; }

    /// <summary>Deadband filtering on the server.</summary>
    public DeadbandType DeadbandType { get; init; } = DeadbandType.None;

    /// <summary>Deadband size: engineering units for Absolute, percent of the EURange for Percent.</summary>
    public double DeadbandValue { get; init; }

    /// <summary>Any application object to carry along (for example the tag it belongs to). Not sent to the server.</summary>
    public object? Tag { get; init; }
}

/// <summary>
/// One monitored item of a subscription. The object stays the same across reconnects; the server-side
/// id may change when the subscription has to be recreated.
/// </summary>
public sealed class MonitoredItem
{
    internal MonitoredItem(MonitoredItemRequest request, uint clientHandle)
    {
        Request = request;
        ClientHandle = clientHandle;
    }

    /// <summary>What was asked for.</summary>
    public MonitoredItemRequest Request { get; }

    /// <summary>The monitored node.</summary>
    public NodeId NodeId => Request.NodeId;

    /// <summary>The application object from <see cref="MonitoredItemRequest.Tag"/>.</summary>
    public object? Tag => Request.Tag;

    /// <summary>The id the client gave the item; notifications carry it. Unique within the client.</summary>
    public uint ClientHandle { get; }

    /// <summary>Result of creating the item on the server. Bad (for example BadNodeIdUnknown) means it is not monitored.</summary>
    public StatusCode Status { get; internal set; } = StatusCodes.BadWaitingForInitialData;

    /// <summary>The server's id for the item in the current session.</summary>
    public uint ServerId { get; internal set; }

    /// <summary>The sampling interval the server actually uses.</summary>
    public TimeSpan RevisedSamplingInterval { get; internal set; }

    /// <summary>The queue size the server actually uses.</summary>
    public uint RevisedQueueSize { get; internal set; }

    /// <summary>Node and status, for logs.</summary>
    public override string ToString() => $"MonitoredItem({NodeId}, handle {ClientHandle}, {Status})";
}

/// <summary>One value change of a monitored item.</summary>
/// <param name="Item">The item that changed.</param>
/// <param name="Value">The new value with its quality and timestamps.</param>
/// <param name="SequenceNumber">Sequence number of the notification message that carried it.</param>
/// <param name="PublishTime">When the server sent that message.</param>
public readonly record struct DataChange(MonitoredItem Item, DataValue Value, uint SequenceNumber, DateTime PublishTime);

/// <summary>What happened to a subscription.</summary>
public enum SubscriptionEventKind
{
    /// <summary>
    /// After a reconnect the server moved the subscription to the new session. Changes that happened
    /// while the connection was down were fetched again (Republish), unless reported as lost.
    /// </summary>
    Transferred,

    /// <summary>
    /// The subscription could not be transferred (for example the server restarted) and was created
    /// again with all its items. Changes during the outage are gone; each item sends its current value.
    /// </summary>
    Recreated,

    /// <summary>Notification messages could not be recovered; <see cref="SubscriptionEvent.Count"/> says how many.</summary>
    NotificationsLost,

    /// <summary>The server reported a change of the subscription's state (for example BadTimeout when it expired).</summary>
    ServerStatusChange,

    /// <summary>No keep-alive or data arrived within the expected time; the connection is treated as lost.</summary>
    KeepAliveMissed,

    /// <summary>Recreating the subscription failed; the next reconnect tries again.</summary>
    RecoveryFailed,
}

/// <summary>Data of <see cref="OpcUaSubscription.EventOccurred"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Status">Related status, Good when there is none.</param>
/// <param name="Count">How many, for <see cref="SubscriptionEventKind.NotificationsLost"/>; 0 otherwise.</param>
public sealed record SubscriptionEvent(SubscriptionEventKind Kind, StatusCode Status, long Count = 0);
