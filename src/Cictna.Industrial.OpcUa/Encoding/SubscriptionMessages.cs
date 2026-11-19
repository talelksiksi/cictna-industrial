using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.Encoding;

// Subscription and MonitoredItem service sets (OPC UA Part 4, sections 5.12 and 5.13) as
// OPC UA binary. Requests are encoded here and decoded by open62541 in the shim; responses come
// back the other way. Every message also has the opposite direction (Encode on responses, Decode
// on requests) so the differential tests can check both against open62541 byte for byte.
//
// Field order follows Part 4 exactly; a wrong order would still "work" against our own decoder,
// which is why every message is round-tripped through open62541 in the tests.

/// <summary>Standard DefaultBinary encoding ids of the subscription messages (Part 6, Annex A).</summary>
internal static class SubscriptionEncodingIds
{
    public const uint CreateSubscriptionRequest = 787;
    public const uint CreateSubscriptionResponse = 790;
    public const uint ModifySubscriptionRequest = 793;
    public const uint ModifySubscriptionResponse = 796;
    public const uint SetPublishingModeRequest = 799;
    public const uint SetPublishingModeResponse = 802;
    public const uint PublishRequest = 826;
    public const uint PublishResponse = 829;
    public const uint RepublishRequest = 832;
    public const uint RepublishResponse = 835;
    public const uint TransferSubscriptionsRequest = 841;
    public const uint TransferSubscriptionsResponse = 844;
    public const uint DeleteSubscriptionsRequest = 847;
    public const uint DeleteSubscriptionsResponse = 850;
    public const uint CreateMonitoredItemsRequest = 751;
    public const uint CreateMonitoredItemsResponse = 754;
    public const uint DeleteMonitoredItemsRequest = 781;
    public const uint DeleteMonitoredItemsResponse = 784;

    // Notification bodies inside NotificationMessage.notificationData, and the data change filter.
    public const uint DataChangeNotification = 811;
    public const uint StatusChangeNotification = 820;
    public const uint EventNotificationList = 916;
    public const uint DataChangeFilter = 724;
    public const uint NotificationMessage = 805;
}

// ---- subscriptions -----------------------------------------------------------------------

internal sealed record CreateSubscriptionRequest(
    RequestHeader Header,
    double RequestedPublishingInterval,
    uint RequestedLifetimeCount,
    uint RequestedMaxKeepAliveCount,
    uint MaxNotificationsPerPublish,
    bool PublishingEnabled,
    byte Priority)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteDouble(RequestedPublishingInterval);
        e.WriteUInt32(RequestedLifetimeCount);
        e.WriteUInt32(RequestedMaxKeepAliveCount);
        e.WriteUInt32(MaxNotificationsPerPublish);
        e.WriteBoolean(PublishingEnabled);
        e.WriteByte(Priority);
        return e.ToArray();
    }
}

internal sealed record CreateSubscriptionResponse(
    ResponseHeader Header,
    uint SubscriptionId,
    double RevisedPublishingInterval,
    uint RevisedLifetimeCount,
    uint RevisedMaxKeepAliveCount)
{
    public static CreateSubscriptionResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadUInt32(), d.ReadDouble(), d.ReadUInt32(), d.ReadUInt32());

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteUInt32(SubscriptionId);
        e.WriteDouble(RevisedPublishingInterval);
        e.WriteUInt32(RevisedLifetimeCount);
        e.WriteUInt32(RevisedMaxKeepAliveCount);
        return e.ToArray();
    }
}

internal sealed record ModifySubscriptionRequest(
    RequestHeader Header,
    uint SubscriptionId,
    double RequestedPublishingInterval,
    uint RequestedLifetimeCount,
    uint RequestedMaxKeepAliveCount,
    uint MaxNotificationsPerPublish,
    byte Priority)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteUInt32(SubscriptionId);
        e.WriteDouble(RequestedPublishingInterval);
        e.WriteUInt32(RequestedLifetimeCount);
        e.WriteUInt32(RequestedMaxKeepAliveCount);
        e.WriteUInt32(MaxNotificationsPerPublish);
        e.WriteByte(Priority);
        return e.ToArray();
    }
}

