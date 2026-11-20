using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus.InteropTests;

/// <summary>Starts one independent server per test class.</summary>
#pragma warning disable CA1816 // fixtures have no finalizers
public abstract class PeerServerFixture(string lib) : IAsyncLifetime
{
    private PeerServer? _server;

    public string Lib { get; } = lib;

    public int Port => _server!.Port;

    /// <summary>Why the server process is gone, or null while it runs.</summary>
    public string? Exited => _server?.Exited;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await ModbusPeers.EnsureAvailableAsync();
        }
        catch (Exception)
        {
            return; // the tests skip or fail through EnsureAvailableAsync themselves
        }
        _server = await ModbusPeers.StartServerAsync(Lib);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}

#pragma warning restore CA1816

public sealed class ModbusSerialServer() : PeerServerFixture("modbus-serial");

public sealed class JsModbusServer() : PeerServerFixture("jsmodbus");

/// <summary>
/// Our client against an independent server. Both servers hold the same data in addresses 0 to
/// 999: coils[i] = i % 3 == 0, discrete inputs[i] = i % 2 == 1, holding registers[i] = i * 3,
/// input registers[i] = 1000 + i.
/// </summary>
public abstract class OurClientTests<TServer>(TServer server) : IClassFixture<TServer>
    where TServer : PeerServerFixture
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected TServer Server { get; } = server;

    protected async Task<ModbusTcpClient> ConnectAsync(ModbusClientOptions? options = null)
    {
        await ModbusPeers.EnsureAvailableAsync();
        Assert.True(Server.Exited is null, $"The {Server.Lib} server is gone: {Server.Exited}");
        var client = new ModbusTcpClient(options ?? new ModbusClientOptions { RequestTimeout = TimeSpan.FromSeconds(3) });
        await client.ConnectAsync("127.0.0.1", Server.Port, Ct);
        return client;
    }

    [Fact]
    public async Task ReadsEveryTable()
    {
        await using var client = await ConnectAsync();
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i % 3 == 0), (await client.ReadCoilsAsync(1, 0, 10, Ct)).Value!);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i % 2 == 1), (await client.ReadDiscreteInputsAsync(1, 0, 10, Ct)).Value!);
        Assert.Equal(new ushort[] { 30, 33, 36, 39, 42 }, (await client.ReadHoldingRegistersAsync(1, 10, 5, Ct)).Value!);
        Assert.Equal(Enumerable.Range(1990, 10).Select(i => (ushort)i), (await client.ReadInputRegistersAsync(1, 990, 10, Ct)).Value!);
        // Odd bit counts that end inside a byte.
        Assert.Equal(Enumerable.Range(7, 13).Select(i => i % 3 == 0), (await client.ReadCoilsAsync(1, 7, 13, Ct)).Value!);
    }

    [Fact]
    public async Task LargeReadsAreSplitAndReassembled()
    {
        await using var client = await ConnectAsync(new ModbusClientOptions { MaxBitsPerRead = 300 });
        var registers = await client.ReadHoldingRegistersAsync(1, 0, 900, Ct);   // 8 requests of 125 or fewer
        Assert.Equal(Enumerable.Range(0, 900).Select(i => (ushort)(i * 3)), registers.Value!);
        var inputs = await client.ReadDiscreteInputsAsync(1, 0, 1000, Ct);      // 4 requests of 300 or fewer
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => i % 2 == 1), inputs.Value!);
    }

    // Writes use addresses from 900 on, which the read tests leave alone.
    [Fact]
    public async Task WritesAreReadBackTheSame()
    {
        await using var client = await ConnectAsync();
        Assert.True((await client.WriteSingleCoilAsync(1, 901, true, Ct)).IsGood);          // 901 % 3 != 0, so it was off
        Assert.True((await client.WriteSingleRegisterAsync(1, 902, 0xBEEF, Ct)).IsGood);
        bool[] coils = [true, true, false, true, false, false, true, true, true, false, true];
        Assert.True((await client.WriteMultipleCoilsAsync(1, 910, coils, Ct)).IsGood);
        Assert.True((await client.WriteMultipleRegistersAsync(1, 930, new ushort[] { 1, 0xFFFF, 0x8000 }, Ct)).IsGood);
        var flow = new ModbusPoint(ModbusTable.HoldingRegisters, 940, ModbusDataType.Float32) { WordOrder = ModbusWordOrder.LowWordFirst };
        Assert.True((await client.WriteValueAsync(1, flow, Variant.From(-12.75f), Ct)).IsGood);

        Assert.Equal(new[] { true }, (await client.ReadCoilsAsync(1, 901, 1, Ct)).Value!);
        Assert.Equal(new ushort[] { 0xBEEF }, (await client.ReadHoldingRegistersAsync(1, 902, 1, Ct)).Value!);
        Assert.Equal(coils, (await client.ReadCoilsAsync(1, 910, coils.Length, Ct)).Value!);
        Assert.Equal(new ushort[] { 1, 0xFFFF, 0x8000 }, (await client.ReadHoldingRegistersAsync(1, 930, 3, Ct)).Value!);
        Assert.Equal(Variant.From(-12.75f), (await client.ReadValueAsync(1, flow, Ct)).Value);
    }

    [Fact]
    public async Task PipelinedRequestsAreMatchedEvenWhenAnsweredOutOfOrder()
    {
        await using var client = await ConnectAsync(new ModbusClientOptions { MaxConcurrentRequests = 8 });
        var reads = Enumerable.Range(0, 60).Select(async i =>
        {
            var address = (ushort)(i * 13 % 950);
            var result = await client.ReadInputRegistersAsync(1, address, 5, Ct);
            Assert.Equal(Enumerable.Range(1000 + address, 5).Select(v => (ushort)v), result.Value!);
        });
        await Task.WhenAll(reads);
    }
}

