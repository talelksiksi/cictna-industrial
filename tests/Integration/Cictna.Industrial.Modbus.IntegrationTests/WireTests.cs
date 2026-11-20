using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus.IntegrationTests;

/// <summary>
/// What happens on the wire when peers are slow, split frames, merge them, or send nonsense:
/// a scripted server for the client, a raw socket for the server.
/// </summary>
public sealed class WireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    /// <summary>An FC 3 response whose register values are address, address + 1, ...</summary>
    private static byte[] Answer(byte[] request)
    {
        var tid = BinaryPrimitives.ReadUInt16BigEndian(request);
        var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8));
        var count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10));
        var frame = new byte[9 + count * 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, tid);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(3 + count * 2));
        frame[6] = request[6];
        frame[7] = 3;
        frame[8] = (byte)(count * 2);
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(9 + i * 2), (ushort)(address + i));
        }
        return frame;
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, Ct);
        var frame = new byte[6 + BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4))];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(7), Ct);
        return frame;
    }

    /// <summary>A one-connection server running <paramref name="script"/>.</summary>
    private static (int Port, Task Done) ScriptedServer(Func<NetworkStream, Task> script)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var done = Task.Run(async () =>
        {
            try
            {
                using var socket = await listener.AcceptSocketAsync(Ct);
                await using var stream = new NetworkStream(socket);
                await script(stream);
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, done);
    }

    [Fact]
    public async Task ALateAnswerAfterATimeoutIsNotTakenForTheNextOne()
    {
        var (port, done) = ScriptedServer(async stream =>
        {
            var first = await ReadFrameAsync(stream);   // answered too late
            var second = await ReadFrameAsync(stream);
            await stream.WriteAsync(Answer(first), Ct);
            await stream.WriteAsync(Answer(second), Ct);
            await Task.Delay(200, Ct);
        });
        await using var client = new ModbusTcpClient(new ModbusClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(400), TimeoutsBeforeReconnect = 0 });
        await client.ConnectAsync("127.0.0.1", port, Ct);

        var first = await client.ReadHoldingRegistersAsync(1, 100, 2, Ct);
        Assert.Equal(StatusCodes.BadTimeout, first.Status);
        var second = await client.ReadHoldingRegistersAsync(1, 500, 2, Ct);
        Assert.Equal([500, 501], second.Value!); // not 100, 101
        await done;
    }

    [Fact]
    public async Task AnswersSplitIntoSingleBytesOrMergedIntoOnePacketAreReassembled()
    {
        var (port, done) = ScriptedServer(async stream =>
        {
            var a = await ReadFrameAsync(stream);
            foreach (var b in Answer(a))
            {
                await stream.WriteAsync(new[] { b }, Ct);
                await stream.FlushAsync(Ct);
                await Task.Delay(2, Ct);
            }
            var c = await ReadFrameAsync(stream);
            var d = await ReadFrameAsync(stream);
            await stream.WriteAsync(Answer(d).Concat(Answer(c)).ToArray(), Ct); // out of order, one write
            await Task.Delay(200, Ct);
        });
        await using var client = new ModbusTcpClient(new ModbusClientOptions { MaxConcurrentRequests = 2 });
        await client.ConnectAsync("127.0.0.1", port, Ct);

        Assert.Equal(new ushort[] { 10, 11, 12 }, (await client.ReadHoldingRegistersAsync(1, 10, 3, Ct)).Value!);
        var c = client.ReadHoldingRegistersAsync(1, 20, 1, Ct);
        await Task.Delay(100, Ct); // make sure c is on the wire first
        var d = client.ReadHoldingRegistersAsync(1, 30, 1, Ct);
        Assert.Equal(new ushort[] { 20 }, (await c).Value!);
        Assert.Equal(new ushort[] { 30 }, (await d).Value!);
        await done;
    }

    [Theory]
    [InlineData("03 02 0001")]       // byte count for one register, two were asked for
    [InlineData("04 04 0001 0002")]  // another function
    [InlineData("03 04 0001")]       // short data
    public async Task AnAnswerThatDoesNotFitTheRequestIsADecodingError(string pdu)
    {
        var (port, done) = ScriptedServer(async stream =>
        {
            var request = await ReadFrameAsync(stream);
            var body = Hex(pdu);
            var frame = new byte[7 + body.Length];
            request.AsSpan(0, 4).CopyTo(frame);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(body.Length + 1));
            frame[6] = 1;
            body.CopyTo(frame, 7);
            await stream.WriteAsync(frame, Ct);
            var next = await ReadFrameAsync(stream);   // the connection is still usable
            await stream.WriteAsync(Answer(next), Ct);
            await Task.Delay(200, Ct);
        });
        await using var client = new ModbusTcpClient();
        await client.ConnectAsync("127.0.0.1", port, Ct);
        Assert.Equal(StatusCodes.BadDecodingError, (await client.ReadHoldingRegistersAsync(1, 0, 2, Ct)).Status);
        Assert.Equal(new ushort[] { 9 }, (await client.ReadHoldingRegistersAsync(1, 9, 1, Ct)).Value!);
        await done;
    }

    [Fact]
    public async Task AnImpossibleFrameLengthEndsTheConnection()
    {
        var (port, done) = ScriptedServer(async stream =>
        {
            await ReadFrameAsync(stream);
            await stream.WriteAsync(Hex("0000 0000 0400 01 03"), Ct); // length 1024
            await Task.Delay(500, Ct);
        });
        await using var client = new ModbusTcpClient(new ModbusClientOptions { Reconnect = new() { Enabled = false } });
        await client.ConnectAsync("127.0.0.1", port, Ct);
        var result = await client.ReadHoldingRegistersAsync(1, 0, 1, Ct);
        Assert.Equal(StatusCodes.BadDecodingError, result.Status);
        await done;
        Assert.Equal(ModbusConnectionState.Disconnected, client.State);
    }

    private static async Task<(ModbusTcpServer Server, Socket Socket, NetworkStream Stream)> RawClientAsync(ModbusServerOptions? options = null)
    {
        var store = new ModbusDataStore();
        store.SetHoldingRegisters(0, [0x1111, 0x2222]);
        var server = await ModbusTcpServer.StartAsync((options ?? new ModbusServerOptions()) with { Address = IPAddress.Loopback, Port = 0, DefaultUnit = store });
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, server.Port, Ct);
        return (server, socket, new NetworkStream(socket, ownsSocket: true));
    }

    [Fact]
    public async Task TheServerReassemblesRequestsSentByteByByteAndAnswersMergedOnesInOrder()
    {
        var (server, _, stream) = await RawClientAsync();
        await using var _s = server;
        await using var _n = stream;

        foreach (var b in Hex("0001 0000 0006 01 03 0000 0001"))
        {
            await stream.WriteAsync(new[] { b }, Ct);
            await Task.Delay(2, Ct);
        }
        Assert.Equal(Hex("0001 0000 0005 01 03 02 1111"), await ReadFrameAsync(stream));

        await stream.WriteAsync(Hex("0002 0000 0006 01 03 0001 0001  0003 0000 0006 07 03 0000 0002"), Ct);
        Assert.Equal(Hex("0002 0000 0005 01 03 02 2222"), await ReadFrameAsync(stream));
        Assert.Equal(Hex("0003 0000 0007 07 03 04 1111 2222"), await ReadFrameAsync(stream));
    }

    [Fact]
    public async Task TheServerSkipsOtherProtocolsAndDropsImpossibleFrames()
    {
        var (server, _, stream) = await RawClientAsync();
        await using var _s = server;
        await using var _n = stream;

        await stream.WriteAsync(Hex("0001 0001 0006 01 03 0000 0001"), Ct);          // protocol 1: skipped
        await stream.WriteAsync(Hex("0002 0000 0006 01 03 0000 0001"), Ct);
        Assert.Equal(Hex("0002 0000 0005 01 03 02 1111"), await ReadFrameAsync(stream));

        await stream.WriteAsync(Hex("0003 0000 0001 01"), Ct);                       // length 1: no function code
        await AssertClosedByServerAsync(stream);
    }

    /// <summary>The server ends the connection within a few seconds (orderly or by reset).</summary>
    private static async Task AssertClosedByServerAsync(NetworkStream stream)
    {
        var buffer = new byte[16];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(0, await stream.ReadAsync(buffer, cts.Token));
        }
        catch (IOException)
        {
            // reset: closed as well
        }
    }

    [Fact]
    public async Task TheServerClosesIdleAndStalledConnections()
    {
        var (server, _, stream) = await RawClientAsync(new ModbusServerOptions { IdleTimeout = TimeSpan.FromMilliseconds(300), FrameTimeout = TimeSpan.FromMilliseconds(300) });
        await using var _s = server;
        await using var _n = stream;
        await AssertClosedByServerAsync(stream); // idle

        var (server2, _, stalled) = await RawClientAsync(new ModbusServerOptions { FrameTimeout = TimeSpan.FromMilliseconds(300) });
        await using var _s2 = server2;
        await using var _n2 = stalled;
        await stalled.WriteAsync(Hex("0001 0000 0006 01 03"), Ct); // half a frame, then nothing
        await AssertClosedByServerAsync(stalled);
    }

    [Fact]
    public async Task TheServerRefusesConnectionsAboveItsLimit()
    {
        await using var server = await ModbusTcpServer.StartAsync(new ModbusServerOptions { Address = IPAddress.Loopback, Port = 0, DefaultUnit = new ModbusDataStore(), MaxConnections = 2 });
        await using var a = new ModbusTcpClient(new ModbusClientOptions { Reconnect = new() { Enabled = false } });
        await using var b = new ModbusTcpClient(new ModbusClientOptions { Reconnect = new() { Enabled = false } });
        await using var c = new ModbusTcpClient(new ModbusClientOptions { Reconnect = new() { Enabled = false } });
        await a.ConnectAsync("127.0.0.1", server.Port, Ct);
        await b.ConnectAsync("127.0.0.1", server.Port, Ct);
        Assert.True((await b.ReadCoilsAsync(1, 0, 1, Ct)).IsGood); // both accepted before the third arrives
        try
        {
            await c.ConnectAsync("127.0.0.1", server.Port, Ct);    // TCP accepts, then the server closes it
            Assert.False((await c.ReadCoilsAsync(1, 0, 1, Ct)).IsGood);
        }
        catch (ModbusException)
        {
            // closed so quickly that connecting already failed
        }
        Assert.Equal(2, server.ConnectionCount);
        Assert.True((await a.ReadCoilsAsync(1, 0, 1, Ct)).IsGood);
    }
}
