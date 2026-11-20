using System.Buffers.Binary;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus;

/// <summary>
/// Converts between raw Modbus registers or bits and <see cref="Variant"/> values, following a
/// <see cref="ModbusPoint"/>'s type, word order and byte order.
/// </summary>
/// <example>
/// <code>
/// var point = new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.Float32);
/// ModbusResult&lt;ushort[]&gt; raw = ModbusDataConverter.ToRegisters(point, Variant.From(123.456f));
/// // raw.Value is { 0x42F6, 0xE979 }
/// Variant back = ModbusDataConverter.FromRegisters(point, raw.Value);
/// </code>
/// </example>
public static class ModbusDataConverter
{
    /// <summary>The value held in <paramref name="registers"/>.</summary>
    /// <param name="point">Type and layout; must be a register-table point.</param>
    /// <param name="registers">Exactly <see cref="ModbusPoint.Quantity"/> registers, as read.</param>
    /// <returns>A scalar, or an array when <see cref="ModbusPoint.ArrayLength"/> is set.</returns>
    /// <exception cref="ArgumentException">The point is not valid or the register count does not match.</exception>
    public static Variant FromRegisters(ModbusPoint point, ReadOnlySpan<ushort> registers)
    {
        ArgumentNullException.ThrowIfNull(point);
        point.Validate();
        if (point.IsBitTable)
        {
            throw new ArgumentException($"{point.Table} hold bits; use FromBits.", nameof(point));
        }
        if (registers.Length != point.Quantity)
        {
            throw new ArgumentException($"Expected {point.Quantity} registers, got {registers.Length}.", nameof(registers));
        }

        if (point.Type == ModbusDataType.Boolean)
        {
            return Variant.From(((registers[0] >> point.BitIndex!.Value) & 1) != 0);
        }
        if (point.Type == ModbusDataType.String)
        {
            Span<byte> text = stackalloc byte[registers.Length * 2];
            WordsToBytes(registers, text, ModbusWordOrder.HighWordFirst, point.ByteOrder);
            text = text[..point.StringLength];
            var end = text.IndexOf((byte)0);
            return Variant.From(System.Text.Encoding.Latin1.GetString(end < 0 ? text : text[..end]));
        }

        var size = point.RegistersPerElement;
        Span<byte> bytes = stackalloc byte[size * 2];
        if (point.ArrayLength is null)
        {
            WordsToBytes(registers, bytes, point.WordOrder, point.ByteOrder);
            return Scalar(point.Type, bytes);
        }

        var count = point.ElementCount;
        var values = point.Type switch
        {
            ModbusDataType.Int16 => (Array)new short[count],
            ModbusDataType.UInt16 => new ushort[count],
            ModbusDataType.Int32 => new int[count],
            ModbusDataType.UInt32 => new uint[count],
            ModbusDataType.Int64 => new long[count],
            ModbusDataType.UInt64 => new ulong[count],
            ModbusDataType.Float32 => new float[count],
            ModbusDataType.Float64 => new double[count],
            _ => throw new ArgumentException($"Unsupported type {point.Type}.", nameof(point)),
        };
        for (var i = 0; i < count; i++)
        {
            WordsToBytes(registers.Slice(i * size, size), bytes, point.WordOrder, point.ByteOrder);
            values.SetValue(Scalar(point.Type, bytes).Value, i);
        }
        return values switch
        {
            short[] a => Variant.FromArray(a),
            ushort[] a => Variant.FromArray(a),
            int[] a => Variant.FromArray(a),
            uint[] a => Variant.FromArray(a),
            long[] a => Variant.FromArray(a),
            ulong[] a => Variant.FromArray(a),
            float[] a => Variant.FromArray(a),
            double[] a => Variant.FromArray(a),
            _ => throw new InvalidOperationException("unreachable"),
        };
    }

    /// <summary>The value held in <paramref name="bits"/> (coils or discrete inputs).</summary>
    /// <param name="point">A Boolean point in a bit table.</param>
    /// <param name="bits">Exactly <see cref="ModbusPoint.Quantity"/> bits, as read.</param>
    /// <returns>A Boolean, or a Boolean array when <see cref="ModbusPoint.ArrayLength"/> is set.</returns>
    /// <exception cref="ArgumentException">The point is not a bit-table point or the count does not match.</exception>
    public static Variant FromBits(ModbusPoint point, ReadOnlySpan<bool> bits)
    {
        ArgumentNullException.ThrowIfNull(point);
        point.Validate();
        if (!point.IsBitTable)
        {
            throw new ArgumentException($"{point.Table} hold registers; use FromRegisters.", nameof(point));
        }
        if (bits.Length != point.Quantity)
        {
            throw new ArgumentException($"Expected {point.Quantity} bits, got {bits.Length}.", nameof(bits));
        }
        return point.ArrayLength is null ? Variant.From(bits[0]) : Variant.FromArray(bits.ToArray());
    }

