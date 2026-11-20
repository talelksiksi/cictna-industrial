using System.Buffers.Binary;

namespace Cictna.Industrial.Modbus.Protocol;

/// <summary>
/// Server side of the protocol: turns a request PDU into a response PDU against a data store.
/// Checks in the order the specification gives: function code (01), quantity and length (03),
/// address range (02), then the store (02 or 04). Never throws on any input bytes; only a store
/// that throws can make it throw.
/// </summary>
internal static class ModbusRequestHandler
{
    /// <summary>Handles one request.</summary>
    /// <param name="store">The unit's data.</param>
    /// <param name="request">The request PDU (function code first), at least one byte.</param>
    /// <param name="response">At least <see cref="ModbusPdu.MaxLength"/> bytes.</param>
    /// <returns>The length of the response PDU written to <paramref name="response"/>.</returns>
    public static int Handle(IModbusDataStore store, ReadOnlySpan<byte> request, Span<byte> response)
    {
        if (request.IsEmpty)
        {
            return Exception(response, 0, ModbusExceptionCode.IllegalFunction);
        }
        var function = request[0];
        return (ModbusFunctionCode)function switch
        {
            ModbusFunctionCode.ReadCoils => ReadBits(request, response, store.ReadCoils),
            ModbusFunctionCode.ReadDiscreteInputs => ReadBits(request, response, store.ReadDiscreteInputs),
            ModbusFunctionCode.ReadHoldingRegisters => ReadRegisters(request, response, store.ReadHoldingRegisters),
            ModbusFunctionCode.ReadInputRegisters => ReadRegisters(request, response, store.ReadInputRegisters),
            ModbusFunctionCode.WriteSingleCoil => WriteSingleCoil(store, request, response),
            ModbusFunctionCode.WriteSingleRegister => WriteSingleRegister(store, request, response),
            ModbusFunctionCode.WriteMultipleCoils => WriteMultipleCoils(store, request, response),
            ModbusFunctionCode.WriteMultipleRegisters => WriteMultipleRegisters(store, request, response),
            ModbusFunctionCode.ReadWriteMultipleRegisters => ReadWriteMultipleRegisters(store, request, response),
            _ => Exception(response, function, ModbusExceptionCode.IllegalFunction),
        };
    }

    /// <summary>Writes an exception response and returns its length (2).</summary>
    public static int Exception(Span<byte> response, byte function, ModbusExceptionCode code)
    {
        response[0] = (byte)(function | 0x80);
        response[1] = (byte)code;
        return 2;
    }

    private delegate ModbusExceptionCode BitReader(ushort address, Span<bool> values);

    private delegate ModbusExceptionCode RegisterReader(ushort address, Span<ushort> values);

    private static bool InRange(ushort address, int quantity) => address + quantity <= 65536;

