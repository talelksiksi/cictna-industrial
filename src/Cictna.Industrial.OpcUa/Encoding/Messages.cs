using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.Encoding;

// Service request and response messages exchanged with the native library as OPC UA binary.
// Requests are encoded here and decoded by open62541 in the shim; responses go the other way.
// The encoding ids are the standard DefaultBinary ids of namespace 0 (Part 6, Annex A); the shim
// uses them to pick the open62541 type, so they also act as the message type tag on the boundary.

/// <summary>Standard DefaultBinary encoding ids of the messages and structures used here.</summary>
internal static class EncodingIds
{
    public const uint ReadRequest = 631;
    public const uint ReadResponse = 634;
    public const uint WriteRequest = 673;
    public const uint WriteResponse = 676;
    public const uint BrowseRequest = 527;
    public const uint BrowseResponse = 530;
    public const uint BrowseNextRequest = 533;
    public const uint BrowseNextResponse = 536;
    public const uint GetEndpointsResponse = 431;
    public const uint AddNodesItem = 378;
    public const uint VariableAttributes = 357;
    public const uint ObjectAttributes = 354;
    public const uint WriteValue = 670;
    public const uint ReadValueId = 628;
}

/// <summary>
/// Common request header. open62541 fills in the authentication token, timestamp and request
/// handle when it sends the request, so only the timeout hint is ours to set.
/// </summary>
internal sealed record RequestHeader(uint TimeoutHintMs = 0, uint ReturnDiagnostics = 0)
{
    public void Encode(BinaryEncoder e)
    {
        e.WriteNodeId(NodeId.Null);              // authenticationToken (set by open62541)
        e.WriteDateTime(DateTime.UtcNow);        // timestamp (overwritten by open62541)
        e.WriteUInt32(0);                        // requestHandle (set by open62541)
        e.WriteUInt32(ReturnDiagnostics);
        e.WriteString(null);                     // auditEntryId
        e.WriteUInt32(TimeoutHintMs);
        e.WriteExtensionObject(null);            // additionalHeader
    }
}

internal sealed record ResponseHeader(
    DateTime Timestamp,
    uint RequestHandle,
    StatusCode ServiceResult,
    DiagnosticInfo? ServiceDiagnostics,
    string?[] StringTable)
{
    public static ResponseHeader Decode(BinaryDecoder d)
    {
        var timestamp = d.ReadDateTime();
        var handle = d.ReadUInt32();
        var result = d.ReadStatusCode();
        var diagnostics = d.ReadDiagnosticInfo();
        var table = d.ReadArray(x => x.ReadString()) ?? [];
        d.ReadExtensionObject(); // additionalHeader, not used
        return new ResponseHeader(timestamp, handle, result, diagnostics, table);
    }

    public void Encode(BinaryEncoder e)
    {
        e.WriteDateTime(Timestamp);
        e.WriteUInt32(RequestHandle);
        e.WriteStatusCode(ServiceResult);
        e.WriteDiagnosticInfo(ServiceDiagnostics);
        e.WriteArray(StringTable, (x, s) => x.WriteString(s));
        e.WriteExtensionObject(null);
    }
}

internal sealed record ReadRequest(RequestHeader Header, double MaxAgeMs, TimestampsToReturn Timestamps, IReadOnlyList<ReadValueId> NodesToRead)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteDouble(MaxAgeMs);
        e.WriteEnum(Timestamps);
        e.WriteArray(NodesToRead, (x, n) => n.Encode(x));
        return e.ToArray();
    }
}

internal sealed record ReadResponse(ResponseHeader Header, DataValue[] Results, DiagnosticInfo?[] DiagnosticInfos)
{
    public static ReadResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadArray(x => x.ReadDataValue()) ?? [], d.ReadArray(x => x.ReadDiagnosticInfo()) ?? []);
}

internal sealed record WriteRequest(RequestHeader Header, IReadOnlyList<WriteValue> NodesToWrite)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteArray(NodesToWrite, (x, n) => n.Encode(x));
        return e.ToArray();
    }
}

internal sealed record WriteResponse(ResponseHeader Header, StatusCode[] Results, DiagnosticInfo?[] DiagnosticInfos)
{
    public static WriteResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadArray(x => x.ReadStatusCode()) ?? [], d.ReadArray(x => x.ReadDiagnosticInfo()) ?? []);
}

internal sealed record BrowseRequest(RequestHeader Header, uint MaxReferencesPerNode, IReadOnlyList<BrowseDescription> NodesToBrowse)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        // ViewDescription: the whole address space (null view, no timestamp, version 0).
        e.WriteNodeId(NodeId.Null);
        e.WriteDateTime(DateTime.MinValue);
        e.WriteUInt32(0);
        e.WriteUInt32(MaxReferencesPerNode);
        e.WriteArray(NodesToBrowse, (x, n) => n.Encode(x));
        return e.ToArray();
    }
}

