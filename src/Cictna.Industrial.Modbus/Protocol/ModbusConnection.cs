using System.Collections.Concurrent;
using System.Net.Sockets;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus.Protocol;

/// <summary>
/// One TCP connection to a Modbus server: frames requests with transaction IDs, matches responses
/// to waiting requests, and reports the loss of the connection once. Several requests may be in
/// flight; a response nobody waits for any more (late after a timeout) is discarded.
/// </summary>
internal sealed class ModbusConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<byte[]>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Action<ModbusConnection, StatusCode> _onLost;
    private readonly Action<ushort> _onDiscarded;
    private Task _receiveLoop = Task.CompletedTask;
    private int _nextTransactionId;
    private int _failed;

    private ModbusConnection(Socket socket, Action<ModbusConnection, StatusCode> onLost, Action<ushort> onDiscarded)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _onLost = onLost;
        _onDiscarded = onDiscarded;
        _nextTransactionId = Random.Shared.Next(0, 65536);
    }

    /// <summary>Why the connection ended, or Good while it is open.</summary>
    public StatusCode Failure { get; private set; } = StatusCodes.Good;

    /// <summary>Opens the connection; throws <see cref="ModbusException"/> when it cannot.</summary>
    public static async Task<ModbusConnection> OpenAsync(
        string host, int port, TimeSpan timeout,
        Action<ModbusConnection, StatusCode> onLost, Action<ushort> onDiscarded, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModbusException(StatusCodes.BadTimeout, $"No connection to {host}:{port} within {timeout.TotalSeconds:0.#} s");
            }
            catch (SocketException ex)
            {
                var status = ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                    ? StatusCodes.BadInvalidArgument
                    : StatusCodes.BadNotConnected;
                throw new ModbusException(status, $"Cannot connect to {host}:{port}: {ex.SocketErrorCode}", ex);
            }
            EnableKeepAlive(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var connection = new ModbusConnection(socket, onLost, onDiscarded);
        connection._receiveLoop = Task.Run(connection.ReceiveLoopAsync, CancellationToken.None);
        return connection;
    }

    /// <summary>
    /// Sends one request and waits for its response PDU. Returns BadTimeout, BadConnectionClosed or
    /// BadNotConnected with no data when there is no answer. Throws only when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<(StatusCode Status, byte[]? Response)> SendAsync(byte unitId, byte[] pdu, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_failed != 0)
        {
            return (StatusCodes.BadNotConnected, null);
        }
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ushort transactionId;
        do
        {
            transactionId = (ushort)Interlocked.Increment(ref _nextTransactionId);
        }
        while (!_pending.TryAdd(transactionId, completion));
        if (_failed != 0)
        {
            // Lost between the check above and registering: nobody would complete this request.
            _pending.TryRemove(transactionId, out _);
            return (Failure, null);
        }

        try
        {
            // The frame is written in full even if the caller cancels, so the stream never holds half a frame.
            var frame = MbapHeader.Frame(transactionId, unitId, pdu);
            await _sendLock.WaitAsync(_closing.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame, _closing.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            _pending.TryRemove(transactionId, out _);
            Fail(StatusCodes.BadConnectionClosed);
            return (Failure.IsGood ? StatusCodes.BadConnectionClosed : Failure, null);
        }

        try
        {
            var response = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return (StatusCodes.Good, response);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(transactionId, out _);
            return (StatusCodes.BadTimeout, null);
        }
        catch (ConnectionFailedException ex)
        {
            return (ex.Status, null);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(transactionId, out _);
            throw;
        }
    }

    /// <summary>Ends the connection because of <paramref name="reason"/> and reports the loss (once).</summary>
    public void Fail(StatusCode reason)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }
        Failure = reason;
        Shutdown(reason);
        _onLost(this, reason);
    }

    /// <summary>Closes the connection without reporting a loss.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
        {
            Failure = StatusCodes.BadConnectionClosed;
            Shutdown(StatusCodes.BadConnectionClosed);
        }
        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _closing.Dispose();
        _sendLock.Dispose();
    }

    private void Shutdown(StatusCode reason)
    {
        try
        {
            _closing.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            _socket.Close(0);
        }
        catch (ObjectDisposedException)
        {
        }
        foreach (var transactionId in _pending.Keys)
        {
            if (_pending.TryRemove(transactionId, out var waiting))
            {
                waiting.TrySetException(new ConnectionFailedException(reason));
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var header = new byte[MbapHeader.Size];
        try
        {
            while (true)
            {
                await _stream.ReadExactlyAsync(header, _closing.Token).ConfigureAwait(false);
                var mbap = MbapHeader.Read(header);
                if (!mbap.HasValidLength || mbap.ProtocolId != 0)
                {
                    // The stream can no longer be trusted to split into frames.
                    Fail(StatusCodes.BadDecodingError);
                    return;
                }
                var pdu = new byte[mbap.PduLength];
                await _stream.ReadExactlyAsync(pdu, _closing.Token).ConfigureAwait(false);
                if (_pending.TryRemove(mbap.TransactionId, out var waiting))
                {
                    waiting.TrySetResult(pdu);
                }
                else
                {
                    _onDiscarded(mbap.TransactionId);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or EndOfStreamException)
        {
            Fail(StatusCodes.BadConnectionClosed);
        }
    }

    private static void EnableKeepAlive(Socket socket)
    {
        // Notice a dead peer within about 25 s even when no request is waiting.
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException)
        {
            // Older systems without these options keep the system defaults.
        }
    }

    /// <summary>Completes waiting requests when the connection ends.</summary>
    private sealed class ConnectionFailedException(StatusCode status) : Exception(status.Name)
    {
        public StatusCode Status { get; } = status;
    }
}
