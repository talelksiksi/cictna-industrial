namespace Cictna.Industrial.Modbus;

/// <summary>
/// The data behind one unit of a Modbus TCP server. The server has already checked
/// quantities and that the address range fits in 0 to 65535; an implementation answers
/// <see cref="ModbusExceptionCode.IllegalDataAddress"/> for addresses it does not have, or another
/// exception code, or <see cref="ModbusExceptionCode.None"/> on success.
/// </summary>
/// <remarks>
/// Calls come from the server's connection tasks, possibly several at once. Implementations must
/// be thread-safe and should return quickly; a thrown exception becomes
/// <see cref="ModbusExceptionCode.ServerDeviceFailure"/> and is logged.
/// </remarks>
public interface IModbusDataStore
{
    /// <summary>Function code 1.</summary>
    /// <param name="address">First coil.</param>
    /// <param name="values">Receives the coils; its length is the quantity.</param>
    /// <returns>None on success, otherwise the exception to answer with.</returns>
    ModbusExceptionCode ReadCoils(ushort address, Span<bool> values);

    /// <summary>Function code 2.</summary>
    /// <param name="address">First input.</param>
    /// <param name="values">Receives the inputs; its length is the quantity.</param>
    /// <returns>None on success, otherwise the exception to answer with.</returns>
    ModbusExceptionCode ReadDiscreteInputs(ushort address, Span<bool> values);

    /// <summary>Function code 3.</summary>
    /// <param name="address">First register.</param>
    /// <param name="values">Receives the registers; its length is the quantity.</param>
    /// <returns>None on success, otherwise the exception to answer with.</returns>
    ModbusExceptionCode ReadHoldingRegisters(ushort address, Span<ushort> values);

    /// <summary>Function code 4.</summary>
    /// <param name="address">First register.</param>
    /// <param name="values">Receives the registers; its length is the quantity.</param>
    /// <returns>None on success, otherwise the exception to answer with.</returns>
    ModbusExceptionCode ReadInputRegisters(ushort address, Span<ushort> values);

    /// <summary>Function codes 5 and 15.</summary>
    /// <param name="address">First coil.</param>
    /// <param name="values">The new values.</param>
    /// <returns>None on success, otherwise the exception to answer with.</returns>
    ModbusExceptionCode WriteCoils(ushort address, ReadOnlySpan<bool> values);

    /// <summary>Function codes 6 and 16.</summary>
    /// <param name="address">First register.</param>
    /// <param name="values">The new values.</param>
    /// <returns>None on success, otherwise the exception to answer with.</returns>
    ModbusExceptionCode WriteHoldingRegisters(ushort address, ReadOnlySpan<ushort> values);

    /// <summary>
    /// Function code 23: write, then read. The default calls <see cref="WriteHoldingRegisters"/>
    /// and <see cref="ReadHoldingRegisters"/>; override it to make the pair atomic.
    /// </summary>
    /// <param name="writeAddress">First register to write.</param>
    /// <param name="values">The new values.</param>
    /// <param name="readAddress">First register to read.</param>
    /// <param name="result">Receives the registers read; its length is the read quantity.</param>
    /// <returns>None on success, otherwise the exception to answer with.</returns>
    ModbusExceptionCode WriteThenReadHoldingRegisters(ushort writeAddress, ReadOnlySpan<ushort> values, ushort readAddress, Span<ushort> result)
    {
        var written = WriteHoldingRegisters(writeAddress, values);
        return written != ModbusExceptionCode.None ? written : ReadHoldingRegisters(readAddress, result);
    }
}

/// <summary>What a client wrote: data of <see cref="ModbusDataStore.Written"/>.</summary>
/// <param name="Table">Coils or holding registers.</param>
/// <param name="Address">First address written.</param>
/// <param name="Count">Number of coils or registers written.</param>
public sealed record ModbusWrite(ModbusTable Table, ushort Address, int Count);

