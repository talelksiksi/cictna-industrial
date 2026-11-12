namespace Cictna.Industrial.Core;

/// <summary>How the body of an <see cref="ExtensionObject"/> is encoded.</summary>
public enum ExtensionObjectEncoding : byte
{
    /// <summary>No body.</summary>
    None = 0,
    /// <summary>OPC UA binary encoding.</summary>
    Binary = 1,
    /// <summary>XML text.</summary>
    Xml = 2,
}

/// <summary>
/// A structured value (for example Range or EUInformation, or a vendor structure) carried with the
/// id of its encoding. The body is kept in its encoded form; decoding known structures into typed
/// objects is done by the layer that knows them.
/// </summary>
/// <remarks>
/// Keeping the body opaque means any structure, including vendor types this stack has never seen,
/// can be read and written back unchanged.
/// </remarks>
public sealed class ExtensionObject : IEquatable<ExtensionObject>
{
    private readonly byte[]? _binary;

    private ExtensionObject(NodeId typeId, ExtensionObjectEncoding encoding, byte[]? binary, string? xml)
    {
        TypeId = typeId;
        Encoding = encoding;
        _binary = binary;
        XmlBody = xml;
    }

    /// <summary>An ExtensionObject with no type and no body.</summary>
    public static ExtensionObject Empty { get; } = new(NodeId.Null, ExtensionObjectEncoding.None, null, null);

    /// <summary>Creates an ExtensionObject with a binary body. The body is copied.</summary>
    /// <param name="encodingId">NodeId of the binary encoding (for Range: <c>i=886</c>).</param>
    /// <param name="body">The binary-encoded structure.</param>
    public static ExtensionObject FromBinary(NodeId encodingId, ReadOnlySpan<byte> body) =>
        new(encodingId, ExtensionObjectEncoding.Binary, body.ToArray(), null);

    /// <summary>Creates an ExtensionObject with an XML body.</summary>
    /// <param name="encodingId">NodeId of the XML encoding.</param>
    /// <param name="xml">The XML text.</param>
    public static ExtensionObject FromXml(NodeId encodingId, string xml) =>
        new(encodingId, ExtensionObjectEncoding.Xml, null, xml);

    /// <summary>Creates an ExtensionObject that only carries a type id.</summary>
    /// <param name="typeId">The type id.</param>
    public static ExtensionObject FromTypeOnly(NodeId typeId) =>
        new(typeId, ExtensionObjectEncoding.None, null, null);

    /// <summary>NodeId of the encoding of the body (for binary bodies, the DefaultBinary encoding id).</summary>
    public NodeId TypeId { get; }

    /// <summary>How the body is encoded.</summary>
    public ExtensionObjectEncoding Encoding { get; }

    /// <summary>The binary body, empty unless <see cref="Encoding"/> is Binary.</summary>
    public ReadOnlyMemory<byte> BinaryBody => _binary ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>The XML body, null unless <see cref="Encoding"/> is Xml.</summary>
    public string? XmlBody { get; }

    /// <inheritdoc />
    public bool Equals(ExtensionObject? other) =>
        other is not null &&
        TypeId.Equals(other.TypeId) &&
        Encoding == other.Encoding &&
        BinaryBody.Span.SequenceEqual(other.BinaryBody.Span) &&
        string.Equals(XmlBody, other.XmlBody, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ExtensionObject);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TypeId, Encoding, BinaryBody.Length);

    /// <summary>Type id and body size.</summary>
    public override string ToString() => Encoding switch
    {
        ExtensionObjectEncoding.Binary => $"ExtensionObject({TypeId}, {BinaryBody.Length} bytes)",
        ExtensionObjectEncoding.Xml => $"ExtensionObject({TypeId}, xml)",
        _ => $"ExtensionObject({TypeId})",
    };
}

/// <summary>
/// Vendor-specific diagnostics a server can return with a result. The integer fields are indexes
/// into the string table of the response that carried this object.
/// </summary>
/// <param name="SymbolicId">Index of the symbolic id in the string table.</param>
/// <param name="NamespaceUri">Index of the namespace URI in the string table.</param>
/// <param name="Locale">Index of the locale in the string table.</param>
/// <param name="LocalizedText">Index of the localized text in the string table.</param>
/// <param name="AdditionalInfo">Free text from the server.</param>
/// <param name="InnerStatusCode">Status code from a lower layer.</param>
/// <param name="InnerDiagnosticInfo">Diagnostics from a lower layer.</param>
public sealed record DiagnosticInfo(
    int? SymbolicId = null,
    int? NamespaceUri = null,
    int? Locale = null,
    int? LocalizedText = null,
    string? AdditionalInfo = null,
    StatusCode? InnerStatusCode = null,
    DiagnosticInfo? InnerDiagnosticInfo = null);
