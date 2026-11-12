using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;

namespace Cictna.Industrial.OpcUa;

// Public OPC UA structures used by the client API. Each type carries its own binary
// encode/decode (internal), written field by field in the order of Part 4 and Part 6.

/// <summary>Node attributes that can be read or written (Part 3, 5.9). Values are the wire ids.</summary>
public enum AttributeId : uint
{
    /// <summary>NodeId of the node.</summary>
    NodeId = 1,
    /// <summary>Class of the node (Object, Variable...).</summary>
    NodeClass = 2,
    /// <summary>Browse name.</summary>
    BrowseName = 3,
    /// <summary>Display name.</summary>
    DisplayName = 4,
    /// <summary>Description.</summary>
    Description = 5,
    /// <summary>Which attributes are writable.</summary>
    WriteMask = 6,
    /// <summary>Which attributes the current user may write.</summary>
    UserWriteMask = 7,
    /// <summary>Abstract type flag.</summary>
    IsAbstract = 8,
    /// <summary>Symmetric reference flag.</summary>
    Symmetric = 9,
    /// <summary>Inverse name of a reference type.</summary>
    InverseName = 10,
    /// <summary>View without loops flag.</summary>
    ContainsNoLoops = 11,
    /// <summary>Event notifier flags.</summary>
    EventNotifier = 12,
    /// <summary>The value of a variable. The usual attribute to read and write.</summary>
    Value = 13,
    /// <summary>Data type of a variable.</summary>
    DataType = 14,
    /// <summary>Scalar (-1), array (1) and so on.</summary>
    ValueRank = 15,
    /// <summary>Array dimensions.</summary>
    ArrayDimensions = 16,
    /// <summary>Read/write access of the variable.</summary>
    AccessLevel = 17,
    /// <summary>Read/write access for the current user.</summary>
    UserAccessLevel = 18,
    /// <summary>Fastest sampling interval the server supports.</summary>
    MinimumSamplingInterval = 19,
    /// <summary>Whether the server stores history.</summary>
    Historizing = 20,
    /// <summary>Whether a method can be called.</summary>
    Executable = 21,
    /// <summary>Whether the current user can call the method.</summary>
    UserExecutable = 22,
    /// <summary>Structure or enumeration definition of a data type.</summary>
    DataTypeDefinition = 23,
    /// <summary>Role permissions.</summary>
    RolePermissions = 24,
    /// <summary>Role permissions of the current user.</summary>
    UserRolePermissions = 25,
    /// <summary>Access restrictions.</summary>
    AccessRestrictions = 26,
    /// <summary>Extended access level.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Name defined by the OPC UA specification.")]
    AccessLevelEx = 27,
}

/// <summary>Class of a node. Also used as a bit mask filter when browsing (0 = all classes).</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Member names are defined by the OPC UA specification.")]
public enum NodeClass
{
    /// <summary>Not specified (in a filter: all classes).</summary>
    Unspecified = 0,
    /// <summary>Object (folders, devices...).</summary>
    Object = 1,
    /// <summary>Variable (tags with values).</summary>
    Variable = 2,
    /// <summary>Method.</summary>
    Method = 4,
    /// <summary>Object type.</summary>
    ObjectType = 8,
    /// <summary>Variable type.</summary>
    VariableType = 16,
    /// <summary>Reference type.</summary>
    ReferenceType = 32,
    /// <summary>Data type.</summary>
    DataType = 64,
    /// <summary>View.</summary>
    View = 128,
}

/// <summary>Which references to follow when browsing.</summary>
public enum BrowseDirection
{
    /// <summary>From the node to its targets (children).</summary>
    Forward = 0,
    /// <summary>From targets back to the node (parents).</summary>
    Inverse = 1,
    /// <summary>Both directions.</summary>
    Both = 2,
}

/// <summary>Which timestamps a read should return.</summary>
public enum TimestampsToReturn
{
    /// <summary>Source timestamp only.</summary>
    Source = 0,
    /// <summary>Server timestamp only.</summary>
    Server = 1,
    /// <summary>Both timestamps (the default of this client).</summary>
    Both = 2,
    /// <summary>No timestamps.</summary>
    Neither = 3,
}

/// <summary>Security of the channel between client and server.</summary>
public enum MessageSecurityMode
{
    /// <summary>Not set.</summary>
    Invalid = 0,
    /// <summary>No signing, no encryption. For trusted networks and tests only.</summary>
    None = 1,
    /// <summary>Messages are signed (tamper-proof) but readable on the network.</summary>
    Sign = 2,
    /// <summary>Messages are signed and encrypted. The normal choice for production.</summary>
    SignAndEncrypt = 3,
}