internal sealed record ModifySubscriptionResponse(
    ResponseHeader Header,
    double RevisedPublishingInterval,
    uint RevisedLifetimeCount,
    uint RevisedMaxKeepAliveCount)
{
    public static ModifySubscriptionResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadDouble(), d.ReadUInt32(), d.ReadUInt32());

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteDouble(RevisedPublishingInterval);
        e.WriteUInt32(RevisedLifetimeCount);
        e.WriteUInt32(RevisedMaxKeepAliveCount);
        return e.ToArray();
    }
}

internal sealed record SetPublishingModeRequest(RequestHeader Header, bool PublishingEnabled, IReadOnlyList<uint> SubscriptionIds)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteBoolean(PublishingEnabled);
        e.WriteArray(SubscriptionIds, (x, id) => x.WriteUInt32(id));
        return e.ToArray();
    }
}

internal sealed record DeleteSubscriptionsRequest(RequestHeader Header, IReadOnlyList<uint> SubscriptionIds)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteArray(SubscriptionIds, (x, id) => x.WriteUInt32(id));
        return e.ToArray();
    }
}

/// <summary>
/// Response layout shared by SetPublishingMode, DeleteSubscriptions and DeleteMonitoredItems:
/// a header, one StatusCode per item, optional diagnostics.
/// </summary>
internal sealed record StatusResultsResponse(ResponseHeader Header, StatusCode[] Results, DiagnosticInfo?[] DiagnosticInfos)
{
    public static StatusResultsResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadArray(x => x.ReadStatusCode()) ?? [], d.ReadArray(x => x.ReadDiagnosticInfo()) ?? []);

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteArray(Results, (x, s) => x.WriteStatusCode(s));
        e.WriteArray(DiagnosticInfos, (x, s) => x.WriteDiagnosticInfo(s));
        return e.ToArray();
    }
}

internal sealed record TransferSubscriptionsRequest(RequestHeader Header, IReadOnlyList<uint> SubscriptionIds, bool SendInitialValues)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteArray(SubscriptionIds, (x, id) => x.WriteUInt32(id));
        e.WriteBoolean(SendInitialValues);
        return e.ToArray();
    }
}

/// <summary>Result of transferring one subscription: status and the sequence numbers still available for Republish.</summary>
internal sealed record TransferResult(StatusCode Status, uint[] AvailableSequenceNumbers)
{
    public static TransferResult Decode(BinaryDecoder d) => new(d.ReadStatusCode(), d.ReadArray(x => x.ReadUInt32()) ?? []);

    public void Encode(BinaryEncoder e)
    {
        e.WriteStatusCode(Status);
        e.WriteArray(AvailableSequenceNumbers, (x, n) => x.WriteUInt32(n));
    }
}

internal sealed record TransferSubscriptionsResponse(ResponseHeader Header, TransferResult[] Results, DiagnosticInfo?[] DiagnosticInfos)
{
    public static TransferSubscriptionsResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadArray(TransferResult.Decode) ?? [], d.ReadArray(x => x.ReadDiagnosticInfo()) ?? []);

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteArray(Results, (x, r) => r.Encode(x));
        e.WriteArray(DiagnosticInfos, (x, s) => x.WriteDiagnosticInfo(s));
        return e.ToArray();
    }
}

// ---- monitored items ---------------------------------------------------------------------

/// <summary>MonitoringParameters (Part 4, 7.21). The client handle is how notifications find their item.</summary>
internal sealed record MonitoringParametersMessage(uint ClientHandle, double SamplingInterval, ExtensionObject Filter, uint QueueSize, bool DiscardOldest)
{
    public void Encode(BinaryEncoder e)
    {
        e.WriteUInt32(ClientHandle);
        e.WriteDouble(SamplingInterval);
        e.WriteExtensionObject(Filter);
        e.WriteUInt32(QueueSize);
        e.WriteBoolean(DiscardOldest);
    }
}

