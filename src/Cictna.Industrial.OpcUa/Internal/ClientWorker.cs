using System.Diagnostics;
using System.Runtime.InteropServices;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Native;
using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.OpcUa.Internal;

/// <summary>
/// Owns one native client on its own thread (see <see cref="NativeWorker"/>): sends requests,
/// matches responses to callers, tracks the connection state and forwards native log lines.
/// </summary>
/// <remarks>
/// Every field below is touched only by the worker thread, except where noted, so no locking is
/// needed. Callers interact through methods that post work and return tasks.
/// </remarks>
internal sealed partial class ClientWorker : NativeWorker
{
    // Native session/channel states (UA_SessionState, UA_SecureChannelState).
    private const int SessionActivated = 4;
    private const int ChannelClosed = 0;
    private const int SessionClosed = 0;
    private const uint IterateTimeoutMs = 5;

    private readonly OpcUaClientOptions _options;
    private readonly Action<ConnectionStateChange> _onStateChange;
    private readonly Dictionary<uint, PendingRequest> _pending = [];
    private ClientHandle? _handle;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private bool _disconnectRequested;
    private TaskCompletionSource? _connectWaiter;
    private TaskCompletionSource? _disconnectWaiter;

    private ClientWorker(OpcUaClientOptions options, ILogger logger, Action<ConnectionStateChange> onStateChange)
        : base("cictna-opcua-client", logger)
    {
        _options = options;
        _onStateChange = onStateChange;
    }

    /// <summary>Starts the thread and creates the native client on it.</summary>
    public static async Task<ClientWorker> CreateAsync(OpcUaClientOptions options, ILogger logger, Action<ConnectionStateChange> onStateChange)
    {
        var worker = new ClientWorker(options, logger, onStateChange);
        try
        {
            await worker.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            worker.Dispose();
            throw;
        }
        return worker;
    }

    /// <summary>Current state. Written by the worker thread, readable from any thread.</summary>
    public ConnectionState State => _state;

    // ---- lifecycle -------------------------------------------------------------------

    protected override unsafe void OnStart()
    {
        using var strings = new Utf8Strings();
        fixed (byte* cert = _options.Certificate?.Certificate)
        fixed (byte* key = _options.Certificate?.PrivateKey)
        {
            var native = new ClientOptionsNative
            {
                StructSize = (uint)sizeof(ClientOptionsNative),
                RequestTimeoutMs = ToMs(_options.RequestTimeout),
                SessionTimeoutMs = ToMs(_options.SessionTimeout),
                SecurityMode = (int)_options.SecurityMode,
                SecurityPolicyUri = strings.Add(_options.SecurityPolicyUri),
                ApplicationUri = strings.Add(_options.ApplicationUri),
                ApplicationName = strings.Add(_options.ApplicationName),
                Certificate = cert,
                CertificateLength = (nuint)(_options.Certificate?.Certificate.Length ?? 0),
                PrivateKey = key,
                PrivateKeyLength = (nuint)(_options.Certificate?.PrivateKey.Length ?? 0),
                Username = strings.Add((_options.Identity as UserIdentity.UserNameIdentity)?.Name),
                Password = strings.Add((_options.Identity as UserIdentity.UserNameIdentity)?.Password),
                AcceptAnyServerCertificate = _options.AcceptAnyServerCertificateForDevelopment ? 1u : 0u,
                LogLevel = NativeLog.ToNativeLevel(_options.NativeLogLevel),
            };
            var status = NativeMethods.ClientNew(&native, out var handle);
            if (status != 0)
            {
                handle.Dispose();
                throw new OpcUaException(new StatusCode(status), "Could not create the OPC UA client");
            }
            _handle = handle;
        }
    }

    protected override void Iterate()
    {
        NativeMethods.ClientIterate(_handle!, IterateTimeoutMs);
        DrainCompletions();
        UpdateState();
        DrainLogs();
    }

    protected override void OnStop()
    {
        foreach (var call in _pending.Values)
        {
            call.Fail(new OpcUaException(StatusCodes.BadShutdown, "The client was closed"));
        }
        _pending.Clear();
        _connectWaiter?.TrySetException(new OpcUaException(StatusCodes.BadShutdown, "The client was closed"));
        _disconnectWaiter?.TrySetResult();
        _handle?.Dispose(); // deletes the native client: closes the connection, frees everything
    }