/// <summary>Kind of OPC UA application.</summary>
public enum ApplicationType
{
    /// <summary>Server.</summary>
    Server = 0,
    /// <summary>Client.</summary>
    Client = 1,
    /// <summary>Both client and server.</summary>
    ClientAndServer = 2,
    /// <summary>Discovery server.</summary>
    DiscoveryServer = 3,
}

/// <summary>Kind of user identity a server accepts.</summary>
public enum UserTokenType
{
    /// <summary>No user (anonymous).</summary>
    Anonymous = 0,
    /// <summary>User name and password.</summary>
    UserName = 1,
    /// <summary>X.509 user certificate.</summary>
    Certificate = 2,
    /// <summary>Token from an external issuer.</summary>
    IssuedToken = 3,
}

/// <summary>Well-known security policy URIs.</summary>
public static class SecurityPolicies
{
    /// <summary>No security.</summary>
    public const string None = "http://opcfoundation.org/UA/SecurityPolicy#None";

    /// <summary>RSA 2048+ with SHA-256. The widely supported default for secure connections.</summary>
    public const string Basic256Sha256 = "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256";

    /// <summary>RSA with OAEP-SHA256 and PSS signatures (newer servers).</summary>
    public const string Aes256Sha256RsaPss = "http://opcfoundation.org/UA/SecurityPolicy#Aes256_Sha256_RsaPss";

    /// <summary>AES-128 with SHA-256 and RSA-OAEP (newer servers).</summary>
    public const string Aes128Sha256RsaOaep = "http://opcfoundation.org/UA/SecurityPolicy#Aes128_Sha256_RsaOaep";
}

/// <summary>Well-known nodes of namespace 0 used by the client.</summary>
public static class WellKnownNodes
{
    /// <summary>Root of the address space.</summary>
    public static NodeId RootFolder => new(84);
    /// <summary>Objects folder: where a server's devices and tags live.</summary>
    public static NodeId ObjectsFolder => new(85);
    /// <summary>The Server object.</summary>
    public static NodeId Server => new(2253);
    /// <summary>Server_NamespaceArray: namespace URIs indexed by namespace index.</summary>
    public static NodeId NamespaceArray => new(2255);
    /// <summary>Server_ServerStatus_CurrentTime.</summary>
    public static NodeId ServerCurrentTime => new(2258);
    /// <summary>Server_ServerStatus_State.</summary>
    public static NodeId ServerState => new(2259);
    /// <summary>HierarchicalReferences: the default reference type for browsing.</summary>
    public static NodeId HierarchicalReferences => new(33);
    /// <summary>Organizes reference type.</summary>
    public static NodeId Organizes => new(35);
    /// <summary>HasComponent reference type.</summary>
    public static NodeId HasComponent => new(47);
    /// <summary>HasTypeDefinition reference type.</summary>
    public static NodeId HasTypeDefinition => new(40);
    /// <summary>FolderType.</summary>
    public static NodeId FolderType => new(61);
    /// <summary>BaseDataVariableType.</summary>
    public static NodeId BaseDataVariableType => new(63);
    /// <summary>OperationLimits_MaxNodesPerRead.</summary>
    public static NodeId MaxNodesPerRead => new(11705);
    /// <summary>OperationLimits_MaxNodesPerWrite.</summary>
    public static NodeId MaxNodesPerWrite => new(11707);
    /// <summary>OperationLimits_MaxNodesPerBrowse.</summary>
    public static NodeId MaxNodesPerBrowse => new(11710);
}

/// <summary>One node attribute to read.</summary>
/// <param name="NodeId">The node.</param>
/// <param name="AttributeId">The attribute; the value by default.</param>
/// <param name="IndexRange">Optional sub-range of an array value, for example "2:5".</param>
public sealed record ReadValueId(NodeId NodeId, AttributeId AttributeId = AttributeId.Value, string? IndexRange = null)
{
    internal void Encode(BinaryEncoder e)
    {
        e.WriteNodeId(NodeId);
        e.WriteUInt32((uint)AttributeId);
        e.WriteString(IndexRange);
        e.WriteQualifiedName(default); // DataEncoding: default (binary)
    }