    private static int ReadBits(ReadOnlySpan<byte> request, Span<byte> response, BitReader read)
    {
        var function = request[0];
        if (request.Length != 5)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[1..]);
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);
        if (quantity is < 1 or > ModbusPdu.MaxReadBits)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        if (!InRange(address, quantity))
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataAddress);
        }
        Span<bool> bits = stackalloc bool[quantity];
        var result = read(address, bits);
        if (result != ModbusExceptionCode.None)
        {
            return Exception(response, function, result);
        }
        var byteCount = (quantity + 7) / 8;
        response[0] = function;
        response[1] = (byte)byteCount;
        ModbusPdu.PackBits(bits, response[2..]);
        return 2 + byteCount;
    }

    private static int ReadRegisters(ReadOnlySpan<byte> request, Span<byte> response, RegisterReader read)
    {
        var function = request[0];
        if (request.Length != 5)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[1..]);
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);
        if (quantity is < 1 or > ModbusPdu.MaxReadRegisters)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        if (!InRange(address, quantity))
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataAddress);
        }
        Span<ushort> registers = stackalloc ushort[quantity];
        var result = read(address, registers);
        if (result != ModbusExceptionCode.None)
        {
            return Exception(response, function, result);
        }
        response[0] = function;
        response[1] = (byte)(quantity * 2);
        ModbusPdu.WriteRegisters(registers, response[2..]);
        return 2 + quantity * 2;
    }

    private static int WriteSingleCoil(IModbusDataStore store, ReadOnlySpan<byte> request, Span<byte> response)
    {
        var function = request[0];
        if (request.Length != 5)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);
        if (value is not (ModbusPdu.CoilOn or ModbusPdu.CoilOff))
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        var result = store.WriteCoils(address, [value == ModbusPdu.CoilOn]);
        if (result != ModbusExceptionCode.None)
        {
            return Exception(response, function, result);
        }
        request.CopyTo(response);
        return 5;
    }

    private static int WriteSingleRegister(IModbusDataStore store, ReadOnlySpan<byte> request, Span<byte> response)
    {
        var function = request[0];
        if (request.Length != 5)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);
        var result = store.WriteHoldingRegisters(address, [value]);
        if (result != ModbusExceptionCode.None)
        {
            return Exception(response, function, result);
        }
        request.CopyTo(response);
        return 5;
    }

    private static int WriteMultipleCoils(IModbusDataStore store, ReadOnlySpan<byte> request, Span<byte> response)
    {
        var function = request[0];
        if (request.Length < 6)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[1..]);
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);
        var byteCount = request[5];
        if (quantity is < 1 or > ModbusPdu.MaxWriteBits || byteCount != (quantity + 7) / 8 || request.Length != 6 + byteCount)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        if (!InRange(address, quantity))
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataAddress);
        }
        var bits = ModbusPdu.UnpackBits(request[6..], quantity);
        var result = store.WriteCoils(address, bits);
        if (result != ModbusExceptionCode.None)
        {
            return Exception(response, function, result);
        }
        request[..5].CopyTo(response);
        return 5;
    }

    private static int WriteMultipleRegisters(IModbusDataStore store, ReadOnlySpan<byte> request, Span<byte> response)
    {
        var function = request[0];
        if (request.Length < 6)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[1..]);
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);
        var byteCount = request[5];
        if (quantity is < 1 or > ModbusPdu.MaxWriteRegisters || byteCount != quantity * 2 || request.Length != 6 + byteCount)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        if (!InRange(address, quantity))
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataAddress);
        }
        var result = store.WriteHoldingRegisters(address, ModbusPdu.ReadRegisters(request[6..], quantity));
        if (result != ModbusExceptionCode.None)
        {
            return Exception(response, function, result);
        }
        request[..5].CopyTo(response);
        return 5;
    }

    private static int ReadWriteMultipleRegisters(IModbusDataStore store, ReadOnlySpan<byte> request, Span<byte> response)
    {
        var function = request[0];
        if (request.Length < 10)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        var readAddress = BinaryPrimitives.ReadUInt16BigEndian(request[1..]);
        var readQuantity = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);
        var writeAddress = BinaryPrimitives.ReadUInt16BigEndian(request[5..]);
        var writeQuantity = BinaryPrimitives.ReadUInt16BigEndian(request[7..]);
        var byteCount = request[9];
        if (readQuantity is < 1 or > ModbusPdu.MaxReadWriteRead ||
            writeQuantity is < 1 or > ModbusPdu.MaxReadWriteWrite ||
            byteCount != writeQuantity * 2 || request.Length != 10 + byteCount)
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataValue);
        }
        if (!InRange(readAddress, readQuantity) || !InRange(writeAddress, writeQuantity))
        {
            return Exception(response, function, ModbusExceptionCode.IllegalDataAddress);
        }
        Span<ushort> read = stackalloc ushort[readQuantity];
        var result = store.WriteThenReadHoldingRegisters(writeAddress, ModbusPdu.ReadRegisters(request[10..], writeQuantity), readAddress, read);
        if (result != ModbusExceptionCode.None)
        {
            return Exception(response, function, result);
        }
        response[0] = function;
        response[1] = (byte)(readQuantity * 2);
        ModbusPdu.WriteRegisters(read, response[2..]);
        return 2 + readQuantity * 2;
    }
}