    public Task StopWorkerAsync() => StopAsync();

    // ---- connect / disconnect ----------------------------------------------------------

    public async Task ConnectAsync(string endpointUrl, byte[] endpoint, UserTokenType tokenType, CancellationToken cancellationToken)
    {
        var waiter = await InvokeAsync(() =>
        {
            if (_state != ConnectionState.Disconnected)
            {
                throw new InvalidOperationException($"The client is {_state}; disconnect first.");
            }
            uint status;
            unsafe
            {
                fixed (byte* ep = endpoint)
                {
                    status = NativeMethods.ClientConnect(_handle!, endpointUrl, ep, (nuint)endpoint.Length, (int)tokenType);
                }
            }
            if (status != 0)
            {
                throw new OpcUaException(new StatusCode(status), $"Could not start connecting to {endpointUrl}");
            }
            _disconnectRequested = false;
            _connectWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetState(ConnectionState.Connecting, StatusCodes.Good);
            return _connectWaiter;
        }).ConfigureAwait(false);

        try
        {
            await waiter.Task.WaitAsync(_options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Give up: close whatever was opened so the client is reusable.
            await InvokeAsync(() =>
            {
                _disconnectRequested = true;
                NativeMethods.ClientDisconnect(_handle!);
                return true;
            }).ConfigureAwait(false);
            if (ex is TimeoutException)
            {
                throw new OpcUaException(StatusCodes.BadTimeout, $"No session with {endpointUrl} within {_options.ConnectTimeout.TotalSeconds:0.#} s");
            }
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var waiter = await InvokeAsync(() =>
        {
            if (_state == ConnectionState.Disconnected)
            {
                return Task.CompletedTask;
            }
            _disconnectRequested = true;
            _disconnectWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NativeMethods.ClientDisconnect(_handle!);
            SetState(ConnectionState.Disconnecting, StatusCodes.Good);
            return _disconnectWaiter.Task;
        }).ConfigureAwait(false);
        try
        {
            await waiter.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The server did not answer CloseSession; the channel is closed locally anyway.
        }
    }

    private void UpdateState()
    {
        NativeMethods.ClientState(_handle!, out var channel, out var session, out var connectStatus);
        var status = new StatusCode(connectStatus);
        ConnectionState next;
        if (session == SessionActivated && status.IsGood)
        {
            next = ConnectionState.Connected;
        }
        else if (status.IsBad || (channel == ChannelClosed && session == SessionClosed))
        {
            next = ConnectionState.Disconnected;
        }
        else
        {
            next = _disconnectRequested ? ConnectionState.Disconnecting : ConnectionState.Connecting;
        }

        if (next == _state)
        {
            return;
        }
        SetState(next, status);
        switch (next)
        {
            case ConnectionState.Connected:
                _connectWaiter?.TrySetResult();
                _connectWaiter = null;
                break;
            case ConnectionState.Disconnected:
                var reason = status.IsBad ? status : StatusCodes.BadConnectionClosed;
                _connectWaiter?.TrySetException(new OpcUaException(reason, "Connection failed"));
                _connectWaiter = null;
                _disconnectWaiter?.TrySetResult();
                _disconnectWaiter = null;
                break;
        }
    }

    private void SetState(ConnectionState state, StatusCode reason)
    {
        if (state == _state)
        {
            return;
        }
        _state = state;
        OpcUaTelemetry.StateChanges.Add(1, new KeyValuePair<string, object?>("state", state.ToString()));
        LogStateChange(Logger, state, reason.Name);
        _onStateChange(new ConnectionStateChange(state, reason));
    }

    // ---- requests ------------------------------------------------------------------------

    /// <summary>
    /// Sends one service request and returns the encoded response. Fails with OpcUaException for
    /// transport problems; the response's own service result is checked by the caller.
    /// </summary>
    public async Task<byte[]> SendAsync(string service, uint requestEncodingId, uint responseEncodingId, byte[] request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = new PendingRequest(service);
        Post(() => Send(call, requestEncodingId, responseEncodingId, request));

        // Native open62541 enforces the request timeout and answers with BadTimeout. This managed
        // deadline is a safety net a little later, for a native side that never answers.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout + TimeSpan.FromSeconds(2));
        await using var registration = deadline.Token.Register(() => Post(() => Abandon(call, cancellationToken))).ConfigureAwait(false);
        return await call.Completion.Task.ConfigureAwait(false);
    }

    private unsafe void Send(PendingRequest call, uint requestEncodingId, uint responseEncodingId, byte[] request)
    {
        if (call.Completion.Task.IsCompleted)
        {
            return; // abandoned before it was sent
        }
        if (_state != ConnectionState.Connected)
        {
            call.Fail(new OpcUaException(StatusCodes.BadNotConnected, $"{call.Service}: the client is {_state}"));
            return;
        }
        uint status, requestId;
        fixed (byte* p = request)
        {
            status = NativeMethods.ClientSend(_handle!, requestEncodingId, responseEncodingId, p, (nuint)request.Length, out requestId);
        }
        if (status != 0)
        {
            call.Fail(new OpcUaException(new StatusCode(status), $"{call.Service} could not be sent"));
            return;
        }
        call.RequestId = requestId;
        _pending[requestId] = call;
    }

    private void Abandon(PendingRequest call, CancellationToken callerToken)
    {
        if (call.Completion.Task.IsCompleted)
        {
            return;
        }
        if (call.RequestId != 0 && _pending.Remove(call.RequestId))
        {
            // Tell the server too (best effort); the late response is then simply dropped.
            NativeMethods.ClientCancel(_handle!, call.RequestId);
        }
        if (callerToken.IsCancellationRequested)
        {
            call.Cancel(callerToken);
        }
        else
        {
            call.Fail(new OpcUaException(StatusCodes.BadTimeout, $"{call.Service} got no response in time"));
        }
    }

    private unsafe void DrainCompletions()
    {
        while (NativeMethods.ClientPoll(_handle!, out var requestId, out var status, out var data, out var length) == 1)
        {
            if (!_pending.Remove(requestId, out var call))
            {
                continue; // abandoned (timed out or cancelled) earlier
            }
            if (status != 0)
            {
                call.Fail(new OpcUaException(new StatusCode(status), $"{call.Service} response could not be encoded"));
                continue;
            }
            call.Complete(new ReadOnlySpan<byte>(data, checked((int)length)).ToArray());
        }
    }

    // ---- discovery -----------------------------------------------------------------------

    public Task<byte[]> GetEndpointsAsync(string url) => InvokeAsync(() =>
    {
        var status = NativeMethods.ClientGetEndpoints(_handle!, url, out var response, out var length);
        var bytes = NativeMethods.TakeBuffer(response, length);
        if (status != 0)
        {
            throw new OpcUaException(new StatusCode(status), $"GetEndpoints on {url} failed");
        }
        return bytes;
    });

    // ---- logs --------------------------------------------------------------------------------

    private unsafe void DrainLogs()
    {
        while (true)
        {
            var more = NativeMethods.ClientPollLog(_handle!, out var level, out var category, out var message, out var dropped);
            if (dropped > 0)
            {
                NativeLog.Dropped(Logger, dropped, "client");
            }
            if (more != 1)
            {
                return;
            }
            NativeLog.Write(Logger, level, category, Marshal.PtrToStringUTF8((nint)message) ?? string.Empty, "client");
        }
    }

    private static uint ToMs(TimeSpan value) => (uint)Math.Clamp(value.TotalMilliseconds, 0, uint.MaxValue);

    [LoggerMessage(Level = LogLevel.Information, Message = "OPC UA client {State} ({Reason})")]
    private static partial void LogStateChange(ILogger logger, ConnectionState state, string reason);

    /// <summary>One request waiting for its response.</summary>
    private sealed class PendingRequest(string service)
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public string Service { get; } = service;
        public uint RequestId { get; set; }
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(byte[] response)
        {
            Record("Good");
            Completion.TrySetResult(response);
        }

        public void Fail(OpcUaException error)
        {
            Record(error.StatusCode.Name);
            Completion.TrySetException(error);
        }

        public void Cancel(CancellationToken token)
        {
            Record("Cancelled");
            Completion.TrySetCanceled(token);
        }

        private void Record(string status)
        {
            var service = new KeyValuePair<string, object?>("service", Service);
            OpcUaTelemetry.Requests.Add(1, service, new KeyValuePair<string, object?>("status", status));
            OpcUaTelemetry.RequestDuration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, service);
        }
    }
}
