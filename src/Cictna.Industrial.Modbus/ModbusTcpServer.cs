using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Cictna.Industrial.Modbus.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cictna.Industrial.Modbus;

/// <summary>What the server does with a request for a unit ID it has no data store for.</summary>
public enum ModbusUnknownUnit
{
    /// <summary>Answer with exception 0x0B (gateway target device failed to respond), as a gateway would. The default.</summary>
    RespondGatewayTargetFailed,
    /// <summary>Do not answer, as a single device on a shared line would; the client times out.</summary>
    Ignore,
}

/// <summary>Settings of a <see cref="ModbusTcpServer"/>.</summary>
public sealed record ModbusServerOptions
{
    /// <summary>Local address to listen on. Default: all IPv4 addresses.</summary>
    public IPAddress Address { get; init; } = IPAddress.Any;

    /// <summary>TCP port. Default 502 (needs administrator rights on Linux); 0 picks a free port.</summary>
    public int Port { get; init; } = 502;

    /// <summary>Data per unit ID. Units not listed here go to <see cref="DefaultUnit"/>.</summary>
    public IReadOnlyDictionary<byte, IModbusDataStore> Units { get; init; } = new Dictionary<byte, IModbusDataStore>();

    /// <summary>Data for every unit ID not in <see cref="Units"/>, or null to treat them as unknown.</summary>
    public IModbusDataStore? DefaultUnit { get; init; }

    /// <summary>What to do with requests for unknown units.</summary>
    public ModbusUnknownUnit UnknownUnit { get; init; } = ModbusUnknownUnit.RespondGatewayTargetFailed;

    /// <summary>Most clients connected at once; further connections are closed at once. Default 32.</summary>
    public int MaxConnections { get; init; } = 32;

    /// <summary>
    /// A connection that sends nothing for this long is closed. Default 5 minutes;
    /// <see cref="Timeout.InfiniteTimeSpan"/> keeps idle connections open.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Time allowed for the rest of a frame once its first bytes arrived. Default 5 s.</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// A Modbus TCP server: answers function codes 1, 2, 3, 4, 5, 6, 15, 16 and 23 from one
/// <see cref="IModbusDataStore"/> per unit ID. Other function codes get exception 01. Requests on
/// one connection are answered in order; connections are served in parallel.
/// </summary>
/// <remarks>
/// A frame whose length field is impossible (less than 2 or more than 254) ends the connection,
/// because the stream can no longer be split into frames. A frame with a protocol ID other than 0
/// is skipped without an answer.
/// </remarks>
/// <example>
/// <code>
/// var store = new ModbusDataStore();
/// await using var server = await ModbusTcpServer.StartAsync(new ModbusServerOptions { Port = 5020, DefaultUnit = store });
/// store.SetInputRegisters(0, [215]);
/// </code>
/// </example>
public sealed partial class ModbusTcpServer : IAsyncDisposable
{
    private readonly ModbusServerOptions _options;
    private readonly FrozenDictionary<byte, IModbusDataStore> _units;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<long, Socket> _connections = new();
    private readonly ConcurrentDictionary<long, Task> _connectionTasks = new();
    private Task _acceptLoop = Task.CompletedTask;
    private long _nextConnectionId;
    private int _disposed;

    private ModbusTcpServer(ModbusServerOptions options, ILogger? logger)
    {
        _options = options;
        _units = options.Units.ToFrozenDictionary();
        _logger = logger ?? NullLogger.Instance;
        _listener = new TcpListener(options.Address, options.Port);
        if (options.Address.Equals(IPAddress.IPv6Any))
        {
            _listener.Server.DualMode = true;
        }
    }

