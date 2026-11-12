using System.Buffers.Binary;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.Encoding;

/// <summary>Limits applied while decoding, protecting against hostile or corrupt input.</summary>
/// <param name="MaxStringLength">Longest string or ByteString in bytes.</param>
/// <param name="MaxArrayLength">Most elements in one array.</param>
/// <param name="MaxDepth">Deepest nesting of Variants, DataValues and DiagnosticInfos.</param>
internal sealed record DecodingLimits(int MaxStringLength = 16 * 1024 * 1024, int MaxArrayLength = 1_000_000, int MaxDepth = 100)
{
    public static DecodingLimits Default { get; } = new();
}

/// <summary>Thrown when bytes are not valid OPC UA binary encoding or exceed a decoding limit.</summary>
internal sealed class OpcUaDecodingException : Exception
{
    public OpcUaDecodingException(string message) : base(message) { }
    public OpcUaDecodingException(string message, Exception inner) : base(message, inner) { }
    public OpcUaDecodingException() : base("Invalid OPC UA binary encoding.") { }
}

/// <summary>
/// Reads values in the OPC UA binary encoding (Part 6, 5.2). Mirror of <see cref="BinaryEncoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Treats all input as untrusted: every length is checked against the remaining bytes and the
/// <see cref="DecodingLimits"/> before anything is allocated, nesting depth is bounded, and any
/// malformed input ends in an <see cref="OpcUaDecodingException"/> rather than an
/// IndexOutOfRange or OutOfMemory exception. The fuzz tests rely on that contract.
/// </para>
/// <para>Not thread-safe; use one decoder per message.</para>
/// </remarks>
internal sealed class BinaryDecoder
{
    /// <summary>Ticks of 1601-01-01 00:00 UTC, the OPC UA DateTime epoch.</summary>
    internal static readonly long Epoch1601Ticks = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long MaxOpcUaTicks = DateTime.MaxValue.Ticks - Epoch1601Ticks;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly DecodingLimits _limits;
    private int _position;
    private int _depth;

    public BinaryDecoder(ReadOnlyMemory<byte> data, DecodingLimits? limits = null)
    {
        _data = data;
        _limits = limits ?? DecodingLimits.Default;
    }

    /// <summary>Bytes consumed so far.</summary>
    public int Position => _position;

    /// <summary>Bytes not consumed yet.</summary>
    public int Remaining => _data.Length - _position;

    /// <summary>Throws unless every byte has been consumed (trailing garbage is an error).</summary>
    public void EnsureFullyConsumed()
    {
        if (Remaining != 0)
        {
            throw new OpcUaDecodingException($"{Remaining} unexpected bytes after the end of the message.");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new OpcUaDecodingException($"Need {count} bytes at offset {_position}, only {Remaining} left.");
        }
        var span = _data.Span.Slice(_position, count);
        _position += count;
        return span;
    }

    // ---- primitives --------------------------------------------------------------------------