/// <summary>
/// An in-memory <see cref="IModbusDataStore"/>: four tables of fixed size, thread-safe. The
/// application (or the simulator) sets inputs and reads what clients wrote; clients see the same
/// data over the network.
/// </summary>
/// <example>
/// <code>
/// var store = new ModbusDataStore(holdingRegisters: 1000);
/// store.SetInputRegisters(0, [215, 230]);            // values clients read with function code 4
/// store.Written += (_, e) => Console.WriteLine($"{e.Table} {e.Address} written");
/// await using var server = await ModbusTcpServer.StartAsync(new ModbusServerOptions { Port = 5020, DefaultUnit = store });
/// </code>
/// </example>
public sealed class ModbusDataStore : IModbusDataStore
{
    private readonly Lock _gate = new();
    private readonly bool[] _coils;
    private readonly bool[] _discreteInputs;
    private readonly ushort[] _holdingRegisters;
    private readonly ushort[] _inputRegisters;

    /// <summary>Creates the store with the given table sizes (addresses 0 to size - 1), all zero.</summary>
    /// <param name="coils">Number of coils, 0 to 65536.</param>
    /// <param name="discreteInputs">Number of discrete inputs, 0 to 65536.</param>
    /// <param name="holdingRegisters">Number of holding registers, 0 to 65536.</param>
    /// <param name="inputRegisters">Number of input registers, 0 to 65536.</param>
    public ModbusDataStore(int coils = 65536, int discreteInputs = 65536, int holdingRegisters = 65536, int inputRegisters = 65536)
    {
        _coils = new bool[CheckSize(coils)];
        _discreteInputs = new bool[CheckSize(discreteInputs)];
        _holdingRegisters = new ushort[CheckSize(holdingRegisters)];
        _inputRegisters = new ushort[CheckSize(inputRegisters)];
    }

    /// <summary>
    /// Raised after a client wrote coils or holding registers (not for the Set methods), on the
    /// server's connection task. Handlers must be quick and must not throw.
    /// </summary>
    public event EventHandler<ModbusWrite>? Written;

    /// <summary>Number of coils.</summary>
    public int CoilCount => _coils.Length;

    /// <summary>Number of discrete inputs.</summary>
    public int DiscreteInputCount => _discreteInputs.Length;

    /// <summary>Number of holding registers.</summary>
    public int HoldingRegisterCount => _holdingRegisters.Length;

    /// <summary>Number of input registers.</summary>
    public int InputRegisterCount => _inputRegisters.Length;

    /// <summary>Copies coils out.</summary>
    /// <param name="address">First coil.</param>
    /// <param name="count">How many.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the table.</exception>
    public bool[] GetCoils(ushort address, int count) => Get(_coils, address, count);

    /// <summary>Sets coils.</summary>
    /// <param name="address">First coil.</param>
    /// <param name="values">The values.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the table.</exception>
    public void SetCoils(ushort address, ReadOnlySpan<bool> values) => Set(_coils, address, values);

    /// <summary>Copies discrete inputs out.</summary>
    /// <param name="address">First input.</param>
    /// <param name="count">How many.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the table.</exception>
    public bool[] GetDiscreteInputs(ushort address, int count) => Get(_discreteInputs, address, count);

    /// <summary>Sets discrete inputs.</summary>
    /// <param name="address">First input.</param>
    /// <param name="values">The values.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the table.</exception>
    public void SetDiscreteInputs(ushort address, ReadOnlySpan<bool> values) => Set(_discreteInputs, address, values);

    /// <summary>Copies holding registers out.</summary>
    /// <param name="address">First register.</param>
    /// <param name="count">How many.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the table.</exception>
    public ushort[] GetHoldingRegisters(ushort address, int count) => Get(_holdingRegisters, address, count);

    /// <summary>Sets holding registers.</summary>
    /// <param name="address">First register.</param>
    /// <param name="values">The values.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the table.</exception>
    public void SetHoldingRegisters(ushort address, ReadOnlySpan<ushort> values) => Set(_holdingRegisters, address, values);

