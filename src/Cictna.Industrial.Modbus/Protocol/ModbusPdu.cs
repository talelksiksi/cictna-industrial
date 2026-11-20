using System.Buffers.Binary;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus.Protocol;

/// <summary>
/// Modbus application protocol data units: building requests, checking responses (client side).
/// Pure functions over byte spans, so they can be tested against the specification's examples and
/// fuzzed.
/// </summary>
internal static class ModbusPdu
{
    /// <summary>Largest PDU: 256-byte serial frame minus address and CRC.</summary>
    public const int MaxLength = 253;

    public const int MaxReadBits = 2000;
    public const int MaxReadRegisters = 125;
    public const int MaxWriteBits = 1968;
    public const int MaxWriteRegisters = 123;
    public const int MaxReadWriteRead = 125;
    public const int MaxReadWriteWrite = 121;

    public const ushort CoilOn = 0xFF00;
    public const ushort CoilOff = 0x0000;

    public static byte[] Read(ModbusFunctionCode function, ushort address, int quantity)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)quantity);
        return pdu;
    }

    public static byte[] WriteSingleCoil(ushort address, bool value)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)ModbusFunctionCode.WriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value ? CoilOn : CoilOff);
        return pdu;
    }

    public static byte[] WriteSingleRegister(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)ModbusFunctionCode.WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }

    public static byte[] WriteMultipleCoils(ushort address, ReadOnlySpan<bool> values)
    {
        var byteCount = (values.Length + 7) / 8;
        var pdu = new byte[6 + byteCount];
        pdu[0] = (byte)ModbusFunctionCode.WriteMultipleCoils;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)byteCount;
        PackBits(values, pdu.AsSpan(6));
        return pdu;
    }

    public static byte[] WriteMultipleRegisters(ushort address, ReadOnlySpan<ushort> values)
    {
        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = (byte)ModbusFunctionCode.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        WriteRegisters(values, pdu.AsSpan(6));
        return pdu;
    }

    public static byte[] ReadWriteMultipleRegisters(ushort readAddress, int readQuantity, ushort writeAddress, ReadOnlySpan<ushort> values)
    {
        var pdu = new byte[10 + values.Length * 2];
        pdu[0] = (byte)ModbusFunctionCode.ReadWriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), readAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)readQuantity);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(5), writeAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(7), (ushort)values.Length);
        pdu[9] = (byte)(values.Length * 2);
        WriteRegisters(values, pdu.AsSpan(10));
        return pdu;
    }

    /// <summary>
    /// Checks that <paramref name="response"/> is a well-formed answer to <paramref name="request"/>:
    /// either an exception response for the same function or a normal response whose lengths,
    /// counts and echoes match. Never throws, whatever the bytes.
    /// </summary>
    public static ModbusResult Check(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        if (request.IsEmpty || response.IsEmpty)
        {
            return Bad;
        }
        var function = request[0];
        if (response[0] == (function | 0x80) && function < 0x80)
        {
            return response.Length == 2 && response[1] != 0
                ? new(ModbusStatus.FromException((ModbusExceptionCode)response[1]), (ModbusExceptionCode)response[1])
                : Bad;
        }
        if (response[0] != function)
        {
            return Bad;
        }

        switch ((ModbusFunctionCode)function)
        {
            case ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs:
                if (request.Length != 5)
                {
                    return Bad;
                }
                var bits = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);
                return CountedData(response, (bits + 7) / 8);
            case ModbusFunctionCode.ReadHoldingRegisters or ModbusFunctionCode.ReadInputRegisters:
                return request.Length == 5 ? CountedData(response, BinaryPrimitives.ReadUInt16BigEndian(request[3..]) * 2) : Bad;
            case ModbusFunctionCode.ReadWriteMultipleRegisters:
                return request.Length >= 5 ? CountedData(response, BinaryPrimitives.ReadUInt16BigEndian(request[3..]) * 2) : Bad;
            case ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteSingleRegister:
                // The response echoes the request.
                return response.Length == 5 && request.Length == 5 && response.SequenceEqual(request) ? ModbusResult.Good : Bad;
            case ModbusFunctionCode.WriteMultipleCoils or ModbusFunctionCode.WriteMultipleRegisters:
                // Address and quantity echoed.
                return response.Length == 5 && request.Length >= 5 && response[..5].SequenceEqual(request[..5]) ? ModbusResult.Good : Bad;
            default:
                return ModbusResult.Good; // raw requests: the caller interprets the data
        }
    }

    /// <summary>The data bytes of a checked read response (after the byte count).</summary>
    public static ReadOnlySpan<byte> Data(ReadOnlySpan<byte> response) => response[2..];

    public static bool[] UnpackBits(ReadOnlySpan<byte> packed, int count)
    {
        var bits = new bool[count];
        for (var i = 0; i < count; i++)
        {
            bits[i] = (packed[i >> 3] & (1 << (i & 7))) != 0;
        }
        return bits;
    }

    public static void PackBits(ReadOnlySpan<bool> bits, Span<byte> packed)
    {
        packed[..((bits.Length + 7) / 8)].Clear();
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                packed[i >> 3] |= (byte)(1 << (i & 7));
            }
        }
    }

    public static ushort[] ReadRegisters(ReadOnlySpan<byte> data, int count)
    {
        var values = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]);
        }
        return values;
    }

    public static void WriteRegisters(ReadOnlySpan<ushort> values, Span<byte> data)
    {
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data[(i * 2)..], values[i]);
        }
    }

    private static ModbusResult Bad => new(StatusCodes.BadDecodingError);

    private static ModbusResult CountedData(ReadOnlySpan<byte> response, int expectedBytes) =>
        response.Length >= 2 && response[1] == expectedBytes && response.Length == 2 + expectedBytes ? ModbusResult.Good : Bad;
}

/// <summary>The 7-byte Modbus TCP header (MBAP) in front of every PDU.</summary>
/// <param name="TransactionId">Pairs a response with its request.</param>
/// <param name="ProtocolId">Always 0 for Modbus.</param>
/// <param name="Length">Bytes that follow: the unit ID plus the PDU.</param>
/// <param name="UnitId">The addressed unit (device behind a gateway, or the device itself).</param>
internal readonly record struct MbapHeader(ushort TransactionId, ushort ProtocolId, ushort Length, byte UnitId)
{
    public const int Size = 7;

    /// <summary>Largest Length field: unit ID plus the largest PDU.</summary>
    public const int MaxLength = 1 + ModbusPdu.MaxLength;

    public int PduLength => Length - 1;

    /// <summary>The Length field leaves room for a function code and no more than the largest PDU.</summary>
    public bool HasValidLength => Length is >= 2 and <= MaxLength;

    public static MbapHeader Read(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt16BigEndian(bytes),
        BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]),
        BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]),
        bytes[6]);

    public void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt16BigEndian(bytes, TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[2..], ProtocolId);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[4..], Length);
        bytes[6] = UnitId;
    }

    /// <summary>A complete frame: header followed by the PDU.</summary>
    public static byte[] Frame(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        var frame = new byte[Size + pdu.Length];
        new MbapHeader(transactionId, 0, (ushort)(pdu.Length + 1), unitId).Write(frame);
        pdu.CopyTo(frame.AsSpan(Size));
        return frame;
    }
}