public sealed class OurClientAgainstModbusSerial(ModbusSerialServer server) : OurClientTests<ModbusSerialServer>(server)
{
    [Fact]
    public async Task ExceptionsAndSilenceAreReportedAsResults()
    {
        await using var client = await ConnectAsync(new ModbusClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(800), TimeoutsBeforeReconnect = 0 });

        var pastTheEnd = await client.ReadHoldingRegistersAsync(1, 998, 5, Ct);
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, pastTheEnd.Exception);

        // modbus-serial's server does not implement function code 23.
        var readWrite = await client.ReadWriteMultipleRegistersAsync(1, 0, 1, 700, new ushort[] { 1 }, Ct);
        Assert.Equal(ModbusExceptionCode.IllegalFunction, readWrite.Exception);

        // It ignores other unit IDs, like a single device would.
        Assert.Equal(StatusCodes.BadTimeout, (await client.ReadHoldingRegistersAsync(9, 0, 1, Ct)).Status);
        Assert.True((await client.ReadHoldingRegistersAsync(1, 0, 1, Ct)).IsGood);
    }
}

public sealed class OurClientAgainstJsModbus(JsModbusServer server) : OurClientTests<JsModbusServer>(server)
{
    [Fact]
    public async Task AShortAnswerIsRefusedAndASilentConnectionIsRebuilt()
    {
        await using var client = await ConnectAsync(new ModbusClientOptions
        {
            RequestTimeout = TimeSpan.FromMilliseconds(800),
            TimeoutsBeforeReconnect = 2,
            Reconnect = new() { InitialDelay = TimeSpan.FromMilliseconds(100) },
        });

        // jsmodbus answers a read past its data with fewer registers than asked instead of
        // exception 02. Taking that as data would shift every value; the client refuses it.
        var pastTheEnd = await client.ReadHoldingRegistersAsync(1, 998, 5, Ct);
        Assert.Equal(StatusCodes.BadDecodingError, pastTheEnd.Status);
        Assert.Equal(new ushort[] { 0, 3 }, (await client.ReadHoldingRegistersAsync(1, 0, 2, Ct)).Value!);

        // It does not answer function code 23, and from then on answers nothing on that
        // connection: it looks open but is dead. The second timeout makes the client rebuild it.
        var readWrite = await client.ReadWriteMultipleRegistersAsync(1, 0, 1, 700, new ushort[] { 1 }, Ct);
        Assert.Equal(StatusCodes.BadTimeout, readWrite.Status);
        Assert.Equal(StatusCodes.BadTimeout, (await client.ReadHoldingRegistersAsync(1, 0, 2, Ct)).Status);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        ModbusResult<ushort[]> again;
        while (!(again = await client.ReadHoldingRegistersAsync(1, 0, 2, Ct)).IsGood)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Still failing: {again}");
            await Task.Delay(50, Ct);
        }
        Assert.Equal(new ushort[] { 0, 3 }, again.Value!);
    }
}
