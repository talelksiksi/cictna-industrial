using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus.IntegrationTests;

/// <summary>Our client against our server over TCP: every function code, splitting, typed values, units.</summary>
public sealed class ClientServerTests : IAsyncLifetime
{
    private readonly ModbusDataStore _store = new();
    private readonly ModbusDataStore _unit2 = new(holdingRegisters: 10);
    private readonly CountingStore _counting;
    private ModbusTcpServer _server = null!;
    private ModbusTcpClient _client = null!;

    public ClientServerTests() => _counting = new CountingStore(_store);

    public async ValueTask InitializeAsync()
    {
        _server = await ModbusTcpServer.StartAsync(new ModbusServerOptions
        {
            Address = System.Net.IPAddress.Loopback,
            Port = 0,
            DefaultUnit = _counting,
            Units = new Dictionary<byte, IModbusDataStore> { [2] = _unit2, [3] = new FailingStore() },
        });
        _client = new ModbusTcpClient(new ModbusClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
        await _client.ConnectAsync("127.0.0.1", _server.Port, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryFunctionCodeRoundTrips()
    {
        _store.SetCoils(10, [true, false, true]);
        _store.SetDiscreteInputs(20, [false, true]);
        _store.SetHoldingRegisters(30, [1, 2, 3]);
        _store.SetInputRegisters(40, [400, 401]);

        Assert.Equal(new[] { true, false, true }, (await _client.ReadCoilsAsync(1, 10, 3, Ct)).Value!);
        Assert.Equal(new[] { false, true }, (await _client.ReadDiscreteInputsAsync(1, 20, 2, Ct)).Value!);
        Assert.Equal(new ushort[] { 1, 2, 3 }, (await _client.ReadHoldingRegistersAsync(1, 30, 3, Ct)).Value!);
        Assert.Equal(new ushort[] { 400, 401 }, (await _client.ReadInputRegistersAsync(1, 40, 2, Ct)).Value!);

        Assert.True((await _client.WriteSingleCoilAsync(1, 11, true, Ct)).IsGood);
        Assert.True((await _client.WriteSingleRegisterAsync(1, 31, 222, Ct)).IsGood);
        Assert.True((await _client.WriteMultipleCoilsAsync(1, 100, new[] { true, true, false, true, true, true, true, true, true }, Ct)).IsGood);
        Assert.True((await _client.WriteMultipleRegistersAsync(1, 200, new ushort[] { 7, 8, 9 }, Ct)).IsGood);
        var both = await _client.ReadWriteMultipleRegistersAsync(1, 200, 4, 203, new ushort[] { 10 }, Ct);

        Assert.Equal([true, true, true], _store.GetCoils(10, 3));
        Assert.Equal([1, 222, 3], _store.GetHoldingRegisters(30, 3));
        Assert.Equal([true, true, false, true, true, true, true, true, true], _store.GetCoils(100, 9));
        Assert.Equal([7, 8, 9, 10], both.Value!); // written before read
    }

    [Fact]
    public async Task LargeReadsAndWritesAreSplitIntoProtocolSizedRequests()
    {
        var values = Enumerable.Range(0, 1000).Select(i => (ushort)(i * 7)).ToArray();
        _counting.Reset();
        Assert.True((await _client.WriteMultipleRegistersAsync(1, 5000, values, Ct)).IsGood);
        Assert.Equal(9, _counting.Requests);  // 8 x 123 + 16
        Assert.Equal(123, _counting.Largest);

        _counting.Reset();
        var read = await _client.ReadHoldingRegistersAsync(1, 5000, 1000, Ct);
        Assert.Equal(values, read.Value!);
        Assert.Equal(8, _counting.Requests);  // 7 x 125 + 125
        Assert.Equal(125, _counting.Largest);

        var bits = Enumerable.Range(0, 5000).Select(i => i % 3 == 0).ToArray();
        Assert.True((await _client.WriteMultipleCoilsAsync(1, 0, bits, Ct)).IsGood);
        Assert.Equal(bits, (await _client.ReadCoilsAsync(1, 0, 5000, Ct)).Value!);
    }

    [Fact]
    public async Task SmallerLimitsForPickyDevicesAreHonoured()
    {
        await using var picky = new ModbusTcpClient(new ModbusClientOptions { MaxRegistersPerRead = 10, AlwaysWriteMultiple = true });
        await picky.ConnectAsync("127.0.0.1", _server.Port, Ct);
        _counting.Reset();
        Assert.True((await picky.ReadHoldingRegistersAsync(1, 0, 25, Ct)).IsGood);
        Assert.Equal(3, _counting.Requests);
        Assert.Equal(10, _counting.Largest);
        _counting.Reset();
        Assert.True((await picky.WriteSingleRegisterAsync(1, 0, 5, Ct)).IsGood);
        Assert.Equal(ModbusFunctionCode.WriteMultipleRegisters, _counting.LastFunction);
    }

    [Fact]
    public async Task TypedValuesRoundTripInEveryLayout()
    {
        var points = new (ModbusPoint Point, Variant Value)[]
        {
            (new(ModbusTable.HoldingRegisters, 1000, ModbusDataType.Float32) { WordOrder = ModbusWordOrder.LowWordFirst }, Variant.From(21.5f)),
            (new(ModbusTable.HoldingRegisters, 1002, ModbusDataType.Float64) { ByteOrder = ModbusByteOrder.LowByteFirst }, Variant.From(-0.125)),
            (new(ModbusTable.HoldingRegisters, 1006, ModbusDataType.Int32), Variant.From(-123456)),
            (new(ModbusTable.HoldingRegisters, 1008, ModbusDataType.UInt64) { WordOrder = ModbusWordOrder.LowWordFirst, ByteOrder = ModbusByteOrder.LowByteFirst }, Variant.From(9_000_000_000UL)),
            (new(ModbusTable.HoldingRegisters, 1012, ModbusDataType.Int16) { ArrayLength = 3 }, Variant.FromArray(new short[] { -1, 0, 1 })),
            (new(ModbusTable.HoldingRegisters, 1015, ModbusDataType.String) { StringLength = 9 }, Variant.From("Line 3 OK")),
            (new(ModbusTable.HoldingRegisters, 1020, ModbusDataType.UInt16), Variant.From((ushort)65535)),
            (new(ModbusTable.Coils, 1000, ModbusDataType.Boolean), Variant.From(true)),
            (new(ModbusTable.Coils, 1001, ModbusDataType.Boolean) { ArrayLength = 4 }, Variant.FromArray(new[] { true, false, false, true })),
        };
        foreach (var (point, value) in points)
        {
            var written = await _client.WriteValueAsync(1, point, value, Ct);
            Assert.True(written.IsGood, $"{point}: {written}");
            var read = await _client.ReadValueAsync(1, point, Ct);
            Assert.True(read.Status.IsGood, $"{point}: {read}");
            Assert.Equal(value, read.Value);
            Assert.NotNull(read.ServerTimestamp);
        }

        _store.SetInputRegisters(7, [0b0000_0000_0010_0000]);
        var bit5 = await _client.ReadValueAsync(1, new ModbusPoint(ModbusTable.InputRegisters, 7, ModbusDataType.Boolean) { BitIndex = 5 }, Ct);
        Assert.Equal(Variant.From(true), bit5.Value);

        var readOnly = await _client.WriteValueAsync(1, new ModbusPoint(ModbusTable.InputRegisters, 0, ModbusDataType.UInt16), Variant.From((ushort)1), Ct);
        Assert.Equal(StatusCodes.BadNotWritable, readOnly.Status);
        var tooBig = await _client.WriteValueAsync(1, new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.Int16), Variant.From(40000), Ct);
        Assert.Equal(StatusCodes.BadOutOfRange, tooBig.Status);
    }

    [Fact]
    public async Task ServerExceptionsComeBackAsResults()
    {
        var missing = await _client.ReadHoldingRegistersAsync(2, 8, 5, Ct); // unit 2 has 10 registers
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, missing.Exception);
        Assert.Equal(StatusCodes.BadOutOfRange, missing.Status);
        Assert.Null(missing.Value);

        var diagnostics = await _client.SendRawAsync(1, new byte[] { 0x08, 0x00, 0x00, 0x12, 0x34 }, Ct);
        Assert.Equal(ModbusExceptionCode.IllegalFunction, diagnostics.Exception);

        var failing = await _client.ReadHoldingRegistersAsync(3, 0, 1, Ct);
        Assert.Equal(ModbusExceptionCode.ServerDeviceFailure, failing.Exception);

        var value = await _client.ReadValueAsync(2, new ModbusPoint(ModbusTable.HoldingRegisters, 9, ModbusDataType.Float32), Ct);
        Assert.Equal(StatusCodes.BadOutOfRange, value.Status);

        // The connection survives all of that.
        Assert.True((await _client.ReadHoldingRegistersAsync(1, 0, 1, Ct)).IsGood);
        Assert.Equal(ModbusConnectionState.Connected, _client.State);
    }

    [Fact]
    public async Task UnitIdsSelectTheirOwnData()
    {
        _store.SetHoldingRegisters(0, [111]);
        _unit2.SetHoldingRegisters(0, [222]);
        Assert.Equal(new ushort[] { 111 }, (await _client.ReadHoldingRegistersAsync(1, 0, 1, Ct)).Value!);
        Assert.Equal(new ushort[] { 222 }, (await _client.ReadHoldingRegistersAsync(2, 0, 1, Ct)).Value!);
        Assert.Equal(new ushort[] { 111 }, (await _client.ReadHoldingRegistersAsync(247, 0, 1, Ct)).Value!); // default unit
    }

    [Fact]
    public async Task UnknownUnitsGetTheGatewayExceptionOrSilence()
    {
        var store = new ModbusDataStore();
        await using var gateway = await ModbusTcpServer.StartAsync(new ModbusServerOptions
        {
            Address = System.Net.IPAddress.Loopback, Port = 0, Units = new Dictionary<byte, IModbusDataStore> { [1] = store },
        });
        await using var single = await ModbusTcpServer.StartAsync(new ModbusServerOptions
        {
            Address = System.Net.IPAddress.Loopback, Port = 0, Units = new Dictionary<byte, IModbusDataStore> { [1] = store },
            UnknownUnit = ModbusUnknownUnit.Ignore,
        });
        var options = new ModbusClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(300), TimeoutsBeforeReconnect = 0 };
        await using var a = new ModbusTcpClient(options);
        await using var b = new ModbusTcpClient(options);
        await a.ConnectAsync("127.0.0.1", gateway.Port, Ct);
        await b.ConnectAsync("127.0.0.1", single.Port, Ct);

        var viaGateway = await a.ReadHoldingRegistersAsync(9, 0, 1, Ct);
        Assert.Equal(ModbusExceptionCode.GatewayTargetDeviceFailedToRespond, viaGateway.Exception);
        Assert.Equal(StatusCodes.BadNoCommunication, viaGateway.Status);

        Assert.Equal(StatusCodes.BadTimeout, (await b.ReadHoldingRegistersAsync(9, 0, 1, Ct)).Status);
        Assert.True((await b.ReadHoldingRegistersAsync(1, 0, 1, Ct)).IsGood);
    }