    /// <summary>Starts listening. The server runs until disposed.</summary>
    /// <param name="options">Port, units and limits.</param>
    /// <param name="logger">Receives connection and error logs; null for none.</param>
    /// <returns>The running server.</returns>
    /// <exception cref="ModbusException">The port is in use or not allowed (BadResourceUnavailable).</exception>
    public static Task<ModbusTcpServer> StartAsync(ModbusServerOptions options, ILogger<ModbusTcpServer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConnections, 1);
        var server = new ModbusTcpServer(options, logger);
        try
        {
            server._listener.Start();
        }
        catch (SocketException ex)
        {
            server._stopping.Dispose();
            throw new ModbusException(Core.StatusCodes.BadResourceUnavailable,
                $"Cannot listen on {options.Address}:{options.Port}: {ex.SocketErrorCode}", ex);
        }
        server.Port = ((IPEndPoint)server._listener.LocalEndpoint).Port;
        server._acceptLoop = server.AcceptLoopAsync();
        LogListening(server._logger, options.Address, server.Port, server._units.Count, options.DefaultUnit is not null);
        return Task.FromResult(server);
    }

    /// <summary>The port the server listens on (useful when <see cref="ModbusServerOptions.Port"/> is 0).</summary>
    public int Port { get; private set; }

    /// <summary>Number of clients connected now.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Closes every client connection; the server keeps listening and clients may connect again.
    /// Useful to test client reconnection.
    /// </summary>
    public void DisconnectAll()
    {
        foreach (var socket in _connections.Values)
        {
            Close(socket);
        }
    }

