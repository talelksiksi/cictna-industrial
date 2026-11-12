using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.Encoding;

/// <summary>
/// Writes values in the OPC UA binary encoding (Part 6, 5.2).
/// </summary>
/// <remarks>
/// <para>
/// Why we encode in C# at all: values and service messages cross the boundary to the native
/// library as standard OPC UA binary bytes (see docs/native-layer.md). The shim decodes them with
/// open62541's own decoder, so every byte this class writes is checked by a reference
/// implementation, and the differential tests compare both byte for byte.
/// </para>
/// <para>
/// Canonical choices, identical to open62541 so round trips are byte-exact:
/// NodeIds use the smallest numeric form (two-byte, four-byte, full); LocalizedText and
/// ExtensionObject set mask bits for non-null parts; DataValue omits fields that are absent
/// (Good status, null timestamps, zero picoseconds, null value).
/// </para>
/// <para>Not thread-safe; use one encoder per message.</para>
/// </remarks>
internal sealed class BinaryEncoder
{
    private readonly ArrayBufferWriter<byte> _buffer;

    public BinaryEncoder(int initialCapacity = 256) => _buffer = new ArrayBufferWriter<byte>(initialCapacity);

    /// <summary>The bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    /// <summary>A copy of the bytes written so far.</summary>
    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    // ---- primitives --------------------------------------------------------------------------

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

    public void WriteByte(byte value)
    {
        _buffer.GetSpan(1)[0] = value;
        _buffer.Advance(1);
    }