/// <summary>MonitoredItemCreateRequest (Part 4, 7.21).</summary>
internal sealed record MonitoredItemCreateRequest(ReadValueId ItemToMonitor, MonitoringMode MonitoringMode, MonitoringParametersMessage Parameters)
{
    public void Encode(BinaryEncoder e)
    {
        ItemToMonitor.Encode(e);
        e.WriteInt32((int)MonitoringMode);
        Parameters.Encode(e);
    }
}

internal sealed record CreateMonitoredItemsRequest(
    RequestHeader Header,
    uint SubscriptionId,
    TimestampsToReturn TimestampsToReturn,
    IReadOnlyList<MonitoredItemCreateRequest> ItemsToCreate)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteUInt32(SubscriptionId);
        e.WriteEnum(TimestampsToReturn);
        e.WriteArray(ItemsToCreate, (x, i) => i.Encode(x));
        return e.ToArray();
    }
}

internal sealed record MonitoredItemCreateResult(StatusCode Status, uint MonitoredItemId, double RevisedSamplingInterval, uint RevisedQueueSize, ExtensionObject FilterResult)
{
    public static MonitoredItemCreateResult Decode(BinaryDecoder d) =>
        new(d.ReadStatusCode(), d.ReadUInt32(), d.ReadDouble(), d.ReadUInt32(), d.ReadExtensionObject());

    public void Encode(BinaryEncoder e)
    {
        e.WriteStatusCode(Status);
        e.WriteUInt32(MonitoredItemId);
        e.WriteDouble(RevisedSamplingInterval);
        e.WriteUInt32(RevisedQueueSize);
        e.WriteExtensionObject(FilterResult);
    }
}

internal sealed record CreateMonitoredItemsResponse(ResponseHeader Header, MonitoredItemCreateResult[] Results, DiagnosticInfo?[] DiagnosticInfos)
{
    public static CreateMonitoredItemsResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadArray(MonitoredItemCreateResult.Decode) ?? [], d.ReadArray(x => x.ReadDiagnosticInfo()) ?? []);

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteArray(Results, (x, r) => r.Encode(x));
        e.WriteArray(DiagnosticInfos, (x, s) => x.WriteDiagnosticInfo(s));
        return e.ToArray();
    }
}

internal sealed record DeleteMonitoredItemsRequest(RequestHeader Header, uint SubscriptionId, IReadOnlyList<uint> MonitoredItemIds)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteUInt32(SubscriptionId);
        e.WriteArray(MonitoredItemIds, (x, id) => x.WriteUInt32(id));
        return e.ToArray();
    }
}

/// <summary>DataChangeFilter (Part 4, 7.22.2), sent inside MonitoringParameters.filter.</summary>
internal sealed record DataChangeFilterMessage(DataChangeTrigger Trigger, DeadbandType DeadbandType, double DeadbandValue)
{
    public ExtensionObject ToExtensionObject()
    {
        var e = new BinaryEncoder();
        e.WriteInt32((int)Trigger);
        e.WriteUInt32((uint)DeadbandType);
        e.WriteDouble(DeadbandValue);
        return ExtensionObject.FromBinary(new NodeId(SubscriptionEncodingIds.DataChangeFilter), e.ToArray());
    }
}

// ---- publish -----------------------------------------------------------------------------

/// <summary>Acknowledges one received notification message, so the server can drop it from its retransmission queue.</summary>
internal readonly record struct SubscriptionAcknowledgement(uint SubscriptionId, uint SequenceNumber);

internal sealed record PublishRequest(RequestHeader Header, IReadOnlyList<SubscriptionAcknowledgement> Acknowledgements)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteArray(Acknowledgements, (x, a) =>
        {
            x.WriteUInt32(a.SubscriptionId);
            x.WriteUInt32(a.SequenceNumber);
        });
        return e.ToArray();
    }
}

/// <summary>
/// NotificationMessage (Part 4, 7.25). An empty <see cref="NotificationData"/> is a keep-alive;
/// its sequence number is the next one the server will use, not a new message.
/// </summary>
internal sealed record NotificationMessage(uint SequenceNumber, DateTime PublishTime, ExtensionObject[] NotificationData)
{
    public bool IsKeepAlive => NotificationData.Length == 0;

