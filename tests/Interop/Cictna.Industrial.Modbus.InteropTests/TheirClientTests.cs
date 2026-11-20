using System.Net;
using System.Text.Json;

namespace Cictna.Industrial.Modbus.InteropTests;

/// <summary>
/// Independent clients against our server: modbus-serial and jsmodbus run client.js, a fixed
/// sequence of reads, writes and errors; the test checks their report and our store.
/// </summary>
public sealed class TheirClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("modbus-serial")]
    [InlineData("jsmodbus")]
    public async Task AnIndependentClientReadsWritesAndGetsExceptions(string lib)
    {
        await ModbusPeers.EnsureAvailableAsync();
        var store = new ModbusDataStore(coils: 2000, discreteInputs: 1000, holdingRegisters: 1000, inputRegisters: 1000);
        store.SetCoils(0, Enumerable.Range(0, 2000).Select(i => i % 3 == 0).ToArray());
        store.SetDiscreteInputs(0, Enumerable.Range(0, 1000).Select(i => i % 2 == 1).ToArray());
        store.SetHoldingRegisters(0, Enumerable.Range(0, 1000).Select(i => (ushort)(i * 3)).ToArray());
        store.SetInputRegisters(0, Enumerable.Range(0, 1000).Select(i => (ushort)(1000 + i)).ToArray());
        await using var server = await ModbusTcpServer.StartAsync(new ModbusServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            Units = new Dictionary<byte, IModbusDataStore> { [1] = store }, // unit 9 is unknown: exception 0x0B
        });

        var report = await ModbusPeers.RunClientAsync(lib, server.Port);

        bool[] Bools(string step) => Data(report, step).EnumerateArray().Select(e => e.GetBoolean()).ToArray();
        int[] Ints(string step) => Data(report, step).EnumerateArray().Select(e => e.GetInt32()).ToArray();

        Assert.Equal(Enumerable.Range(0, 10).Select(i => i % 3 == 0), Bools("readCoils"));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i % 2 == 1), Bools("readDiscreteInputs"));
        Assert.Equal([30, 33, 36, 39, 42], Ints("readHoldingRegisters"));
        Assert.Equal(Enumerable.Range(1990, 10), Ints("readInputRegisters"));
        Assert.Equal(125, Data(report, "readMaxRegisters").GetInt32());
        Assert.Equal(667, Data(report, "readMaxCoils").GetInt32()); // 2000 coils in one request
        foreach (var write in new[] { "writeCoil", "writeRegister", "writeCoils", "writeRegisters" })
        {
            Assert.True(Data(report, write).GetBoolean(), write);
        }
        Assert.Equal(2, Exception(report, "readPastTheEnd"));

        Assert.True(store.GetCoils(500, 1)[0]);
        Assert.Equal([0xBEEF], store.GetHoldingRegisters(501, 1));
        Assert.Equal([true, false, true, true, false, true, false, true, true], store.GetCoils(510, 9));
        Assert.True(store.GetCoils(519, 1)[0]); // the padding bits of the last byte leave coil 519 as it was
        Assert.Equal([1, 2, 3], store.GetHoldingRegisters(520, 3));

        if (lib == "modbus-serial")
        {
            // Only modbus-serial's client has function code 23 and a settable unit ID.
            Assert.Equal([1, 2, 3], Ints("readWriteRegisters"));
            Assert.Equal([7, 8], store.GetHoldingRegisters(530, 2));
            Assert.Equal(11, Exception(report, "unknownUnit"));
        }
    }

    private static JsonElement Data(JsonElement report, string step)
    {
        var outcome = report.GetProperty(step);
        Assert.True(outcome.TryGetProperty("data", out var data), $"{step}: {outcome}");
        return data;
    }

    private static int Exception(JsonElement report, string step)
    {
        var outcome = report.GetProperty(step);
        Assert.True(outcome.TryGetProperty("exception", out var code), $"{step} should be an exception: {outcome}");
        return code.GetInt32();
    }
}