    /// <summary>Stops listening and closes every connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        DisconnectAll();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
            await Task.WhenAll(_connectionTasks.Values).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stopping.Dispose();
        LogStopped(_logger, Port);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || _stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                LogAcceptFailed(_logger, ex.SocketErrorCode);
                continue;
            }

            var remote = socket.RemoteEndPoint?.ToString() ?? "?";
            if (_connections.Count >= _options.MaxConnections)
            {
                ModbusTelemetry.ServerRefusedConnections.Add(1);
                LogRefused(_logger, remote, _options.MaxConnections);
                Close(socket);
                continue;
            }
            socket.NoDelay = true;
            var id = Interlocked.Increment(ref _nextConnectionId);
            _connections[id] = socket;
            ModbusTelemetry.ServerConnections.Add(1);
            LogConnected(_logger, remote, _connections.Count);
            var task = Task.Run(() => ServeAsync(id, socket, remote));
            _connectionTasks[id] = task;
            _ = task.ContinueWith(_ => _connectionTasks.TryRemove(id, out var _), TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(long id, Socket socket, string remote)
    {
        var header = new byte[MbapHeader.Size];
        var request = new byte[ModbusPdu.MaxLength];
        var response = new byte[MbapHeader.Size + ModbusPdu.MaxLength];
        var reason = "closed by client";
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            while (!_stopping.IsCancellationRequested)
            {
                // Wait (idle timeout) for the first byte, then give the rest of the frame FrameTimeout.
                var first = await ReadAsync(stream, header.AsMemory(0, 1), _options.IdleTimeout).ConfigureAwait(false);
                if (first != ReadOutcome.Done)
                {
                    reason = first == ReadOutcome.TimedOut ? "idle timeout" : "closed by client";
                    break;
                }
                if (await ReadAsync(stream, header.AsMemory(1), _options.FrameTimeout).ConfigureAwait(false) != ReadOutcome.Done)
                {
                    reason = "incomplete frame";
                    break;
                }
                var mbap = MbapHeader.Read(header);
                if (!mbap.HasValidLength)
                {
                    ModbusTelemetry.ServerProtocolErrors.Add(1);
                    reason = $"invalid frame length {mbap.Length}";
                    break;
                }
                var pdu = request.AsMemory(0, mbap.PduLength);
                if (await ReadAsync(stream, pdu, _options.FrameTimeout).ConfigureAwait(false) != ReadOutcome.Done)
                {
                    reason = "incomplete frame";
                    break;
                }
                if (mbap.ProtocolId != 0)
                {
                    ModbusTelemetry.ServerProtocolErrors.Add(1);
                    LogSkippedProtocol(_logger, remote, mbap.ProtocolId);
                    continue;
                }

                var length = Handle(mbap.UnitId, pdu.Span, response.AsSpan(MbapHeader.Size));
                if (length == 0)
                {
                    continue; // unknown unit, ignored
                }
                new MbapHeader(mbap.TransactionId, 0, (ushort)(length + 1), mbap.UnitId).Write(response);
                await stream.WriteAsync(response.AsMemory(0, MbapHeader.Size + length), _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            reason = _stopping.IsCancellationRequested ? "server stopping" : "connection lost";
        }
        finally
        {
            Close(socket);
            _connections.TryRemove(id, out _);
            ModbusTelemetry.ServerConnections.Add(-1);
            LogDisconnected(_logger, remote, reason);
        }
    }

    /// <summary>Answers one request PDU; returns the response length, or 0 for no answer.</summary>
    private int Handle(byte unitId, ReadOnlySpan<byte> request, Span<byte> response)
    {
        var function = request[0];
        int length;
        if (!_units.TryGetValue(unitId, out var store) && (store = _options.DefaultUnit) is null)
        {
            if (_options.UnknownUnit == ModbusUnknownUnit.Ignore)
            {
                return 0;
            }
            length = ModbusRequestHandler.Exception(response, function, ModbusExceptionCode.GatewayTargetDeviceFailedToRespond);
        }
        else
        {
            try
            {
                length = ModbusRequestHandler.Handle(store, request, response);
            }
#pragma warning disable CA1031 // A failing data store must not end the connection; the client gets exception 04.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStoreFailed(_logger, ex, unitId, function);
                length = ModbusRequestHandler.Exception(response, function, ModbusExceptionCode.ServerDeviceFailure);
            }
        }
        var exception = length == 2 && (response[0] & 0x80) != 0 ? (ModbusExceptionCode)response[1] : ModbusExceptionCode.None;
        ModbusTelemetry.ServerRequests.Add(1, new TagList { { "function", function }, { "exception", exception.ToString() } });
        return length;
    }

    private enum ReadOutcome { Done, Closed, TimedOut }

    /// <summary>Reads exactly <paramref name="buffer"/>, unless the stream ends or the time runs out.</summary>
    private async Task<ReadOutcome> ReadAsync(NetworkStream stream, Memory<byte> buffer, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(timeout);
        }
        try
        {
            await stream.ReadExactlyAsync(buffer, cts.Token).ConfigureAwait(false);
            return ReadOutcome.Done;
        }
        catch (EndOfStreamException)
        {
            return ReadOutcome.Closed;
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            return ReadOutcome.TimedOut;
        }
    }

    private static void Close(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both); // an orderly close where possible, so clients see the end of the stream
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
        socket.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Modbus TCP server listening on {Address}:{Port} ({Units} units, default unit {HasDefault})")]
    private static partial void LogListening(ILogger logger, IPAddress address, int port, int units, bool hasDefault);

    [LoggerMessage(Level = LogLevel.Information, Message = "Modbus TCP server on port {Port} stopped")]
    private static partial void LogStopped(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Modbus client {Remote} connected ({Count} open)")]
    private static partial void LogConnected(ILogger logger, string remote, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Modbus client {Remote} disconnected: {Reason}")]
    private static partial void LogDisconnected(ILogger logger, string remote, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Modbus client {Remote} refused: {Max} connections already open")]
    private static partial void LogRefused(ILogger logger, string remote, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Accepting a connection failed: {Error}")]
    private static partial void LogAcceptFailed(ILogger logger, SocketError error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Frame from {Remote} with protocol ID {ProtocolId} skipped")]
    private static partial void LogSkippedProtocol(ILogger logger, string remote, ushort protocolId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Data store for unit {UnitId} failed on function {Function}; answered exception 04")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception, byte unitId, byte function);
}