    internal static ReadValueId Decode(BinaryDecoder d)
    {
        var node = d.ReadNodeId();
        var attr = (AttributeId)d.ReadUInt32();
        var range = d.ReadString();
        d.ReadQualifiedName();
        return new ReadValueId(node, attr, range);
    }
}

/// <summary>One node attribute to write.</summary>
/// <param name="NodeId">The node.</param>
/// <param name="Value">The value to write. Most servers reject writes that carry status or timestamps; leave them default.</param>
/// <param name="AttributeId">The attribute; the value by default.</param>
/// <param name="IndexRange">Optional sub-range of an array value.</param>
public sealed record WriteValue(NodeId NodeId, DataValue Value, AttributeId AttributeId = AttributeId.Value, string? IndexRange = null)
{
    internal void Encode(BinaryEncoder e)
    {
        e.WriteNodeId(NodeId);
        e.WriteUInt32((uint)AttributeId);
        e.WriteString(IndexRange);
        e.WriteDataValue(Value);
    }

    internal static WriteValue Decode(BinaryDecoder d)
    {
        var node = d.ReadNodeId();
        var attr = (AttributeId)d.ReadUInt32();
        var range = d.ReadString();
        return new WriteValue(node, d.ReadDataValue(), attr, range);
    }
}

/// <summary>What to browse from one node.</summary>
/// <param name="NodeId">The starting node.</param>
/// <param name="Direction">Which direction to follow references.</param>
/// <param name="ReferenceTypeId">Reference type to follow; HierarchicalReferences when null.</param>
/// <param name="IncludeSubtypes">Also follow subtypes of the reference type.</param>
/// <param name="NodeClassMask">Only return these node classes; Unspecified returns all.</param>
public sealed record BrowseDescription(
    NodeId NodeId,
    BrowseDirection Direction = BrowseDirection.Forward,
    NodeId? ReferenceTypeId = null,
    bool IncludeSubtypes = true,
    NodeClass NodeClassMask = NodeClass.Unspecified)
{
    internal void Encode(BinaryEncoder e)
    {
        e.WriteNodeId(NodeId);
        e.WriteEnum(Direction);
        e.WriteNodeId(ReferenceTypeId ?? WellKnownNodes.HierarchicalReferences);
        e.WriteBoolean(IncludeSubtypes);
        e.WriteUInt32((uint)NodeClassMask);
        e.WriteUInt32(0x3F); // ResultMask: all fields of ReferenceDescription
    }
}

/// <summary>One reference found by browsing: the target node and how it is connected.</summary>
/// <param name="ReferenceTypeId">Type of the reference (Organizes, HasComponent...).</param>
/// <param name="IsForward">True when the reference points from the browsed node to the target.</param>
/// <param name="NodeId">The target node.</param>
/// <param name="BrowseName">Browse name of the target.</param>
/// <param name="DisplayName">Display name of the target.</param>
/// <param name="NodeClass">Class of the target.</param>
/// <param name="TypeDefinition">Type definition of the target (objects and variables).</param>
public sealed record ReferenceDescription(
    NodeId ReferenceTypeId,
    bool IsForward,
    ExpandedNodeId NodeId,
    QualifiedName BrowseName,
    LocalizedText DisplayName,
    NodeClass NodeClass,
    ExpandedNodeId TypeDefinition)
{
    internal static ReferenceDescription Decode(BinaryDecoder d) => new(
        d.ReadNodeId(), d.ReadBoolean(), d.ReadExpandedNodeId(), d.ReadQualifiedName(),
        d.ReadLocalizedText(), (NodeClass)d.ReadInt32(), d.ReadExpandedNodeId());

    internal void Encode(BinaryEncoder e)
    {
        e.WriteNodeId(ReferenceTypeId);
        e.WriteBoolean(IsForward);
        e.WriteExpandedNodeId(NodeId);
        e.WriteQualifiedName(BrowseName);
        e.WriteLocalizedText(DisplayName);
        e.WriteInt32((int)NodeClass);
        e.WriteExpandedNodeId(TypeDefinition);
    }
}

/// <summary>Result of browsing one node: all references, with continuation points already followed.</summary>
/// <param name="Status">Good, or why browsing this node failed.</param>
/// <param name="References">The references found.</param>
public sealed record BrowseResult(StatusCode Status, IReadOnlyList<ReferenceDescription> References);

