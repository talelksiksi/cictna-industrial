using System.Globalization;
using System.Text;

namespace Cictna.Industrial.Core;

/// <summary>Kind of identifier inside a <see cref="NodeId"/>.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "Member names are the type names defined by the OPC UA specification.")]
public enum NodeIdType : byte
{
    /// <summary>Unsigned 32-bit number, written <c>i=</c>.</summary>
    Numeric,
    /// <summary>String, written <c>s=</c>.</summary>
    String,
    /// <summary>GUID, written <c>g=</c>.</summary>
    Guid,
    /// <summary>Opaque byte string, written <c>b=</c> (base64).</summary>
    Opaque,
}

/// <summary>
/// Identifies a node in an OPC UA address space: a namespace index plus a numeric, string, GUID
/// or opaque identifier. Immutable; <c>default(NodeId)</c> is the null NodeId <c>i=0</c>.
/// </summary>
/// <remarks>
/// The text form follows the OPC UA convention: <c>ns=2;s=Pump01.Speed</c>, <c>i=2258</c>,
/// <c>ns=1;g=09087e75-8e5e-499b-954f-f2a9603db28a</c>, <c>ns=1;b=M/RbKBsRVkePCePcx24oRA==</c>.
/// The namespace part is omitted for namespace 0. Namespace indexes are server-specific; code that
/// must survive server changes should resolve indexes from namespace URIs at connect time.
/// </remarks>
/// <example>
/// <code>
/// var speed = NodeId.Parse("ns=2;s=Pump01.Speed");
/// var currentTime = new NodeId(2258);
/// </code>
/// </example>
public readonly struct NodeId : IEquatable<NodeId>
{
    private readonly uint _numeric;
    private readonly object? _identifier; // string, Guid (boxed) or byte[]

    /// <summary>The null NodeId (namespace 0, numeric 0).</summary>
    public static NodeId Null => default;

    /// <summary>Creates a numeric NodeId.</summary>
    /// <param name="identifier">The numeric identifier.</param>
    /// <param name="namespaceIndex">The namespace index (0 for the OPC UA namespace).</param>
    public NodeId(uint identifier, ushort namespaceIndex = 0)
    {
        NamespaceIndex = namespaceIndex;
        IdType = NodeIdType.Numeric;
        _numeric = identifier;
        _identifier = null;
    }

    /// <summary>Creates a string NodeId.</summary>
    /// <param name="identifier">The string identifier (case-sensitive).</param>
    /// <param name="namespaceIndex">The namespace index.</param>
    public NodeId(string identifier, ushort namespaceIndex)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        NamespaceIndex = namespaceIndex;
        IdType = NodeIdType.String;
        _numeric = 0;
        _identifier = identifier;
    }

    /// <summary>Creates a GUID NodeId.</summary>
    /// <param name="identifier">The GUID identifier.</param>
    /// <param name="namespaceIndex">The namespace index.</param>
    public NodeId(Guid identifier, ushort namespaceIndex)
    {
        NamespaceIndex = namespaceIndex;
        IdType = NodeIdType.Guid;
        _numeric = 0;
        _identifier = identifier;
    }

    /// <summary>Creates an opaque NodeId. The array is copied.</summary>
    /// <param name="identifier">The opaque identifier bytes.</param>
    /// <param name="namespaceIndex">The namespace index.</param>
    public NodeId(ReadOnlySpan<byte> identifier, ushort namespaceIndex)
    {
        NamespaceIndex = namespaceIndex;
        IdType = NodeIdType.Opaque;
        _numeric = 0;
        _identifier = identifier.ToArray();
    }

    /// <summary>The namespace index.</summary>
    public ushort NamespaceIndex { get; }

    /// <summary>The kind of identifier.</summary>
    public NodeIdType IdType { get; }

    /// <summary>True for the null NodeId (numeric 0 in namespace 0).</summary>
    public bool IsNull => NamespaceIndex == 0 && IdType == NodeIdType.Numeric && _numeric == 0;

    /// <summary>The numeric identifier. Throws unless <see cref="IdType"/> is Numeric.</summary>
    public uint NumericIdentifier => IdType == NodeIdType.Numeric ? _numeric : throw WrongType(NodeIdType.Numeric);

    /// <summary>The string identifier. Throws unless <see cref="IdType"/> is String.</summary>
    public string StringIdentifier => IdType == NodeIdType.String ? (string)_identifier! : throw WrongType(NodeIdType.String);

    /// <summary>The GUID identifier. Throws unless <see cref="IdType"/> is Guid.</summary>
    public Guid GuidIdentifier => IdType == NodeIdType.Guid ? (Guid)_identifier! : throw WrongType(NodeIdType.Guid);

    /// <summary>The opaque identifier. Throws unless <see cref="IdType"/> is Opaque.</summary>
    public ReadOnlyMemory<byte> OpaqueIdentifier => IdType == NodeIdType.Opaque ? (byte[])_identifier! : throw WrongType(NodeIdType.Opaque);

    private InvalidOperationException WrongType(NodeIdType wanted) =>
        new($"NodeId {this} has a {IdType} identifier, not {wanted}.");

    /// <summary>Parses the text form, for example <c>ns=2;s=Pump01.Speed</c> or <c>i=85</c>.</summary>
    /// <param name="text">The text to parse.</param>
    /// <exception cref="FormatException">The text is not a valid NodeId.</exception>
    public static NodeId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not a valid NodeId.");

    /// <summary>Parses the text form without throwing.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="nodeId">The parsed NodeId, or <see cref="Null"/> on failure.</param>
    /// <returns>True when the text was valid.</returns>
    public static bool TryParse(string? text, out NodeId nodeId)
    {
        nodeId = Null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        ushort ns = 0;
        var rest = text.AsSpan();
        if (rest.StartsWith("ns=", StringComparison.Ordinal))
        {
            var semi = rest.IndexOf(';');
            if (semi < 0 || !ushort.TryParse(rest[3..semi], NumberStyles.None, CultureInfo.InvariantCulture, out ns))
            {
                return false;
            }
            rest = rest[(semi + 1)..];
        }

        if (rest.Length < 2 || rest[1] != '=')
        {
            return false;
        }
        var value = rest[2..];
        switch (rest[0])
        {
            case 'i':
                if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    return false;
                }
                nodeId = new NodeId(n, ns);
                return true;
            case 's':
                nodeId = new NodeId(value.ToString(), ns);
                return true;
            case 'g':
                if (!System.Guid.TryParse(value, out var g))
                {
                    return false;
                }
                nodeId = new NodeId(g, ns);
                return true;
            case 'b':
                var buffer = new byte[value.Length];
                if (!Convert.TryFromBase64Chars(value, buffer, out var written))
                {
                    return false;
                }
                nodeId = new NodeId(buffer.AsSpan(0, written), ns);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The text form, for example <c>ns=2;s=Pump01.Speed</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (NamespaceIndex != 0)
        {
            sb.Append("ns=").Append(NamespaceIndex.ToString(CultureInfo.InvariantCulture)).Append(';');
        }
        switch (IdType)
        {
            case NodeIdType.Numeric:
                sb.Append("i=").Append(_numeric.ToString(CultureInfo.InvariantCulture));
                break;
            case NodeIdType.String:
                sb.Append("s=").Append((string)_identifier!);
                break;
            case NodeIdType.Guid:
                sb.Append("g=").Append(((Guid)_identifier!).ToString("D"));
                break;
            default:
                sb.Append("b=").Append(Convert.ToBase64String((byte[])_identifier!));
                break;
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public bool Equals(NodeId other)
    {
        if (NamespaceIndex != other.NamespaceIndex || IdType != other.IdType)
        {
            return false;
        }
        return IdType switch
        {
            NodeIdType.Numeric => _numeric == other._numeric,
            NodeIdType.String => string.Equals((string)_identifier!, (string)other._identifier!, StringComparison.Ordinal),
            NodeIdType.Guid => ((Guid)_identifier!).Equals((Guid)other._identifier!),
            _ => ((byte[])_identifier!).AsSpan().SequenceEqual((byte[])other._identifier!),
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(NamespaceIndex);
        hash.Add(IdType);
        switch (IdType)
        {
            case NodeIdType.Numeric:
                hash.Add(_numeric);
                break;
            case NodeIdType.Opaque:
                hash.AddBytes((byte[])_identifier!);
                break;
            default:
                hash.Add(_identifier);
                break;
        }
        return hash.ToHashCode();
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);
}