internal sealed record BrowseNextRequest(RequestHeader Header, bool ReleaseContinuationPoints, IReadOnlyList<byte[]> ContinuationPoints)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        Header.Encode(e);
        e.WriteBoolean(ReleaseContinuationPoints);
        e.WriteArray(ContinuationPoints, (x, c) => x.WriteByteString(c));
        return e.ToArray();
    }
}

/// <summary>One BrowseResult as it comes off the wire, before continuation points are followed.</summary>
internal sealed record RawBrowseResult(StatusCode Status, byte[]? ContinuationPoint, ReferenceDescription[] References)
{
    public static RawBrowseResult Decode(BinaryDecoder d) =>
        new(d.ReadStatusCode(), d.ReadByteString(), d.ReadArray(ReferenceDescription.Decode) ?? []);
}

/// <summary>BrowseResponse and BrowseNextResponse have the same layout.</summary>
internal sealed record BrowseResponse(ResponseHeader Header, RawBrowseResult[] Results, DiagnosticInfo?[] DiagnosticInfos)
{
    public static BrowseResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadArray(RawBrowseResult.Decode) ?? [], d.ReadArray(x => x.ReadDiagnosticInfo()) ?? []);
}

internal sealed record GetEndpointsResponse(ResponseHeader Header, EndpointDescription[] Endpoints)
{
    public static GetEndpointsResponse Decode(BinaryDecoder d) =>
        new(ResponseHeader.Decode(d), d.ReadArray(EndpointDescription.Decode) ?? []);
}

/// <summary>Attributes of a new variable node (server side). Only the attributes we set are flagged.</summary>
internal sealed record VariableAttributes(
    LocalizedText DisplayName,
    LocalizedText Description,
    Variant Value,
    NodeId DataType,
    int ValueRank,
    uint[]? ArrayDimensions,
    byte AccessLevel,
    double MinimumSamplingInterval = 0)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        e.WriteUInt32(SpecifiedMask);
        e.WriteLocalizedText(DisplayName);
        e.WriteLocalizedText(Description);
        e.WriteUInt32(0);                  // writeMask
        e.WriteUInt32(0);                  // userWriteMask
        e.WriteVariant(Value);
        e.WriteNodeId(DataType);
        e.WriteInt32(ValueRank);
        e.WriteArray(ArrayDimensions, (x, v) => x.WriteUInt32(v));
        e.WriteByte(AccessLevel);
        e.WriteByte(AccessLevel);          // userAccessLevel = accessLevel
        e.WriteDouble(MinimumSamplingInterval);
        e.WriteBoolean(false);             // historizing
        return e.ToArray();
    }

    /// <summary>NodeAttributesMask bits (Part 4, NodeAttributesMask) for the attributes set above.</summary>
    internal const uint SpecifiedMask =
        0x00000001 | // AccessLevel
        0x00000002 | // ArrayDimensions
        0x00000010 | // DataType
        0x00000020 | // Description
        0x00000040 | // DisplayName
        0x00001000 | // MinimumSamplingInterval
        0x00010000 | // UserAccessLevel
        0x00080000 | // ValueRank
        0x00200000;  // Value
}

/// <summary>Attributes of a new object node (server side), used for folders.</summary>
internal sealed record ObjectAttributes(LocalizedText DisplayName, LocalizedText Description)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        e.WriteUInt32(0x00000020 | 0x00000040); // Description | DisplayName
        e.WriteLocalizedText(DisplayName);
        e.WriteLocalizedText(Description);
        e.WriteUInt32(0);  // writeMask
        e.WriteUInt32(0);  // userWriteMask
        e.WriteByte(0);    // eventNotifier
        return e.ToArray();
    }
}

/// <summary>Request to add one node (server side).</summary>
internal sealed record AddNodesItem(
    NodeId ParentNodeId,
    NodeId ReferenceTypeId,
    NodeId RequestedNewNodeId,
    QualifiedName BrowseName,
    NodeClass NodeClass,
    ExtensionObject NodeAttributes,
    NodeId TypeDefinition)
{
    public byte[] Encode()
    {
        var e = new BinaryEncoder();
        e.WriteExpandedNodeId(ParentNodeId);
        e.WriteNodeId(ReferenceTypeId);
        e.WriteExpandedNodeId(RequestedNewNodeId);
        e.WriteQualifiedName(BrowseName);
        e.WriteInt32((int)NodeClass);
        e.WriteExtensionObject(NodeAttributes);
        e.WriteExpandedNodeId(TypeDefinition);
        return e.ToArray();
    }
}