    public static NotificationMessage Decode(BinaryDecoder d) =>
        new(d.ReadUInt32(), d.ReadDateTime(), d.ReadArray(x => x.ReadExtensionObject()) ?? []);

    public void Encode(BinaryEncoder e)
    {
        e.WriteUInt32(SequenceNumber);
        e.WriteDateTime(PublishTime);
        e.WriteArray(NotificationData, (x, n) => x.WriteExtensionObject(n));
    }
}

internal sealed record PublishResponse(
    ResponseHeader Header,
    uint SubscriptionId,
    uint[] AvailableSequenceNumbers,
    bool MoreNotifications,
    NotificationMessage NotificationMessage,
    StatusCode[] Results,
    DiagnosticInfo?[] DiagnosticInfos)
{
    public static PublishResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadUInt32(), d.ReadArray(x => x.ReadUInt32()) ?? [], d.ReadBoolean(),
            NotificationMessage.Decode(d), d.ReadArray(x => x.ReadStatusCode()) ?? [], d.ReadArray(x => x.ReadDiagnosticInfo()) ?? []);

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteUInt32(SubscriptionId);
        e.WriteArray(AvailableSequenceNumbers, (x, n) => x.WriteUInt32(n));
        e.WriteBoolean(MoreNotifications);
        NotificationMessage.Encode(e);
        e.WriteArray(Results, (x, s) => x.WriteStatusCode(s));
        e.WriteArray(DiagnosticInfos, (x, s) => x.WriteDiagnosticInfo(s));
        return e.ToArray();
    }
}

internal sealed record RepublishRequest(RequestHeader Header, uint SubscriptionId, uint RetransmitSequenceNumber)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteUInt32(SubscriptionId);
        e.WriteUInt32(RetransmitSequenceNumber);
        return e.ToArray();
    }
}

internal sealed record RepublishResponse(ResponseHeader Header, NotificationMessage NotificationMessage)
{
    public static RepublishResponse Decode(BinaryDecoder d) => new(ResponseHeader.Decode(d), NotificationMessage.Decode(d));

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        NotificationMessage.Encode(e);
        return e.ToArray();
    }
}

// ---- notification bodies -----------------------------------------------------------------

/// <summary>One value change of one monitored item, identified by the client handle we gave it.</summary>
internal readonly record struct MonitoredItemNotificationMessage(uint ClientHandle, DataValue Value);

/// <summary>DataChangeNotification (Part 4, 7.20.2).</summary>
internal sealed record DataChangeNotificationMessage(MonitoredItemNotificationMessage[] MonitoredItems, DiagnosticInfo?[] DiagnosticInfos)
{
    public static DataChangeNotificationMessage Decode(BinaryDecoder d) =>
        new(d.ReadArray(x => new MonitoredItemNotificationMessage(x.ReadUInt32(), x.ReadDataValue())) ?? [],
            d.ReadArray(x => x.ReadDiagnosticInfo()) ?? []);

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        e.WriteArray(MonitoredItems, (x, m) =>
        {
            x.WriteUInt32(m.ClientHandle);
            x.WriteDataValue(m.Value);
        });
        e.WriteArray(DiagnosticInfos, (x, s) => x.WriteDiagnosticInfo(s));
        return e.ToArray();
    }
}

/// <summary>StatusChangeNotification (Part 4, 7.20.4): the server reports the subscription's own state, e.g. BadTimeout when it expired.</summary>
internal sealed record StatusChangeNotificationMessage(StatusCode Status, DiagnosticInfo? DiagnosticInfo)
{
    public static StatusChangeNotificationMessage Decode(BinaryDecoder d) => new(d.ReadStatusCode(), d.ReadDiagnosticInfo());

    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        e.WriteStatusCode(Status);
        e.WriteDiagnosticInfo(DiagnosticInfo);
        return e.ToArray();
    }
}