    /// <summary>Copies input registers out.</summary>
    /// <param name="address">First register.</param>
    /// <param name="count">How many.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the table.</exception>
    public ushort[] GetInputRegisters(ushort address, int count) => Get(_inputRegisters, address, count);

    /// <summary>Sets input registers.</summary>
    /// <param name="address">First register.</param>
    /// <param name="values">The values.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the table.</exception>
    public void SetInputRegisters(ushort address, ReadOnlySpan<ushort> values) => Set(_inputRegisters, address, values);

    ModbusExceptionCode IModbusDataStore.ReadCoils(ushort address, Span<bool> values) => Read(_coils, address, values);

    ModbusExceptionCode IModbusDataStore.ReadDiscreteInputs(ushort address, Span<bool> values) => Read(_discreteInputs, address, values);

    ModbusExceptionCode IModbusDataStore.ReadHoldingRegisters(ushort address, Span<ushort> values) => Read(_holdingRegisters, address, values);

    ModbusExceptionCode IModbusDataStore.ReadInputRegisters(ushort address, Span<ushort> values) => Read(_inputRegisters, address, values);

    ModbusExceptionCode IModbusDataStore.WriteCoils(ushort address, ReadOnlySpan<bool> values)
    {
        var result = Write(_coils, address, values);
        if (result == ModbusExceptionCode.None)
        {
            Written?.Invoke(this, new(ModbusTable.Coils, address, values.Length));
        }
        return result;
    }

    ModbusExceptionCode IModbusDataStore.WriteHoldingRegisters(ushort address, ReadOnlySpan<ushort> values)
    {
        var result = Write(_holdingRegisters, address, values);
        if (result == ModbusExceptionCode.None)
        {
            Written?.Invoke(this, new(ModbusTable.HoldingRegisters, address, values.Length));
        }
        return result;
    }

    ModbusExceptionCode IModbusDataStore.WriteThenReadHoldingRegisters(ushort writeAddress, ReadOnlySpan<ushort> values, ushort readAddress, Span<ushort> result)
    {
        lock (_gate)
        {
            if (!Fits(_holdingRegisters, writeAddress, values.Length) || !Fits(_holdingRegisters, readAddress, result.Length))
            {
                return ModbusExceptionCode.IllegalDataAddress;
            }
            values.CopyTo(_holdingRegisters.AsSpan(writeAddress));
            _holdingRegisters.AsSpan(readAddress, result.Length).CopyTo(result);
        }
        Written?.Invoke(this, new(ModbusTable.HoldingRegisters, writeAddress, values.Length));
        return ModbusExceptionCode.None;
    }

    private static int CheckSize(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 65536);
        return size;
    }

    private static bool Fits<T>(T[] table, ushort address, int count) => address + count <= table.Length;

    private T[] Get<T>(T[] table, ushort address, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_gate)
        {
            if (!Fits(table, address, count))
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"Addresses {address} to {address + count - 1} are outside the table of {table.Length}.");
            }
            return table.AsSpan(address, count).ToArray();
        }
    }

    private void Set<T>(T[] table, ushort address, ReadOnlySpan<T> values)
    {
        lock (_gate)
        {
            if (!Fits(table, address, values.Length))
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"Addresses {address} to {address + values.Length - 1} are outside the table of {table.Length}.");
            }
            values.CopyTo(table.AsSpan(address));
        }
    }

    private ModbusExceptionCode Read<T>(T[] table, ushort address, Span<T> values)
    {
        lock (_gate)
        {
            if (!Fits(table, address, values.Length))
            {
                return ModbusExceptionCode.IllegalDataAddress;
            }
            table.AsSpan(address, values.Length).CopyTo(values);
            return ModbusExceptionCode.None;
        }
    }

    private ModbusExceptionCode Write<T>(T[] table, ushort address, ReadOnlySpan<T> values)
    {
        lock (_gate)
        {
            if (!Fits(table, address, values.Length))
            {
                return ModbusExceptionCode.IllegalDataAddress;
            }
            values.CopyTo(table.AsSpan(address));
            return ModbusExceptionCode.None;
        }
    }
}
