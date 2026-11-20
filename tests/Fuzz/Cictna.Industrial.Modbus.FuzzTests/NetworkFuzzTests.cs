using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Cictna.Industrial.Core;
using Cictna.Industrial.Modbus.Protocol;
using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.Modbus.FuzzTests;

/// <summary>
/// Garbage over real sockets: a server fed mutated frames in random pieces, and a client whose
/// server answers with mutated, duplicated, missing or cut-off responses. Both must keep working
/// and log no internal error.
/// </summary>
public sealed class NetworkFuzzTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheServerSurvivesMutatedFramesInRandomPieces()
    {
        var seed = Mutator.Seed(out _);
        var m = new Mutator(seed);
        var store = new ModbusDataStore();
        store.SetHoldingRegisters(0, [0x1234]);
        var errors = new ErrorLogger<ModbusTcpServer>();
        await using var server = await ModbusTcpServer.StartAsync(new ModbusServerOptions
        {
            Address = IPAddress.Loopback, Port = 0, DefaultUnit = store, MaxConnections = 64,
            IdleTimeout = TimeSpan.FromMilliseconds(300), FrameTimeout = TimeSpan.FromMilliseconds(300),
        }, errors);

        var connections = Mutator.Iterations(150);
        for (var c = 0; c < connections; c += 8)
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(k => SendGarbageAsync(server.Port, new Mutator(seed + c + k))));
        }

        // Still serving, still correct, nothing unexpected in the log.
        await using var client = new ModbusTcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port, Ct);
        var check = await client.ReadHoldingRegistersAsync(1, 0, 1, Ct);
        Assert.True(check.IsGood && check.Value![0] == 0x1234, $"{check}; seed {seed}");
        Assert.True(errors.Errors.IsEmpty, $"seed {seed}: {string.Join("\n", errors.Errors)}");
    }

    private static async Task SendGarbageAsync(int port, Mutator m)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, Ct);
        await using var stream = new NetworkStream(socket);
        var drain = DrainAsync(stream);
        try
        {
            var bytes = new List<byte>();
            for (var f = m.Random.Next(1, 30); f > 0; f--)
            {
                var pdu = m.ValidRequest();
                var frame = MbapHeader.Frame((ushort)m.Random.Next(65536), (byte)m.Random.Next(256), pdu);
                bytes.AddRange(m.Random.Next(4) switch
                {
                    0 => frame,                                   // valid
                    1 => MbapHeader.Frame(1, 1, m.Mutate(pdu)),   // valid header, mutated PDU
                    _ => m.Mutate(frame),                         // anything, header included
                });
            }
            var data = bytes.ToArray();
            for (var offset = 0; offset < data.Length;)
            {
                var piece = Math.Min(data.Length - offset, m.Random.Next(1, 64));
                await stream.WriteAsync(data.AsMemory(offset, piece), Ct);
                offset += piece;
                if (m.Random.Next(8) == 0)
                {
                    await Task.Delay(m.Random.Next(1, 5), Ct);
                }
            }
        }
        catch (IOException)
        {
            // the server closed a connection whose framing it could not trust
        }
        socket.Shutdown(SocketShutdown.Send);
        await drain;
    }

    private static async Task DrainAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (await stream.ReadAsync(buffer, cts.Token) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task TheClientSurvivesMutatedAnswers()
    {
        var seed = Mutator.Seed(out _);
        var m = new Mutator(seed);
        var store = new ModbusDataStore();
        store.SetHoldingRegisters(0, [0x0F0F, 0xF0F0]);
        await using var server = new HostileServer(store, m);

        await using var client = new ModbusTcpClient(new ModbusClientOptions
        {
            RequestTimeout = TimeSpan.FromMilliseconds(150),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxConcurrentRequests = 4,
            TimeoutsBeforeReconnect = 3,
            Reconnect = new() { InitialDelay = TimeSpan.FromMilliseconds(20), MaxDelay = TimeSpan.FromMilliseconds(100) },
        });
        await client.ConnectAsync("127.0.0.1", server.Port, Ct);

        var requests = Mutator.Iterations(1500);
        var random = new Random(seed);
        for (var r = 0; r < requests; r += 4)
        {
            var batch = Enumerable.Range(0, 4).Select(_ => AnyRequestAsync(client, random.Next(6), (ushort)random.Next(0, 60000)));
            var all = Task.WhenAll(batch);
            // Every request ends within its timeout (plus scheduling slack), never with an exception.
            await all.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        server.Sane = true;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        ModbusResult<ushort[]> read;
        while (!(read = await client.ReadHoldingRegistersAsync(1, 0, 2, Ct)).IsGood)
        {
            Assert.True(DateTime.UtcNow < deadline, $"No recovery: {read}, state {client.State}; seed {seed}");
            await Task.Delay(50, Ct);
        }
        Assert.Equal(new ushort[] { 0x0F0F, 0xF0F0 }, read.Value!);
    }

    private static async Task AnyRequestAsync(ModbusTcpClient client, int kind, ushort address)
    {
        switch (kind)
        {
            case 0: await client.ReadCoilsAsync(1, address, 37, Ct); break;
            case 1: await client.ReadHoldingRegistersAsync(1, address, 3, Ct); break;
            case 2: await client.WriteSingleRegisterAsync(1, address, 7, Ct); break;
            case 3: await client.WriteMultipleCoilsAsync(1, address, new[] { true, false, true }, Ct); break;
            case 4: await client.ReadWriteMultipleRegistersAsync(1, address, 2, address, new ushort[] { 1 }, Ct); break;
            default: await client.ReadValueAsync(1, new ModbusPoint(ModbusTable.InputRegisters, address, ModbusDataType.Float64), Ct); break;
        }
    }

    /// <summary>
    /// Answers each request with the real response, mutated (header included), duplicated, cut
    /// off, split into pieces, delayed, not at all, or by closing the connection.
    /// </summary>
    private sealed class HostileServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly IModbusDataStore _store;
        private readonly Mutator _m;
        private readonly Lock _gate = new();
        private readonly Task _loop;

        public HostileServer(IModbusDataStore store, Mutator m)
        {
            _store = store;
            _m = m;
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public volatile bool Sane;

        private int Next(int max)
        {
            lock (_gate)
            {
                return _m.Random.Next(max);
            }
        }

        private byte[] Mutate(byte[] data)
        {
            lock (_gate)
            {
                return _m.Mutate(data);
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var socket = await _listener.AcceptSocketAsync(_stop.Token);
                    _ = ServeAsync(socket);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        private async Task ServeAsync(Socket socket)
        {
            using var _ = socket;
            await using var stream = new NetworkStream(socket);
            var header = new byte[7];
            var response = new byte[ModbusPdu.MaxLength];
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    var pdu = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
                    await stream.ReadExactlyAsync(pdu, _stop.Token);
                    var length = ModbusRequestHandler.Handle(_store, pdu, response);
                    var frame = MbapHeader.Frame(BinaryPrimitives.ReadUInt16BigEndian(header), header[6], response.AsSpan(0, length));
                    if (Sane)
                    {
                        await stream.WriteAsync(frame, _stop.Token);
                        continue;
                    }
                    switch (Next(10))
                    {
                        case 0: break;                                                     // no answer
                        case 1: socket.Close(); return;                                    // hang up
                        case 2: await stream.WriteAsync(frame.AsMemory(0, Next(frame.Length)), _stop.Token); break; // cut off
                        case 3: await stream.WriteAsync(frame.Concat(frame).ToArray(), _stop.Token); break;          // twice
                        case 4:
                            foreach (var b in frame)
                            {
                                await stream.WriteAsync(new[] { b }, _stop.Token);                                    // byte by byte
                            }
                            break;
                        case 5: await Task.Delay(Next(300), _stop.Token); await stream.WriteAsync(frame, _stop.Token); break; // late
                        case 6: await stream.WriteAsync(MbapHeader.Frame(BinaryPrimitives.ReadUInt16BigEndian(header), header[6], Mutate(response.AsSpan(0, length).ToArray())), _stop.Token); break;
                        case 7: await stream.WriteAsync(Mutate(frame), _stop.Token); break;  // header may break too
                        default: await stream.WriteAsync(frame, _stop.Token); break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or EndOfStreamException or ArgumentOutOfRangeException)
            {
                // the client hung up or rebuilt its connection
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }

    /// <summary>Collects Error-level log entries (internal failures).</summary>
    private sealed class ErrorLogger<T> : ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Enqueue($"{formatter(state, exception)} {exception}");
            }
        }
    }
}