/// <summary>Describes an OPC UA application (server or client).</summary>
/// <param name="ApplicationUri">Globally unique URI of the application; must match its certificate.</param>
/// <param name="ProductUri">URI of the product.</param>
/// <param name="ApplicationName">Human-readable name.</param>
/// <param name="ApplicationType">Server, client or both.</param>
/// <param name="GatewayServerUri">Gateway, when reached through one.</param>
/// <param name="DiscoveryProfileUri">Discovery profile.</param>
/// <param name="DiscoveryUrls">URLs where the application can be discovered.</param>
public sealed record ApplicationDescription(
    string? ApplicationUri,
    string? ProductUri,
    LocalizedText ApplicationName,
    ApplicationType ApplicationType,
    string? GatewayServerUri,
    string? DiscoveryProfileUri,
    IReadOnlyList<string?> DiscoveryUrls)
{
    internal static ApplicationDescription Decode(BinaryDecoder d) => new(
        d.ReadString(), d.ReadString(), d.ReadLocalizedText(), (ApplicationType)d.ReadInt32(),
        d.ReadString(), d.ReadString(), d.ReadArray(x => x.ReadString()) ?? []);

    internal void Encode(BinaryEncoder e)
    {
        e.WriteString(ApplicationUri);
        e.WriteString(ProductUri);
        e.WriteLocalizedText(ApplicationName);
        e.WriteInt32((int)ApplicationType);
        e.WriteString(GatewayServerUri);
        e.WriteString(DiscoveryProfileUri);
        e.WriteArray(DiscoveryUrls, (x, s) => x.WriteString(s));
    }
}

/// <summary>A kind of user identity an endpoint accepts.</summary>
/// <param name="PolicyId">Id the client must quote when using this policy.</param>
/// <param name="TokenType">Anonymous, user name, certificate or issued token.</param>
/// <param name="IssuedTokenType">Token type for issued tokens.</param>
/// <param name="IssuerEndpointUrl">Issuer for issued tokens.</param>
/// <param name="SecurityPolicyUri">Policy used to protect the token; null means the channel's.</param>
public sealed record UserTokenPolicy(
    string? PolicyId,
    UserTokenType TokenType,
    string? IssuedTokenType,
    string? IssuerEndpointUrl,
    string? SecurityPolicyUri)
{
    internal static UserTokenPolicy Decode(BinaryDecoder d) => new(
        d.ReadString(), (UserTokenType)d.ReadInt32(), d.ReadString(), d.ReadString(), d.ReadString());

    internal void Encode(BinaryEncoder e)
    {
        e.WriteString(PolicyId);
        e.WriteInt32((int)TokenType);
        e.WriteString(IssuedTokenType);
        e.WriteString(IssuerEndpointUrl);
        e.WriteString(SecurityPolicyUri);
    }
}

/// <summary>One way to connect to a server: URL, security and accepted user identities.</summary>
/// <param name="EndpointUrl">The URL to connect to.</param>
/// <param name="Server">The server application.</param>
/// <param name="ServerCertificate">The server's certificate (DER), when the endpoint is secured.</param>
/// <param name="SecurityMode">None, Sign or SignAndEncrypt.</param>
/// <param name="SecurityPolicyUri">Algorithms, see <see cref="SecurityPolicies"/>.</param>
/// <param name="UserIdentityTokens">User identities accepted on this endpoint.</param>
/// <param name="TransportProfileUri">Transport profile (normally UA TCP binary).</param>
/// <param name="SecurityLevel">Relative strength the server assigns to this endpoint (higher is stronger).</param>
public sealed record EndpointDescription(
    string? EndpointUrl,
    ApplicationDescription Server,
    byte[]? ServerCertificate,
    MessageSecurityMode SecurityMode,
    string? SecurityPolicyUri,
    IReadOnlyList<UserTokenPolicy> UserIdentityTokens,
    string? TransportProfileUri,
    byte SecurityLevel)
{
    internal static EndpointDescription Decode(BinaryDecoder d) => new(
        d.ReadString(), ApplicationDescription.Decode(d), d.ReadByteString(), (MessageSecurityMode)d.ReadInt32(),
        d.ReadString(), d.ReadArray(UserTokenPolicy.Decode) ?? [], d.ReadString(), d.ReadByte());

    internal void Encode(BinaryEncoder e)
    {
        e.WriteString(EndpointUrl);
        Server.Encode(e);
        e.WriteByteString(ServerCertificate);
        e.WriteInt32((int)SecurityMode);
        e.WriteString(SecurityPolicyUri);
        e.WriteArray(UserIdentityTokens, (x, t) => t.Encode(x));
        e.WriteString(TransportProfileUri);
        e.WriteByte(SecurityLevel);
    }
}