    public bool ReadBoolean() => ReadByte() != 0;
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());
    public byte ReadByte() => Take(1)[0];
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    private int ReadLength(int max, string what)
    {
        var length = ReadInt32();
        if (length < -1)
        {
            // Some encoders write other negative values for null; Part 6 says treat them as null.
            return -1;
        }
        if (length > max)
        {
            throw new OpcUaDecodingException($"{what} length {length} exceeds the limit of {max}.");
        }
        return length;
    }

    public string? ReadString()
    {
        var length = ReadLength(_limits.MaxStringLength, "String");
        if (length < 0)
        {
            return null;
        }
        // Invalid UTF-8 sequences become U+FFFD instead of failing the whole message, the same
        // tolerance open62541 and most stacks show: one bad tag name must not hide 999 good values.
        return System.Text.Encoding.UTF8.GetString(Take(length));
    }

    /// <summary>Inverse of <see cref="BinaryEncoder.WriteDateTime"/>: 0 and negative values become MinValue, out-of-range values MaxValue.</summary>
    public DateTime ReadDateTime()
    {
        var ticks = ReadInt64();
        if (ticks <= 0)
        {
            return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        }
        if (ticks >= MaxOpcUaTicks)
        {
            return DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        }
        return new DateTime(ticks + Epoch1601Ticks, DateTimeKind.Utc);
    }

    public Guid ReadGuid() => new(Take(16));

    public byte[]? ReadByteString()
    {
        var length = ReadLength(_limits.MaxStringLength, "ByteString");
        return length < 0 ? null : Take(length).ToArray();
    }

    public XmlElement ReadXmlElement() => new(ReadString());

    public StatusCode ReadStatusCode() => new(ReadUInt32());

    // ---- NodeId ------------------------------------------------------------------------------

    public NodeId ReadNodeId()
    {
        var encoding = ReadByte();
        if ((encoding & 0xC0) != 0)
        {
            throw new OpcUaDecodingException($"NodeId encoding byte 0x{encoding:X2} has ExpandedNodeId flags.");
        }
        return ReadNodeIdBody(encoding);
    }

    private NodeId ReadNodeIdBody(byte encoding)
    {
        switch (encoding & 0x3F)
        {
            case 0x00:
                return new NodeId(ReadByte());
            case 0x01:
                var ns = ReadByte();
                return new NodeId(ReadUInt16(), ns);
            case 0x02:
                var ns2 = ReadUInt16();
                return new NodeId(ReadUInt32(), ns2);
            case 0x03:
                var ns3 = ReadUInt16();
                return new NodeId(ReadString() ?? string.Empty, ns3);
            case 0x04:
                var ns4 = ReadUInt16();
                return new NodeId(ReadGuid(), ns4);
            case 0x05:
                var ns5 = ReadUInt16();
                return new NodeId(ReadByteString() ?? [], ns5);
            default:
                throw new OpcUaDecodingException($"Unknown NodeId encoding 0x{encoding:X2}.");
        }
    }

    public ExpandedNodeId ReadExpandedNodeId()
    {
        var encoding = ReadByte();
        var nodeId = ReadNodeIdBody(encoding);
        var uri = (encoding & 0x80) != 0 ? ReadString() : null;
        var server = (encoding & 0x40) != 0 ? ReadUInt32() : 0;
        return new ExpandedNodeId(nodeId, uri, server);
    }

    // ---- structured built-ins ------------------------------------------------------------

    public QualifiedName ReadQualifiedName()
    {
        var ns = ReadUInt16();
        return new QualifiedName(ns, ReadString());
    }

    public LocalizedText ReadLocalizedText()
    {
        var mask = ReadByte();
        var locale = (mask & 0x01) != 0 ? ReadString() : null;
        var text = (mask & 0x02) != 0 ? ReadString() : null;
        return new LocalizedText(locale, text);
    }

    public ExtensionObject ReadExtensionObject()
    {
        var typeId = ReadNodeId();
        var encoding = ReadByte();
        switch (encoding)
        {
            case 0:
                return typeId.IsNull ? ExtensionObject.Empty : ExtensionObject.FromTypeOnly(typeId);
            case 1:
                return ExtensionObject.FromBinary(typeId, ReadByteString() ?? []);
            case 2:
                return ExtensionObject.FromXml(typeId, ReadString() ?? string.Empty);
            default:
                throw new OpcUaDecodingException($"Unknown ExtensionObject encoding {encoding}.");
        }
    }

    public DataValue ReadDataValue()
    {
        Enter();
        try
        {
            var mask = ReadByte();
            var value = (mask & 0x01) != 0 ? ReadVariant() : Variant.Null;
            var status = (mask & 0x02) != 0 ? ReadStatusCode() : default;
            DateTime? source = (mask & 0x04) != 0 ? ReadDateTime() : null;
            var sourcePico = (mask & 0x10) != 0 ? ReadUInt16() : (ushort)0;
            DateTime? server = (mask & 0x08) != 0 ? ReadDateTime() : null;
            var serverPico = (mask & 0x20) != 0 ? ReadUInt16() : (ushort)0;
            return new DataValue(value, status, source, server, sourcePico, serverPico);
        }
        finally
        {
            _depth--;
        }
    }

    public DiagnosticInfo? ReadDiagnosticInfo()
    {
        Enter();
        try
        {
            var mask = ReadByte();
            if (mask == 0)
            {
                return null;
            }
            int? symbolicId = (mask & 0x01) != 0 ? ReadInt32() : null;
            int? namespaceUri = (mask & 0x02) != 0 ? ReadInt32() : null;
            int? locale = (mask & 0x08) != 0 ? ReadInt32() : null;
            int? localizedText = (mask & 0x04) != 0 ? ReadInt32() : null;
            var additional = (mask & 0x10) != 0 ? ReadString() : null;
            StatusCode? inner = (mask & 0x20) != 0 ? ReadStatusCode() : null;
            var innerInfo = (mask & 0x40) != 0 ? ReadDiagnosticInfo() ?? new DiagnosticInfo() : null;
            return new DiagnosticInfo(symbolicId, namespaceUri, locale, localizedText, additional, inner, innerInfo);
        }
        finally
        {
            _depth--;
        }
    }

    private void Enter()
    {
        if (++_depth > _limits.MaxDepth)
        {
            throw new OpcUaDecodingException($"Nesting deeper than {_limits.MaxDepth} levels.");
        }
    }

    // ---- Variant -------------------------------------------------------------------------

    public Variant ReadVariant()
    {
        Enter();
        try
        {
            var mask = ReadByte();
            var typeId = mask & 0x3F;
            if (typeId == 0)
            {
                if ((mask & 0xC0) != 0)
                {
                    throw new OpcUaDecodingException("A Variant without a type cannot be an array.");
                }
                return Variant.Null;
            }
            if (typeId > (int)BuiltInType.DiagnosticInfo)
            {
                throw new OpcUaDecodingException($"Unknown Variant type id {typeId}.");
            }
            var type = (BuiltInType)typeId;
            var isArray = (mask & 0x80) != 0;
            var hasDimensions = (mask & 0x40) != 0;

            if (!isArray)
            {
                if (hasDimensions)
                {
                    throw new OpcUaDecodingException("A scalar Variant cannot have array dimensions.");
                }
                if (type == BuiltInType.Variant)
                {
                    throw new OpcUaDecodingException("A scalar Variant cannot contain a Variant.");
                }
                var scalar = ReadScalar(type);
                return Variant.Scalar(type, scalar);
            }

            var length = ReadLength(_limits.MaxArrayLength, "Array");
            var array = length < 0 ? null : ReadArrayElements(type, length);

            int[]? dims = null;
            if (hasDimensions)
            {
                var rank = ReadLength(64, "ArrayDimensions");
                if (rank < 0)
                {
                    rank = 0;
                }
                dims = new int[rank];
                long product = 1;
                for (var i = 0; i < rank; i++)
                {
                    dims[i] = ReadInt32();
                    if (dims[i] <= 0)
                    {
                        throw new OpcUaDecodingException("Array dimensions must be at least 1.");
                    }
                    product *= dims[i];
                }
                if (product != (array?.Length ?? 0))
                {
                    throw new OpcUaDecodingException("Array dimensions do not match the number of elements.");
                }
                if (rank < 2)
                {
                    dims = null; // one dimension is just an array
                }
            }
            return dims is null ? Variant.Array(type, array) : Variant.Matrix(type, array!, dims);
        }
        finally
        {
            _depth--;
        }
    }

    private Array ReadArrayElements(BuiltInType type, int length)
    {
        // Each element takes at least one byte; reject impossible lengths before allocating.
        if (length > Remaining)
        {
            throw new OpcUaDecodingException($"Array of {length} elements cannot fit in {Remaining} bytes.");
        }
        switch (type)
        {
            case BuiltInType.Boolean: { var a = new bool[length]; for (var i = 0; i < length; i++) a[i] = ReadBoolean(); return a; }
            case BuiltInType.SByte: { var a = new sbyte[length]; for (var i = 0; i < length; i++) a[i] = ReadSByte(); return a; }
            case BuiltInType.Byte: return Take(length).ToArray();
            case BuiltInType.Int16: { var a = new short[length]; for (var i = 0; i < length; i++) a[i] = ReadInt16(); return a; }
            case BuiltInType.UInt16: { var a = new ushort[length]; for (var i = 0; i < length; i++) a[i] = ReadUInt16(); return a; }
            case BuiltInType.Int32: { var a = new int[length]; for (var i = 0; i < length; i++) a[i] = ReadInt32(); return a; }
            case BuiltInType.UInt32: { var a = new uint[length]; for (var i = 0; i < length; i++) a[i] = ReadUInt32(); return a; }
            case BuiltInType.Int64: { var a = new long[length]; for (var i = 0; i < length; i++) a[i] = ReadInt64(); return a; }
            case BuiltInType.UInt64: { var a = new ulong[length]; for (var i = 0; i < length; i++) a[i] = ReadUInt64(); return a; }
            case BuiltInType.Float: { var a = new float[length]; for (var i = 0; i < length; i++) a[i] = ReadFloat(); return a; }
            case BuiltInType.Double: { var a = new double[length]; for (var i = 0; i < length; i++) a[i] = ReadDouble(); return a; }
            case BuiltInType.Variant: { var a = new Variant[length]; for (var i = 0; i < length; i++) a[i] = ReadVariant(); return a; }
        }
        var result = Array.CreateInstance(Variant.ClrType(type), length);
        for (var i = 0; i < length; i++)
        {
            // Only String and ByteString elements can be null; ReadScalar never returns null otherwise.
            result.SetValue(ReadScalar(type), i);
        }
        return result;
    }

    /// <summary>Reads one value of a built-in type (no Variant header).</summary>
    public object? ReadScalar(BuiltInType type) => type switch
    {
        BuiltInType.Boolean => ReadBoolean(),
        BuiltInType.SByte => ReadSByte(),
        BuiltInType.Byte => ReadByte(),
        BuiltInType.Int16 => ReadInt16(),
        BuiltInType.UInt16 => ReadUInt16(),
        BuiltInType.Int32 => ReadInt32(),
        BuiltInType.UInt32 => ReadUInt32(),
        BuiltInType.Int64 => ReadInt64(),
        BuiltInType.UInt64 => ReadUInt64(),
        BuiltInType.Float => ReadFloat(),
        BuiltInType.Double => ReadDouble(),
        BuiltInType.String => ReadString(),
        BuiltInType.DateTime => ReadDateTime(),
        BuiltInType.Guid => ReadGuid(),
        BuiltInType.ByteString => ReadByteString(),
        BuiltInType.XmlElement => ReadXmlElement(),
        BuiltInType.NodeId => ReadNodeId(),
        BuiltInType.ExpandedNodeId => ReadExpandedNodeId(),
        BuiltInType.StatusCode => ReadStatusCode(),
        BuiltInType.QualifiedName => ReadQualifiedName(),
        BuiltInType.LocalizedText => ReadLocalizedText(),
        BuiltInType.ExtensionObject => ReadExtensionObject(),
        BuiltInType.DataValue => ReadDataValue(),
        BuiltInType.Variant => ReadVariant(),
        BuiltInType.DiagnosticInfo => ReadDiagnosticInfo() ?? new DiagnosticInfo(),
        _ => throw new OpcUaDecodingException($"Unknown built-in type {type}."),
    };

    // ---- arrays of anything --------------------------------------------------------------

    /// <summary>Length-prefixed array; length -1 gives null.</summary>
    public T[]? ReadArray<T>(Func<BinaryDecoder, T> readItem)
    {
        var length = ReadLength(_limits.MaxArrayLength, "Array");
        if (length < 0)
        {
            return null;
        }
        if (length > Remaining)
        {
            throw new OpcUaDecodingException($"Array of {length} elements cannot fit in {Remaining} bytes.");
        }
        var items = new T[length];
        for (var i = 0; i < length; i++)
        {
            items[i] = readItem(this);
        }
        return items;
    }

    /// <summary>Reads an Int32 enumeration value.</summary>
    public TEnum ReadEnum<TEnum>() where TEnum : struct, Enum => (TEnum)Enum.ToObject(typeof(TEnum), ReadInt32());
}