    /// <summary>
    /// The registers that hold <paramref name="value"/>. Numbers convert between types when the
    /// value fits exactly: 7.0 into an Int16 works, 7.5 is BadTypeMismatch and 70000 is BadOutOfRange.
    /// </summary>
    /// <param name="point">Type and layout; a register-table point that is not a register bit.</param>
    /// <param name="value">A scalar, or an array of <see cref="ModbusPoint.ArrayLength"/> elements.</param>
    /// <returns>The registers, or BadTypeMismatch, BadOutOfRange or BadNotWritable.</returns>
    /// <exception cref="ArgumentException">The point is not valid.</exception>
    public static ModbusResult<ushort[]> ToRegisters(ModbusPoint point, Variant value)
    {
        ArgumentNullException.ThrowIfNull(point);
        point.Validate();
        if (point.IsBitTable)
        {
            throw new ArgumentException($"{point.Table} hold bits; use ToBits.", nameof(point));
        }
        if (point.Type == ModbusDataType.Boolean)
        {
            return Fail<ushort[]>(StatusCodes.BadNotWritable); // a read-modify-write would race with the device
        }

        var registers = new ushort[point.Quantity];
        if (point.Type == ModbusDataType.String)
        {
            if (value.IsArray || value.Value is not string text)
            {
                return Fail<ushort[]>(StatusCodes.BadTypeMismatch);
            }
            if (text.Length > point.StringLength || text.Any(c => c > 0xFF))
            {
                return Fail<ushort[]>(StatusCodes.BadOutOfRange);
            }
            Span<byte> bytes = stackalloc byte[registers.Length * 2];
            bytes.Clear();
            System.Text.Encoding.Latin1.GetBytes(text, bytes);
            BytesToWords(bytes, registers, ModbusWordOrder.HighWordFirst, point.ByteOrder);
            return new(registers, StatusCodes.Good);
        }

        object?[] elements;
        if (point.ArrayLength is null)
        {
            if (value.IsArray)
            {
                return Fail<ushort[]>(StatusCodes.BadTypeMismatch);
            }
            elements = [value.Value];
        }
        else
        {
            if (!value.IsArray || value.Value is not Array array || array.Length != point.ElementCount)
            {
                return Fail<ushort[]>(StatusCodes.BadTypeMismatch);
            }
            elements = new object?[array.Length];
            array.CopyTo(elements, 0);
        }

        var size = point.RegistersPerElement;
        Span<byte> buffer = stackalloc byte[size * 2];
        for (var i = 0; i < elements.Length; i++)
        {
            var status = EncodeScalar(point.Type, elements[i], buffer);
            if (!status.IsGood)
            {
                return Fail<ushort[]>(status);
            }
            BytesToWords(buffer, registers.AsSpan(i * size, size), point.WordOrder, point.ByteOrder);
        }
        return new(registers, StatusCodes.Good);
    }

    /// <summary>The bits that hold <paramref name="value"/> (a Boolean or a Boolean array).</summary>
    /// <param name="point">A Boolean point in a bit table.</param>
    /// <param name="value">The value.</param>
    /// <returns>The bits, or BadTypeMismatch.</returns>
    /// <exception cref="ArgumentException">The point is not a bit-table point.</exception>
    public static ModbusResult<bool[]> ToBits(ModbusPoint point, Variant value)
    {
        ArgumentNullException.ThrowIfNull(point);
        point.Validate();
        if (!point.IsBitTable)
        {
            throw new ArgumentException($"{point.Table} hold registers; use ToRegisters.", nameof(point));
        }
        if (point.ArrayLength is null)
        {
            return !value.IsArray && value.Value is bool b ? new([b], StatusCodes.Good) : Fail<bool[]>(StatusCodes.BadTypeMismatch);
        }
        return value.IsArray && value.Value is bool[] bits && bits.Length == point.ElementCount
            ? new((bool[])bits.Clone(), StatusCodes.Good)
            : Fail<bool[]>(StatusCodes.BadTypeMismatch);
    }

    private static ModbusResult<T> Fail<T>(StatusCode status) => new(default, status);

    /// <summary>Registers (wire order) to the value's big-endian bytes.</summary>
    private static void WordsToBytes(ReadOnlySpan<ushort> words, Span<byte> bytes, ModbusWordOrder wordOrder, ModbusByteOrder byteOrder)
    {
        var n = words.Length;
        for (var i = 0; i < n; i++)
        {
            var word = words[wordOrder == ModbusWordOrder.HighWordFirst ? i : n - 1 - i];
            if (byteOrder == ModbusByteOrder.LowByteFirst)
            {
                word = BinaryPrimitives.ReverseEndianness(word);
            }
            BinaryPrimitives.WriteUInt16BigEndian(bytes[(i * 2)..], word);
        }
    }

