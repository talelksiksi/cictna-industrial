using System.Collections.Concurrent;
using System.Net;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.IntegrationTests;

namespace Cictna.Industrial.Modbus.IntegrationTests;

/// <summary>Timeouts, lost connections, server restarts and broken networks.</summary>
public sealed class ResilienceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModbusClientOptions FastReconnect(TimeSpan? requestTimeout = null) => new()
    {
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(2),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        Reconnect = new ModbusReconnectOptions { InitialDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(500) },
    };

    private static Task<ModbusTcpServer> StartServerAsync(ModbusDataStore store, int port = 0) =>
        ModbusTcpServer.StartAsync(new ModbusServerOptions { Address = IPAddress.Loopback, Port = port, DefaultUnit = store });

    private static async Task WaitForStateAsync(ModbusTcpClient client, ModbusConnectionState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (client.State != state)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Expected {state} within {timeout}, still {client.State}");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task ReconnectsAfterTheServerDropsTheConnection()
    {
        var store = new ModbusDataStore();
        store.SetHoldingRegisters(0, [42]);
        await using var server = await StartServerAsync(store);
        await using var client = new ModbusTcpClient(FastReconnect());
        var states = new ConcurrentQueue<ModbusConnectionState>();
        client.StateChanged += (_, e) => states.Enqueue(e.State);
        await client.ConnectAsync("127.0.0.1", server.Port, Ct);

        server.DisconnectAll();
        await WaitForStateAsync(client, ModbusConnectionState.Connected, TimeSpan.FromSeconds(10));
        // The loss may only be seen on the next request, so wait for the round trip to settle.
        var read = await RetryUntilGoodAsync(() => client.ReadHoldingRegistersAsync(1, 0, 1, Ct));
        Assert.Equal([42], read.Value!);
        Assert.Contains(ModbusConnectionState.Reconnecting, states);
    }

    [Fact]
    public async Task ReconnectsAfterTheServerRestarts()
    {
        var store = new ModbusDataStore();
        var server = await StartServerAsync(store);
        var port = server.Port;
        await using var client = new ModbusTcpClient(FastReconnect());
        await client.ConnectAsync("127.0.0.1", port, Ct);

        await server.DisposeAsync();
        await WaitForStateAsync(client, ModbusConnectionState.Reconnecting, TimeSpan.FromSeconds(10));
        var whileDown = await client.ReadHoldingRegistersAsync(1, 0, 1, Ct);
        Assert.Equal(StatusCodes.BadNotConnected, whileDown.Status);

        await Task.Delay(800, Ct); // several failed attempts while the server is away
        store.SetHoldingRegisters(0, [7]);
        await using var restarted = await StartServerAsync(store, port);
        await WaitForStateAsync(client, ModbusConnectionState.Connected, TimeSpan.FromSeconds(10));
        Assert.Equal(new ushort[] { 7 }, (await client.ReadHoldingRegistersAsync(1, 0, 1, Ct)).Value!);
    }

    [Fact]
    public async Task RepeatedTimeoutsRebuildAConnectionThatLooksOpen()
    {
        var store = new ModbusDataStore();
        store.SetHoldingRegisters(0, [5]);
        await using var server = await StartServerAsync(store);
        await using var proxy = new TcpProxy(server.Port);
        var options = FastReconnect(TimeSpan.FromMilliseconds(300)) with { TimeoutsBeforeReconnect = 2 };
        await using var client = new ModbusTcpClient(options);
        var states = new ConcurrentQueue<ModbusConnectionStateChange>();
        client.StateChanged += (_, e) => states.Enqueue(e);
        await client.ConnectAsync("127.0.0.1", proxy.Port, Ct);
        var accepted = proxy.AcceptedConnections;

        proxy.Blackhole(true); // a cut cable: no error, no answer
        Assert.Equal(StatusCodes.BadTimeout, (await client.ReadHoldingRegistersAsync(1, 0, 1, Ct)).Status);
        Assert.Equal(ModbusConnectionState.Connected, client.State);
        Assert.Equal(StatusCodes.BadTimeout, (await client.ReadHoldingRegistersAsync(1, 0, 1, Ct)).Status);
        await WaitForStateAsync(client, ModbusConnectionState.Reconnecting, TimeSpan.FromSeconds(5));

        proxy.Blackhole(false);
        var read = await RetryUntilGoodAsync(() => client.ReadHoldingRegistersAsync(1, 0, 1, Ct));
        Assert.Equal([5], read.Value!);
        await Task.Delay(100, Ct); // state change events are delivered on their own task
        Assert.Contains(states, s => s.State == ModbusConnectionState.Reconnecting && s.Reason == StatusCodes.BadTimeout);
        Assert.True(proxy.AcceptedConnections > accepted, "a new connection was opened");
    }

    [Fact]
    public async Task ASeveredNetworkIsNoticedAndRestored()
    {
        var store = new ModbusDataStore();
        await using var server = await StartServerAsync(store);
        await using var proxy = new TcpProxy(server.Port);
        await using var client = new ModbusTcpClient(FastReconnect());
        await client.ConnectAsync("127.0.0.1", proxy.Port, Ct);

        proxy.SeverAll();
        await RetryUntilGoodAsync(() => client.WriteSingleRegisterAsync(1, 3, 33, Ct));
        Assert.Equal([33], store.GetHoldingRegisters(3, 1));
    }

    [Fact]
    public async Task WithoutReconnectALostConnectionEndsDisconnected()
    {
        await using var server = await StartServerAsync(new ModbusDataStore());
        await using var client = new ModbusTcpClient(new ModbusClientOptions { Reconnect = new() { Enabled = false } });
        var lost = new TaskCompletionSource<ModbusConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, e) =>
        {
            if (e.State == ModbusConnectionState.Disconnected)
            {
                lost.TrySetResult(e);
            }
        };
        await client.ConnectAsync("127.0.0.1", server.Port, Ct);
        server.DisconnectAll();
        var change = await lost.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(StatusCodes.BadConnectionClosed, change.Reason);
        Assert.Equal(StatusCodes.BadNotConnected, (await client.ReadCoilsAsync(1, 0, 1, Ct)).Status);

        await client.ConnectAsync("127.0.0.1", server.Port, Ct); // connecting again by hand works
        Assert.True((await client.ReadCoilsAsync(1, 0, 1, Ct)).IsGood);
    }

    [Fact]
    public async Task DisconnectCompletesWaitingRequestsAndStopsReconnecting()
    {
        await using var server = await StartServerAsync(new ModbusDataStore());
        await using var proxy = new TcpProxy(server.Port);
        await using var client = new ModbusTcpClient(FastReconnect(TimeSpan.FromSeconds(30)));
        await client.ConnectAsync("127.0.0.1", proxy.Port, Ct);
        proxy.Blackhole(true);

        var waiting = client.ReadHoldingRegistersAsync(1, 0, 1, Ct);
        await Task.Delay(200, Ct);
        await client.DisconnectAsync();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(StatusCodes.BadConnectionClosed, result.Status);
        Assert.Equal(ModbusConnectionState.Disconnected, client.State);
        await Task.Delay(500, Ct);
        Assert.Equal(ModbusConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task CancellingAWaitingRequestThrowsAndKeepsTheConnection()
    {
        var store = new ModbusDataStore();
        await using var server = await StartServerAsync(store);
        await using var proxy = new TcpProxy(server.Port);
        await using var client = new ModbusTcpClient(FastReconnect(TimeSpan.FromSeconds(30)) with { TimeoutsBeforeReconnect = 0 });
        await client.ConnectAsync("127.0.0.1", proxy.Port, Ct);
        proxy.Blackhole(true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadHoldingRegistersAsync(1, 0, 1, cts.Token));
        Assert.Equal(ModbusConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task ConnectFailuresAreExplained()
    {
        // A port nobody listens on: the server's own port, after it stopped.
        var server = await StartServerAsync(new ModbusDataStore());
        var port = server.Port;
        await server.DisposeAsync();

        await using var client = new ModbusTcpClient(new ModbusClientOptions { ConnectTimeout = TimeSpan.FromSeconds(3) });
        var refused = await Assert.ThrowsAsync<ModbusException>(() => client.ConnectAsync("127.0.0.1", port, Ct));
        Assert.Equal(StatusCodes.BadNotConnected, refused.StatusCode);
        Assert.Contains($"127.0.0.1:{port}", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ModbusConnectionState.Disconnected, client.State);

        var unknown = await Assert.ThrowsAsync<ModbusException>(() => client.ConnectAsync("no-such-device.invalid", 502, Ct));
        Assert.True(unknown.StatusCode == StatusCodes.BadInvalidArgument || unknown.StatusCode == StatusCodes.BadTimeout, unknown.Message);
    }

    [Fact]
    public async Task ConnectingTwiceIsAProgrammingError()
    {
        await using var server = await StartServerAsync(new ModbusDataStore());
        await using var client = new ModbusTcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync("127.0.0.1", server.Port, Ct));
    }

    [Fact]
    public async Task StateChangesArriveInOrder()
    {
        await using var server = await StartServerAsync(new ModbusDataStore());
        var client = new ModbusTcpClient(FastReconnect());
        var states = new ConcurrentQueue<ModbusConnectionState>();
        client.StateChanged += (_, e) => states.Enqueue(e.State);
        await client.ConnectAsync("127.0.0.1", server.Port, Ct);
        server.DisconnectAll();
        await RetryUntilGoodAsync(() => client.ReadCoilsAsync(1, 0, 1, Ct));
        await client.DisposeAsync(); // flushes the event queue
        Assert.Equal(
            [ModbusConnectionState.Connecting, ModbusConnectionState.Connected, ModbusConnectionState.Reconnecting, ModbusConnectionState.Connected, ModbusConnectionState.Disconnected],
            states);
    }

    private static async Task<T> RetryUntilGoodAsync<T>(Func<Task<T>> request) where T : struct
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var result = await request();
            var good = result switch
            {
                ModbusResult r => r.IsGood,
                ModbusResult<ushort[]> r => r.IsGood,
                ModbusResult<bool[]> r => r.IsGood,
                _ => false,
            };
            if (good)
            {
                return result;
            }
            Assert.True(DateTime.UtcNow < deadline, $"Still failing: {result}");
            await Task.Delay(50, Ct);
        }
    }
}
