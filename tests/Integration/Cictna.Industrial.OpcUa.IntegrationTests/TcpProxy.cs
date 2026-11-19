using System.Net;
using System.Net.Sockets;

namespace Cictna.Industrial.OpcUa.IntegrationTests;

/// <summary>
/// A TCP relay between a client and a server, used to break the network on purpose:
/// <list type="bullet">
/// <item><see cref="SeverAll"/>: closes every open connection, like a cable pulled or a switch
/// rebooting. Both sides notice at once.</item>
/// <item><see cref="Blackhole"/>: keeps the connections open but silently drops all bytes in both
/// directions, like a broken route or a firewall dropping packets. Nobody gets an error; only
/// timeouts and keep-alives reveal it. New connections are accepted but go nowhere too.</item>
/// </list>
/// </summary>
public sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(TcpClient Client, TcpClient Server)> _connections = [];
    private readonly Task _acceptLoop;
    private volatile bool _blackholed;

    /// <param name="targetPort">The server's port.</param>
    /// <param name="targetHost">The server's host; a name is resolved (IPv4 or IPv6, whichever answers).</param>
    public TcpProxy(int targetPort, string targetHost = "127.0.0.1")
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The proxy's own port; point the client here.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The client URL for this proxy.</summary>
    public string Url => $"opc.tcp://127.0.0.1:{Port}";

    /// <summary>Connections accepted so far (each reconnect adds one, plus discovery connections).</summary>
    public int AcceptedConnections { get; private set; }

    /// <summary>The last connection error inside the proxy, for diagnosing a test.</summary>
    public string? LastError { get; private set; }

    /// <summary>Drop all traffic silently (true) or forward it again (false, new connections only).</summary>
    public void Blackhole(bool on) => _blackholed = on;

    /// <summary>Closes all current connections at once. New connections work normally.</summary>
    public void SeverAll()
    {
        lock (_connections)
        {
            foreach (var (client, server) in _connections)
            {
                try
                {
                    client.Client?.LingerState = new LingerOption(true, 0); // reset, not a polite close
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // already closed by the pump
                }
                client.Dispose();
                server.Dispose();
            }
            _connections.Clear();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            AcceptedConnections++;
            var server = new TcpClient();
            try
            {
                await server.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                LastError = $"connect to {_targetHost}:{_targetPort}: {ex.Message}";
                client.Dispose(); // server down: refuse like the server itself would
                server.Dispose();
                continue;
            }
            lock (_connections)
            {
                _connections.Add((client, server));
            }
            _ = PumpAsync(client, server);
            _ = PumpAsync(server, client);
        }
    }

    private async Task PumpAsync(TcpClient from, TcpClient to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            var input = from.GetStream();
            var output = to.GetStream();
            while (true)
            {
                var n = await input.ReadAsync(buffer, _stop.Token);
                if (n == 0)
                {
                    break;
                }
                if (!_blackholed)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException or InvalidOperationException)
        {
            LastError = $"pump: {ex.Message}"; // connection closed or severed
        }
        from.Dispose();
        to.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        SeverAll();
        await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }
}