    public void WriteInt16(short value) { BinaryPrimitives.WriteInt16LittleEndian(_buffer.GetSpan(2), value); _buffer.Advance(2); }
    public void WriteUInt16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(_buffer.GetSpan(2), value); _buffer.Advance(2); }
    public void WriteInt32(int value) { BinaryPrimitives.WriteInt32LittleEndian(_buffer.GetSpan(4), value); _buffer.Advance(4); }
    public void WriteUInt32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(4), value); _buffer.Advance(4); }
    public void WriteInt64(long value) { BinaryPrimitives.WriteInt64LittleEndian(_buffer.GetSpan(8), value); _buffer.Advance(8); }
    public void WriteUInt64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(_buffer.GetSpan(8), value); _buffer.Advance(8); }
    public void WriteFloat(float value) { BinaryPrimitives.WriteSingleLittleEndian(_buffer.GetSpan(4), value); _buffer.Advance(4); }
    public void WriteDouble(double value) { BinaryPrimitives.WriteDoubleLittleEndian(_buffer.GetSpan(8), value); _buffer.Advance(8); }

    /// <summary>Length-prefixed UTF-8; null is written as length -1.</summary>
    public void WriteString(string? value)
    {
        if (value is null)
        {
            WriteInt32(-1);
            return;
        }
        var length = System.Text.Encoding.UTF8.GetByteCount(value);
        WriteInt32(length);
        System.Text.Encoding.UTF8.GetBytes(value, _buffer.GetSpan(length));
        _buffer.Advance(length);
    }

    /// <summary>
    /// 100 ns ticks since 1601-01-01 UTC. DateTime.MinValue (and anything before 1601) is written
    /// as 0, DateTime.MaxValue as Int64.MaxValue, as Part 6 prescribes.
    /// </summary>
    public void WriteDateTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        if (utc == DateTime.MaxValue)
        {
            WriteInt64(long.MaxValue);
        }
        else if (utc.Ticks <= BinaryDecoder.Epoch1601Ticks)
        {
            WriteInt64(0);
        }
        else
        {
            WriteInt64(utc.Ticks - BinaryDecoder.Epoch1601Ticks);
        }
    }

    /// <summary>
    /// Data1 (uint32 LE), Data2 (uint16 LE), Data3 (uint16 LE), Data4 (8 bytes): exactly the
    /// layout System.Guid uses for its little-endian byte form.
    /// </summary>
    public void WriteGuid(Guid value)
    {
        value.TryWriteBytes(_buffer.GetSpan(16));
        _buffer.Advance(16);
    }

    /// <summary>Length-prefixed bytes; null is written as length -1.</summary>
    public void WriteByteString(ReadOnlySpan<byte> value, bool isNull = false)
    {
        if (isNull)
        {
            WriteInt32(-1);
            return;
        }
        WriteInt32(value.Length);
        value.CopyTo(_buffer.GetSpan(value.Length));
        _buffer.Advance(value.Length);
    }

    public void WriteByteString(byte[]? value) => WriteByteString(value, value is null);

    public void WriteXmlElement(XmlElement value) => WriteString(value.Value);

    public void WriteStatusCode(StatusCode value) => WriteUInt32(value.Code);

    // ---- NodeId ------------------------------------------------------------------------------

    public void WriteNodeId(NodeId value) => WriteNodeIdCore(value, 0);

    private void WriteNodeIdCore(NodeId value, byte flags)
    {
        switch (value.IdType)
        {
            case NodeIdType.Numeric:
                var id = value.NumericIdentifier;
                if (value.NamespaceIndex == 0 && id <= byte.MaxValue)
                {
                    WriteByte((byte)(0x00 | flags));
                    WriteByte((byte)id);
                }
                else if (value.NamespaceIndex <= byte.MaxValue && id <= ushort.MaxValue)
                {
                    WriteByte((byte)(0x01 | flags));
                    WriteByte((byte)value.NamespaceIndex);
                    WriteUInt16((ushort)id);
                }
                else
                {
                    WriteByte((byte)(0x02 | flags));
                    WriteUInt16(value.NamespaceIndex);
                    WriteUInt32(id);
                }
                break;
            case NodeIdType.String:
                WriteByte((byte)(0x03 | flags));
                WriteUInt16(value.NamespaceIndex);
                WriteString(value.StringIdentifier);
                break;
            case NodeIdType.Guid:
                WriteByte((byte)(0x04 | flags));
                WriteUInt16(value.NamespaceIndex);
                WriteGuid(value.GuidIdentifier);
                break;
            default:
                WriteByte((byte)(0x05 | flags));
                WriteUInt16(value.NamespaceIndex);
                WriteByteString(value.OpaqueIdentifier.Span);
                break;
        }
    }

    public void WriteExpandedNodeId(ExpandedNodeId value)
    {
        // ExpandedNodeId normalises itself (namespace index 0 with a URI, empty URI = none),
        // matching open62541's canonical form.
        byte flags = 0;
        if (value.NamespaceUri is not null)
        {
            flags |= 0x80;
        }
        if (value.ServerIndex != 0)
        {
            flags |= 0x40;
        }
        WriteNodeIdCore(value.NodeId, flags);
        if (value.NamespaceUri is not null)
        {
            WriteString(value.NamespaceUri);
        }
        if (value.ServerIndex != 0)
        {
            WriteUInt32(value.ServerIndex);
        }
    }

    // ---- structured built-ins ------------------------------------------------------------

    public void WriteQualifiedName(QualifiedName value)
    {
        WriteUInt16(value.NamespaceIndex);
        WriteString(value.Name);
    }

    public void WriteLocalizedText(LocalizedText value)
    {
        byte mask = 0;
        if (value.Locale is not null)
        {
            mask |= 0x01;
        }
        if (value.Text is not null)
        {
            mask |= 0x02;
        }
        WriteByte(mask);
        if (value.Locale is not null)
        {
            WriteString(value.Locale);
        }
        if (value.Text is not null)
        {
            WriteString(value.Text);
        }
    }

    public void WriteExtensionObject(ExtensionObject? value)
    {
        value ??= ExtensionObject.Empty;
        WriteNodeId(value.TypeId);
        WriteByte((byte)value.Encoding);
        switch (value.Encoding)
        {
            case ExtensionObjectEncoding.Binary:
                WriteByteString(value.BinaryBody.Span);
                break;
            case ExtensionObjectEncoding.Xml:
                WriteString(value.XmlBody);
                break;
        }
    }

    public void WriteDataValue(DataValue value)
    {
        byte mask = 0;
        if (!value.Value.IsNull) mask |= 0x01;
        if (value.Status.Code != 0) mask |= 0x02;
        if (value.SourceTimestamp is not null) mask |= 0x04;
        if (value.ServerTimestamp is not null) mask |= 0x08;
        if (value.SourcePicoseconds != 0) mask |= 0x10;
        if (value.ServerPicoseconds != 0) mask |= 0x20;
        WriteByte(mask);
        if ((mask & 0x01) != 0) WriteVariant(value.Value);
        if ((mask & 0x02) != 0) WriteStatusCode(value.Status);
        if ((mask & 0x04) != 0) WriteDateTime(value.SourceTimestamp!.Value);
        if ((mask & 0x10) != 0) WriteUInt16(value.SourcePicoseconds);
        if ((mask & 0x08) != 0) WriteDateTime(value.ServerTimestamp!.Value);
        if ((mask & 0x20) != 0) WriteUInt16(value.ServerPicoseconds);
    }

    public void WriteDiagnosticInfo(DiagnosticInfo? value)
    {
        if (value is null)
        {
            WriteByte(0);
            return;
        }
        byte mask = 0;
        if (value.SymbolicId is not null) mask |= 0x01;
        if (value.NamespaceUri is not null) mask |= 0x02;
        if (value.LocalizedText is not null) mask |= 0x04;
        if (value.Locale is not null) mask |= 0x08;
        if (value.AdditionalInfo is not null) mask |= 0x10;
        if (value.InnerStatusCode is not null) mask |= 0x20;
        if (value.InnerDiagnosticInfo is not null) mask |= 0x40;
        WriteByte(mask);
        // Field order differs from the mask bit order: Locale comes before LocalizedText.
        if (value.SymbolicId is { } s) WriteInt32(s);
        if (value.NamespaceUri is { } n) WriteInt32(n);
        if (value.Locale is { } l) WriteInt32(l);
        if (value.LocalizedText is { } t) WriteInt32(t);
        if (value.AdditionalInfo is not null) WriteString(value.AdditionalInfo);
        if (value.InnerStatusCode is { } c) WriteStatusCode(c);
        if (value.InnerDiagnosticInfo is not null) WriteDiagnosticInfo(value.InnerDiagnosticInfo);
    }

    // ---- Variant -------------------------------------------------------------------------

    public void WriteVariant(Variant value)
    {
        if (value.IsNull)
        {
            WriteByte(0);
            return;
        }
        var mask = (byte)value.Type;
        if (value.IsArray)
        {
            mask |= 0x80;
            if (value.IsMatrix)
            {
                mask |= 0x40;
            }
        }
        WriteByte(mask);

        if (!value.IsArray)
        {
            WriteScalar(value.Type, value.Value);
            return;
        }

        if (value.Value is not Array array)
        {
            WriteInt32(-1);
        }
        else
        {
            WriteInt32(array.Length);
            WriteArrayElements(value.Type, array);
        }

        if (value.IsMatrix)
        {
            var dims = value.ArrayDimensions!;
            WriteInt32(dims.Count);
            foreach (var d in dims)
            {
                WriteInt32(d);
            }
        }
    }

    private void WriteArrayElements(BuiltInType type, Array array)
    {
        // Typed loops for the common numeric types avoid boxing every element.
        switch (array)
        {
            case bool[] a: foreach (var v in a) WriteBoolean(v); return;
            case sbyte[] a: foreach (var v in a) WriteSByte(v); return;
            case byte[] a when type == BuiltInType.Byte: foreach (var v in a) WriteByte(v); return;
            case short[] a: foreach (var v in a) WriteInt16(v); return;
            case ushort[] a: foreach (var v in a) WriteUInt16(v); return;
            case int[] a: foreach (var v in a) WriteInt32(v); return;
            case uint[] a: foreach (var v in a) WriteUInt32(v); return;
            case long[] a: foreach (var v in a) WriteInt64(v); return;
            case ulong[] a: foreach (var v in a) WriteUInt64(v); return;
            case float[] a: foreach (var v in a) WriteFloat(v); return;
            case double[] a: foreach (var v in a) WriteDouble(v); return;
            case Variant[] a: foreach (var v in a) WriteVariant(v); return;
        }
        foreach (var element in array)
        {
            WriteScalar(type, element);
        }
    }

    /// <summary>Writes one value of a built-in type (no Variant header).</summary>
    public void WriteScalar(BuiltInType type, object? value)
    {
        switch (type)
        {
            case BuiltInType.Boolean: WriteBoolean((bool)value!); break;
            case BuiltInType.SByte: WriteSByte((sbyte)value!); break;
            case BuiltInType.Byte: WriteByte((byte)value!); break;
            case BuiltInType.Int16: WriteInt16((short)value!); break;
            case BuiltInType.UInt16: WriteUInt16((ushort)value!); break;
            case BuiltInType.Int32: WriteInt32((int)value!); break;
            case BuiltInType.UInt32: WriteUInt32((uint)value!); break;
            case BuiltInType.Int64: WriteInt64((long)value!); break;
            case BuiltInType.UInt64: WriteUInt64((ulong)value!); break;
            case BuiltInType.Float: WriteFloat((float)value!); break;
            case BuiltInType.Double: WriteDouble((double)value!); break;
            case BuiltInType.String: WriteString((string?)value); break;
            case BuiltInType.DateTime: WriteDateTime((DateTime)value!); break;
            case BuiltInType.Guid: WriteGuid((Guid)value!); break;
            case BuiltInType.ByteString: WriteByteString((byte[]?)value); break;
            case BuiltInType.XmlElement: WriteXmlElement((XmlElement)value!); break;
            case BuiltInType.NodeId: WriteNodeId((NodeId)value!); break;
            case BuiltInType.ExpandedNodeId: WriteExpandedNodeId((ExpandedNodeId)value!); break;
            case BuiltInType.StatusCode: WriteStatusCode((StatusCode)value!); break;
            case BuiltInType.QualifiedName: WriteQualifiedName((QualifiedName)value!); break;
            case BuiltInType.LocalizedText: WriteLocalizedText((LocalizedText)value!); break;
            case BuiltInType.ExtensionObject: WriteExtensionObject((ExtensionObject?)value); break;
            case BuiltInType.DataValue: WriteDataValue((DataValue)value!); break;
            case BuiltInType.Variant: WriteVariant((Variant)value!); break;
            case BuiltInType.DiagnosticInfo: WriteDiagnosticInfo((DiagnosticInfo?)value); break;
            default: throw new ArgumentOutOfRangeException(nameof(type), type, "Not a built-in type.");
        }
    }

    // ---- arrays of anything --------------------------------------------------------------

    /// <summary>Length-prefixed array; null is written as length -1.</summary>
    public void WriteArray<T>(IReadOnlyList<T>? items, Action<BinaryEncoder, T> writeItem)
    {
        if (items is null)
        {
            WriteInt32(-1);
            return;
        }
        WriteInt32(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            writeItem(this, items[i]);
        }
    }

    /// <summary>Writes an enumeration value as Int32, as OPC UA encodes all enumerations.</summary>
    public void WriteEnum<TEnum>(TEnum value) where TEnum : struct, Enum =>
        WriteInt32(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
}