    [Fact]
    public async Task PipelinedRequestsAreMatchedByTransactionId()
    {
        var values = Enumerable.Range(0, 2000).Select(i => (ushort)(i ^ 0x5A5A)).ToArray();
        _store.SetHoldingRegisters(0, values);
        await using var fast = new ModbusTcpClient(new ModbusClientOptions { MaxConcurrentRequests = 16 });
        await fast.ConnectAsync("127.0.0.1", _server.Port, Ct);

        var reads = Enumerable.Range(0, 400).Select(async i =>
        {
            var address = (ushort)(i * 5 % 1990);
            var result = await fast.ReadHoldingRegistersAsync(1, address, 10, Ct);
            Assert.True(result.IsGood, result.ToString());
            Assert.Equal(values.AsSpan(address, 10).ToArray(), result.Value!);
        });
        await Task.WhenAll(reads);
    }

    [Fact]
    public async Task WritesTriggerTheStoreEventForTheApplication()
    {
        var written = new TaskCompletionSource<ModbusWrite>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.Written += (_, e) => written.TrySetResult(e);
        await _client.WriteSingleRegisterAsync(1, 4321, 1, Ct);
        Assert.Equal(new ModbusWrite(ModbusTable.HoldingRegisters, 4321, 1), await written.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task ArgumentsOutsideTheProtocolAreProgrammingErrors()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadHoldingRegistersAsync(1, 0, 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadHoldingRegistersAsync(1, 65535, 2, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.WriteMultipleRegistersAsync(1, 0, ReadOnlyMemory<ushort>.Empty, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadWriteMultipleRegistersAsync(1, 0, 126, 0, new ushort[] { 1 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadWriteMultipleRegistersAsync(1, 0, 1, 0, new ushort[122], Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SendRawAsync(1, new byte[254], Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusTcpClient(new ModbusClientOptions { MaxRegistersPerRead = 126 }));
    }

    /// <summary>Counts requests that reach the store, to check how reads and writes were split.</summary>
    private sealed class CountingStore(IModbusDataStore inner) : IModbusDataStore
    {
        private int _requests;
        private int _largest;

        public int Requests => _requests;
        public int Largest => _largest;
        public ModbusFunctionCode LastFunction { get; private set; }

        public void Reset() => (_requests, _largest) = (0, 0);

        private void Count(int quantity, ModbusFunctionCode function)
        {
            Interlocked.Increment(ref _requests);
            _largest = Math.Max(_largest, quantity);
            LastFunction = function;
        }

        public ModbusExceptionCode ReadCoils(ushort address, Span<bool> values) { Count(values.Length, ModbusFunctionCode.ReadCoils); return inner.ReadCoils(address, values); }
        public ModbusExceptionCode ReadDiscreteInputs(ushort address, Span<bool> values) { Count(values.Length, ModbusFunctionCode.ReadDiscreteInputs); return inner.ReadDiscreteInputs(address, values); }
        public ModbusExceptionCode ReadHoldingRegisters(ushort address, Span<ushort> values) { Count(values.Length, ModbusFunctionCode.ReadHoldingRegisters); return inner.ReadHoldingRegisters(address, values); }
        public ModbusExceptionCode ReadInputRegisters(ushort address, Span<ushort> values) { Count(values.Length, ModbusFunctionCode.ReadInputRegisters); return inner.ReadInputRegisters(address, values); }
        public ModbusExceptionCode WriteCoils(ushort address, ReadOnlySpan<bool> values) { Count(values.Length, values.Length == 1 ? ModbusFunctionCode.WriteSingleCoil : ModbusFunctionCode.WriteMultipleCoils); return inner.WriteCoils(address, values); }
        public ModbusExceptionCode WriteHoldingRegisters(ushort address, ReadOnlySpan<ushort> values) { Count(values.Length, ModbusFunctionCode.WriteMultipleRegisters); return inner.WriteHoldingRegisters(address, values); }
        public ModbusExceptionCode WriteThenReadHoldingRegisters(ushort writeAddress, ReadOnlySpan<ushort> values, ushort readAddress, Span<ushort> result) =>
            inner.WriteThenReadHoldingRegisters(writeAddress, values, readAddress, result);
    }

    private sealed class FailingStore : IModbusDataStore
    {
        public ModbusExceptionCode ReadCoils(ushort address, Span<bool> values) => throw new InvalidOperationException("broken");
        public ModbusExceptionCode ReadDiscreteInputs(ushort address, Span<bool> values) => throw new InvalidOperationException("broken");
        public ModbusExceptionCode ReadHoldingRegisters(ushort address, Span<ushort> values) => throw new InvalidOperationException("broken");
        public ModbusExceptionCode ReadInputRegisters(ushort address, Span<ushort> values) => throw new InvalidOperationException("broken");
        public ModbusExceptionCode WriteCoils(ushort address, ReadOnlySpan<bool> values) => throw new InvalidOperationException("broken");
        public ModbusExceptionCode WriteHoldingRegisters(ushort address, ReadOnlySpan<ushort> values) => throw new InvalidOperationException("broken");
    }
}