    /// <summary>The value's big-endian bytes to registers (wire order).</summary>
    private static void BytesToWords(ReadOnlySpan<byte> bytes, Span<ushort> words, ModbusWordOrder wordOrder, ModbusByteOrder byteOrder)
    {
        var n = words.Length;
        for (var i = 0; i < n; i++)
        {
            var word = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i * 2)..]);
            if (byteOrder == ModbusByteOrder.LowByteFirst)
            {
                word = BinaryPrimitives.ReverseEndianness(word);
            }
            words[wordOrder == ModbusWordOrder.HighWordFirst ? i : n - 1 - i] = word;
        }
    }

    private static Variant Scalar(ModbusDataType type, ReadOnlySpan<byte> b) => type switch
    {
        ModbusDataType.Int16 => Variant.From(BinaryPrimitives.ReadInt16BigEndian(b)),
        ModbusDataType.UInt16 => Variant.From(BinaryPrimitives.ReadUInt16BigEndian(b)),
        ModbusDataType.Int32 => Variant.From(BinaryPrimitives.ReadInt32BigEndian(b)),
        ModbusDataType.UInt32 => Variant.From(BinaryPrimitives.ReadUInt32BigEndian(b)),
        ModbusDataType.Int64 => Variant.From(BinaryPrimitives.ReadInt64BigEndian(b)),
        ModbusDataType.UInt64 => Variant.From(BinaryPrimitives.ReadUInt64BigEndian(b)),
        ModbusDataType.Float32 => Variant.From(BinaryPrimitives.ReadSingleBigEndian(b)),
        ModbusDataType.Float64 => Variant.From(BinaryPrimitives.ReadDoubleBigEndian(b)),
        _ => throw new ArgumentException($"Unsupported type {type}."),
    };

    private static StatusCode EncodeScalar(ModbusDataType type, object? value, Span<byte> b)
    {
        switch (type)
        {
            case ModbusDataType.Float32:
                if (!TryGetDouble(value, out var f))
                {
                    return StatusCodes.BadTypeMismatch;
                }
                if (double.IsFinite(f) && Math.Abs(f) > float.MaxValue)
                {
                    return StatusCodes.BadOutOfRange;
                }
                BinaryPrimitives.WriteSingleBigEndian(b, (float)f);
                return StatusCodes.Good;
            case ModbusDataType.Float64:
                if (!TryGetDouble(value, out var d))
                {
                    return StatusCodes.BadTypeMismatch;
                }
                BinaryPrimitives.WriteDoubleBigEndian(b, d);
                return StatusCodes.Good;
        }

        var status = TryGetInteger(value, out var integer);
        if (!status.IsGood)
        {
            return status;
        }
        var (min, max) = type switch
        {
            ModbusDataType.Int16 => ((Int128)short.MinValue, (Int128)short.MaxValue),
            ModbusDataType.UInt16 => (0, ushort.MaxValue),
            ModbusDataType.Int32 => (int.MinValue, int.MaxValue),
            ModbusDataType.UInt32 => (0, uint.MaxValue),
            ModbusDataType.Int64 => (long.MinValue, long.MaxValue),
            _ => (0, (Int128)ulong.MaxValue),
        };
        if (integer < min || integer > max)
        {
            return StatusCodes.BadOutOfRange;
        }
        switch (type)
        {
            case ModbusDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(b, (short)integer); break;
            case ModbusDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)integer); break;
            case ModbusDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(b, (int)integer); break;
            case ModbusDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(b, (uint)integer); break;
            case ModbusDataType.Int64: BinaryPrimitives.WriteInt64BigEndian(b, (long)integer); break;
            default: BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)integer); break;
        }
        return StatusCodes.Good;
    }

    private static bool TryGetDouble(object? value, out double result)
    {
        switch (value)
        {
            case float f: result = f; return true;
            case double d: result = d; return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            default: result = 0; return false;
        }
    }

    private static StatusCode TryGetInteger(object? value, out Int128 result)
    {
        result = 0;
        switch (value)
        {
            case sbyte v: result = v; return StatusCodes.Good;
            case byte v: result = v; return StatusCodes.Good;
            case short v: result = v; return StatusCodes.Good;
            case ushort v: result = v; return StatusCodes.Good;
            case int v: result = v; return StatusCodes.Good;
            case uint v: result = v; return StatusCodes.Good;
            case long v: result = v; return StatusCodes.Good;
            case ulong v: result = v; return StatusCodes.Good;
            case float or double:
                var d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                if (!double.IsFinite(d) || Math.Truncate(d) != d)
                {
                    return StatusCodes.BadTypeMismatch;
                }
                if (Math.Abs(d) >= 18446744073709551616.0) // 2^64: outside every target type
                {
                    return StatusCodes.BadOutOfRange;
                }
                result = (Int128)d;
                return StatusCodes.Good;
            default:
                return StatusCodes.BadTypeMismatch;
        }
    }
}
