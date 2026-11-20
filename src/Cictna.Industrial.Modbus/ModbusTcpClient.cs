using System.Diagnostics;
using System.Threading.Channels;
using Cictna.Industrial.Core;
using Cictna.Industrial.Modbus.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cictna.Industrial.Modbus;

/// <summary>
/// A Modbus TCP client. One client holds one connection to one server (a device or a gateway)
/// and can address any unit ID on it.
/// </summary>
/// <remarks>
/// <para>
/// Requests never throw for expected failures: a timeout, a lost connection or an exception
/// response comes back as a <see cref="ModbusResult"/> whose status says what happened. Only
/// programming errors (a quantity out of range, an address past 65535) and cancellation through
/// the <see cref="CancellationToken"/> throw.
/// </para>
/// <para>
/// Reads and writes larger than one request allows are split into several requests. A split
/// write is not atomic: if a later part fails, the earlier parts are already written.
/// </para>
/// <para>
/// After a connection is lost (or <see cref="ModbusClientOptions.TimeoutsBeforeReconnect"/>
/// consecutive timeouts), the client reconnects in the background with backoff. Requests made
/// meanwhile return BadNotConnected at once.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var client = new ModbusTcpClient(new ModbusClientOptions { RequestTimeout = TimeSpan.FromSeconds(2) }, logger);
/// await client.ConnectAsync("192.168.1.20", 502, cancellationToken);
///
/// ModbusResult&lt;ushort[]&gt; registers = await client.ReadHoldingRegistersAsync(unitId: 1, address: 0, count: 10, cancellationToken);
/// if (registers.IsGood)
/// {
///     Console.WriteLine(string.Join(", ", registers.Value!));
/// }
/// else
/// {
///     Console.WriteLine($"Read failed: {registers}"); // for example "BadOutOfRange (Modbus exception 02 IllegalDataAddress)"
/// }
/// </code>
/// </example>
public sealed partial class ModbusTcpClient : IAsyncDisposable
{
    private readonly ModbusClientOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _slots;
    private readonly Channel<ModbusConnectionStateChange> _events = Channel.CreateUnbounded<ModbusConnectionStateChange>(new() { SingleReader = true });
    private readonly Task _eventPump;
    private readonly Random _random = new();
    private ModbusConnection? _connection;
    private ModbusConnectionState _state;
    private CancellationTokenSource? _reconnect;
    private Task _reconnectLoop = Task.CompletedTask;
    private int _consecutiveTimeouts;
    private int _disposed;

    /// <summary>Creates a client; call <see cref="ConnectAsync"/> next.</summary>
    /// <param name="options">Timeouts, limits and reconnection; null for the defaults.</param>
    /// <param name="logger">Receives connection logs and request details at Debug level; null for none.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public ModbusTcpClient(ModbusClientOptions? options = null, ILogger<ModbusTcpClient>? logger = null)
    {
        _options = options ?? new ModbusClientOptions();
        _options.Validate();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _slots = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);
        _eventPump = Task.Run(PumpEventsAsync);
    }

    /// <summary>The current connection state.</summary>
    public ModbusConnectionState State => _state;

    /// <summary>
    /// Raised on every state change, in order, on a thread-pool thread. Handlers should be quick;
    /// an exception from a handler is logged and ignored.
    /// </summary>
    public event EventHandler<ModbusConnectionStateChange>? StateChanged;

    /// <summary>Host of the current or last connection.</summary>
    public string? Host { get; private set; }

    /// <summary>Port of the current or last connection.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Opens the TCP connection. Once connected, a lost connection is restored automatically (see
    /// <see cref="ModbusClientOptions.Reconnect"/>); a failed first connection is not retried.
    /// </summary>
    /// <param name="host">Host name or IP address of the device or gateway.</param>
    /// <param name="port">TCP port, usually 502.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <exception cref="ModbusException">
    /// The server is unreachable (BadNotConnected), did not answer in time (BadTimeout), or the host
    /// name does not resolve (BadInvalidArgument).
    /// </exception>
    /// <exception cref="InvalidOperationException">The client is already connected or connecting.</exception>
    public async Task ConnectAsync(string host, int port = 502, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_gate)
        {
            if (_state != ModbusConnectionState.Disconnected)
            {
                throw new InvalidOperationException($"The client is {_state}; disconnect first.");
            }
            Host = host;
            Port = port;
            SetState(ModbusConnectionState.Connecting, StatusCodes.Good);
        }
        try
        {
            var connection = await ModbusConnection.OpenAsync(host, port, _options.ConnectTimeout, OnConnectionLost, OnDiscarded, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_state != ModbusConnectionState.Connecting)
                {
                    _ = connection.DisposeAsync().AsTask(); // disconnected while connecting
                    throw new ModbusException(StatusCodes.BadConnectionClosed, "Disconnect was requested while connecting");
                }
                _connection = connection;
                _consecutiveTimeouts = 0;
                SetState(ModbusConnectionState.Connected, StatusCodes.Good);
            }
            LogConnected(_logger, host, port);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_state == ModbusConnectionState.Connecting)
                {
                    SetState(ModbusConnectionState.Disconnected, ex is ModbusException m ? m.StatusCode : StatusCodes.BadRequestCancelledByClient);
                }
            }
            if (ex is ModbusException failure)
            {
                LogConnectFailed(_logger, host, port, failure.Message);
            }
            throw;
        }
    }

    /// <summary>Closes the connection and stops reconnecting. Waiting requests get BadConnectionClosed.</summary>
    public async Task DisconnectAsync()
    {
        ModbusConnection? connection;
        CancellationTokenSource? reconnect;
        Task reconnectLoop;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
            reconnect = _reconnect;
            _reconnect = null;
            reconnectLoop = _reconnectLoop;
            if (_state != ModbusConnectionState.Disconnected)
            {
                SetState(ModbusConnectionState.Disconnected, StatusCodes.Good);
            }
        }
        if (reconnect is not null)
        {
            await reconnect.CancelAsync().ConfigureAwait(false);
            await reconnectLoop.ConfigureAwait(false);
            reconnect.Dispose();
        }
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Disconnects and releases the client.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await DisconnectAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        await _eventPump.ConfigureAwait(false);
        _slots.Dispose();
    }

    /// <summary>Function code 1: reads coils. Reads above <see cref="ModbusClientOptions.MaxBitsPerRead"/> are split.</summary>
    /// <param name="unitId">The unit (1 to 247 on most devices; 0 or 255 for some gateways).</param>
    /// <param name="address">First coil, 0-based.</param>
    /// <param name="count">Number of coils, at least 1.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The coils, or why they could not be read.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is below 1 or the range runs past address 65535.</exception>
    public Task<ModbusResult<bool[]>> ReadCoilsAsync(byte unitId, ushort address, int count, CancellationToken cancellationToken = default) =>
        ReadBitsAsync(ModbusFunctionCode.ReadCoils, unitId, address, count, cancellationToken);

    /// <summary>Function code 2: reads discrete inputs. Reads above <see cref="ModbusClientOptions.MaxBitsPerRead"/> are split.</summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="address">First input, 0-based.</param>
    /// <param name="count">Number of inputs, at least 1.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The inputs, or why they could not be read.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is below 1 or the range runs past address 65535.</exception>
    public Task<ModbusResult<bool[]>> ReadDiscreteInputsAsync(byte unitId, ushort address, int count, CancellationToken cancellationToken = default) =>
        ReadBitsAsync(ModbusFunctionCode.ReadDiscreteInputs, unitId, address, count, cancellationToken);

    /// <summary>Function code 3: reads holding registers. Reads above <see cref="ModbusClientOptions.MaxRegistersPerRead"/> are split.</summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="address">First register, 0-based.</param>
    /// <param name="count">Number of registers, at least 1.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The registers, or why they could not be read.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is below 1 or the range runs past address 65535.</exception>
    public Task<ModbusResult<ushort[]>> ReadHoldingRegistersAsync(byte unitId, ushort address, int count, CancellationToken cancellationToken = default) =>
        ReadRegistersAsync(ModbusFunctionCode.ReadHoldingRegisters, unitId, address, count, cancellationToken);

    /// <summary>Function code 4: reads input registers. Reads above <see cref="ModbusClientOptions.MaxRegistersPerRead"/> are split.</summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="address">First register, 0-based.</param>
    /// <param name="count">Number of registers, at least 1.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The registers, or why they could not be read.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is below 1 or the range runs past address 65535.</exception>
    public Task<ModbusResult<ushort[]>> ReadInputRegistersAsync(byte unitId, ushort address, int count, CancellationToken cancellationToken = default) =>
        ReadRegistersAsync(ModbusFunctionCode.ReadInputRegisters, unitId, address, count, cancellationToken);

    /// <summary>Function code 5 (or 15 with <see cref="ModbusClientOptions.AlwaysWriteMultiple"/>): writes one coil.</summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="address">The coil, 0-based.</param>
    /// <param name="value">On (true) or off.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Good, or why the write failed.</returns>
    public async Task<ModbusResult> WriteSingleCoilAsync(byte unitId, ushort address, bool value, CancellationToken cancellationToken = default)
    {
        var pdu = _options.AlwaysWriteMultiple ? ModbusPdu.WriteMultipleCoils(address, [value]) : ModbusPdu.WriteSingleCoil(address, value);
        return (await ExchangeAsync(unitId, pdu, cancellationToken).ConfigureAwait(false)).Result;
    }

    /// <summary>Function code 6 (or 16 with <see cref="ModbusClientOptions.AlwaysWriteMultiple"/>): writes one holding register.</summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="address">The register, 0-based.</param>
    /// <param name="value">The new value.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Good, or why the write failed.</returns>
    public async Task<ModbusResult> WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        var pdu = _options.AlwaysWriteMultiple ? ModbusPdu.WriteMultipleRegisters(address, [value]) : ModbusPdu.WriteSingleRegister(address, value);
        return (await ExchangeAsync(unitId, pdu, cancellationToken).ConfigureAwait(false)).Result;
    }

    /// <summary>
    /// Function code 15: writes coils. Writes above <see cref="ModbusClientOptions.MaxBitsPerWrite"/>
    /// are split into several requests, which together are not atomic.
    /// </summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="address">First coil, 0-based.</param>
    /// <param name="values">At least one value.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Good, or why the write failed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No values, or the range runs past address 65535.</exception>
    public async Task<ModbusResult> WriteMultipleCoilsAsync(byte unitId, ushort address, ReadOnlyMemory<bool> values, CancellationToken cancellationToken = default)
    {
        CheckRange(address, values.Length);
        for (var offset = 0; offset < values.Length; offset += _options.MaxBitsPerWrite)
        {
            var part = values.Slice(offset, Math.Min(_options.MaxBitsPerWrite, values.Length - offset));
            var result = (await ExchangeAsync(unitId, ModbusPdu.WriteMultipleCoils((ushort)(address + offset), part.Span), cancellationToken).ConfigureAwait(false)).Result;
            if (!result.IsGood)
            {
                return result;
            }
        }
        return ModbusResult.Good;
    }

    /// <summary>
    /// Function code 16: writes holding registers. Writes above
    /// <see cref="ModbusClientOptions.MaxRegistersPerWrite"/> are split into several requests,
    /// which together are not atomic.
    /// </summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="address">First register, 0-based.</param>
    /// <param name="values">At least one value.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Good, or why the write failed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No values, or the range runs past address 65535.</exception>
    public async Task<ModbusResult> WriteMultipleRegistersAsync(byte unitId, ushort address, ReadOnlyMemory<ushort> values, CancellationToken cancellationToken = default)
    {
        CheckRange(address, values.Length);
        for (var offset = 0; offset < values.Length; offset += _options.MaxRegistersPerWrite)
        {
            var part = values.Slice(offset, Math.Min(_options.MaxRegistersPerWrite, values.Length - offset));
            var result = (await ExchangeAsync(unitId, ModbusPdu.WriteMultipleRegisters((ushort)(address + offset), part.Span), cancellationToken).ConfigureAwait(false)).Result;
            if (!result.IsGood)
            {
                return result;
            }
        }
        return ModbusResult.Good;
    }

    /// <summary>
    /// Function code 23: writes holding registers, then reads holding registers, in one request.
    /// Not split: at most 121 registers written and 125 read.
    /// </summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="readAddress">First register to read.</param>
    /// <param name="readCount">Registers to read, 1 to 125.</param>
    /// <param name="writeAddress">First register to write.</param>
    /// <param name="values">Registers to write, 1 to 121.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The registers read, or why the request failed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is out of range or a range runs past address 65535.</exception>
    public async Task<ModbusResult<ushort[]>> ReadWriteMultipleRegistersAsync(
        byte unitId, ushort readAddress, int readCount, ushort writeAddress, ReadOnlyMemory<ushort> values, CancellationToken cancellationToken = default)
    {
        CheckRange(readAddress, readCount);
        CheckRange(writeAddress, values.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readCount, ModbusPdu.MaxReadWriteRead);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(values.Length, ModbusPdu.MaxReadWriteWrite, nameof(values));
        var (result, response) = await ExchangeAsync(unitId, ModbusPdu.ReadWriteMultipleRegisters(readAddress, readCount, writeAddress, values.Span), cancellationToken).ConfigureAwait(false);
        return result.IsGood ? new(ModbusPdu.ReadRegisters(ModbusPdu.Data(response), readCount), StatusCodes.Good) : result.As<ushort[]>();
    }

    /// <summary>
    /// Sends any request PDU (function code first) and returns the response PDU, for function codes
    /// this client has no method for. An exception response becomes a failed result with its code.
    /// </summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="pdu">The request PDU, 1 to 253 bytes.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The response PDU, or why the request failed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The PDU is empty or longer than 253 bytes.</exception>
    public async Task<ModbusResult<byte[]>> SendRawAsync(byte unitId, ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pdu.Length, 1, nameof(pdu));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pdu.Length, ModbusPdu.MaxLength, nameof(pdu));
        var (result, response) = await ExchangeAsync(unitId, pdu.ToArray(), cancellationToken).ConfigureAwait(false);
        return result.IsGood ? new(response, StatusCodes.Good) : result.As<byte[]>();
    }

    /// <summary>
    /// Reads a typed value. Modbus carries no timestamps, so the value's ServerTimestamp is the
    /// time the answer arrived and SourceTimestamp is null.
    /// </summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="point">Where the value is and how it is laid out.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The value with Good status, or a value with no data and the failure status.</returns>
    /// <exception cref="ArgumentException">The point is not valid.</exception>
    public async Task<DataValue> ReadValueAsync(byte unitId, ModbusPoint point, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        point.Validate();
        Variant value;
        switch (point.Table)
        {
            case ModbusTable.Coils or ModbusTable.DiscreteInputs:
                var bits = point.Table == ModbusTable.Coils
                    ? await ReadCoilsAsync(unitId, point.Address, point.Quantity, cancellationToken).ConfigureAwait(false)
                    : await ReadDiscreteInputsAsync(unitId, point.Address, point.Quantity, cancellationToken).ConfigureAwait(false);
                if (!bits.IsGood)
                {
                    return DataValue.FromStatus(bits.Status);
                }
                value = ModbusDataConverter.FromBits(point, bits.Value!);
                break;
            default:
                var registers = point.Table == ModbusTable.HoldingRegisters
                    ? await ReadHoldingRegistersAsync(unitId, point.Address, point.Quantity, cancellationToken).ConfigureAwait(false)
                    : await ReadInputRegistersAsync(unitId, point.Address, point.Quantity, cancellationToken).ConfigureAwait(false);
                if (!registers.IsGood)
                {
                    return DataValue.FromStatus(registers.Status);
                }
                value = ModbusDataConverter.FromRegisters(point, registers.Value!);
                break;
        }
        return new DataValue(value, StatusCodes.Good, ServerTimestamp: DateTime.UtcNow);
    }

    /// <summary>
    /// Writes a typed value to coils or holding registers. Discrete inputs, input registers and
    /// register bits are read-only (BadNotWritable).
    /// </summary>
    /// <param name="unitId">The unit.</param>
    /// <param name="point">Where the value goes and how it is laid out.</param>
    /// <param name="value">The value; numbers convert when they fit exactly (see <see cref="ModbusDataConverter.ToRegisters"/>).</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Good, or BadNotWritable, BadTypeMismatch, BadOutOfRange, or why the request failed.</returns>
    /// <exception cref="ArgumentException">The point is not valid.</exception>
    public async Task<ModbusResult> WriteValueAsync(byte unitId, ModbusPoint point, Variant value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        point.Validate();
        switch (point.Table)
        {
            case ModbusTable.Coils:
                var bits = ModbusDataConverter.ToBits(point, value);
                if (!bits.IsGood)
                {
                    return bits.WithoutValue;
                }
                return bits.Value!.Length == 1
                    ? await WriteSingleCoilAsync(unitId, point.Address, bits.Value[0], cancellationToken).ConfigureAwait(false)
                    : await WriteMultipleCoilsAsync(unitId, point.Address, bits.Value, cancellationToken).ConfigureAwait(false);
            case ModbusTable.HoldingRegisters:
                var registers = ModbusDataConverter.ToRegisters(point, value);
                if (!registers.IsGood)
                {
                    return registers.WithoutValue;
                }
                return registers.Value!.Length == 1
                    ? await WriteSingleRegisterAsync(unitId, point.Address, registers.Value[0], cancellationToken).ConfigureAwait(false)
                    : await WriteMultipleRegistersAsync(unitId, point.Address, registers.Value, cancellationToken).ConfigureAwait(false);
            default:
                return new ModbusResult(StatusCodes.BadNotWritable);
        }
    }

    private async Task<ModbusResult<bool[]>> ReadBitsAsync(ModbusFunctionCode function, byte unitId, ushort address, int count, CancellationToken cancellationToken)
    {
        CheckRange(address, count);
        var bits = new bool[count];
        for (var offset = 0; offset < count; offset += _options.MaxBitsPerRead)
        {
            var part = Math.Min(_options.MaxBitsPerRead, count - offset);
            var (result, response) = await ExchangeAsync(unitId, ModbusPdu.Read(function, (ushort)(address + offset), part), cancellationToken).ConfigureAwait(false);
            if (!result.IsGood)
            {
                return result.As<bool[]>();
            }
            ModbusPdu.UnpackBits(ModbusPdu.Data(response), part).CopyTo(bits, offset);
        }
        return new(bits, StatusCodes.Good);
    }

    private async Task<ModbusResult<ushort[]>> ReadRegistersAsync(ModbusFunctionCode function, byte unitId, ushort address, int count, CancellationToken cancellationToken)
    {
        CheckRange(address, count);
        var registers = new ushort[count];
        for (var offset = 0; offset < count; offset += _options.MaxRegistersPerRead)
        {
            var part = Math.Min(_options.MaxRegistersPerRead, count - offset);
            var (result, response) = await ExchangeAsync(unitId, ModbusPdu.Read(function, (ushort)(address + offset), part), cancellationToken).ConfigureAwait(false);
            if (!result.IsGood)
            {
                return result.As<ushort[]>();
            }
            ModbusPdu.ReadRegisters(ModbusPdu.Data(response), part).CopyTo(registers, offset);
        }
        return new(registers, StatusCodes.Good);
    }

    private static void CheckRange(ushort address, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1, nameof(count));
        if (address + count > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"The range runs past the last address: {address} + {count} > 65536.");
        }
    }

    /// <summary>One request and its checked response; failures are results, cancellation throws.</summary>
    private async Task<(ModbusResult Result, byte[] Response)> ExchangeAsync(byte unitId, byte[] pdu, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var function = pdu[0];
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        ModbusResult result;
        byte[]? response = null;
        try
        {
            var connection = _connection;
            if (connection is null || _state != ModbusConnectionState.Connected)
            {
                result = new(StatusCodes.BadNotConnected);
            }
            else
            {
                (var status, response) = await connection.SendAsync(unitId, pdu, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
                result = status.IsGood ? ModbusPdu.Check(pdu, response) : new(status);
                TrackTimeouts(connection, status);
            }
        }
        finally
        {
            _slots.Release();
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var tags = new TagList { { "function", function }, { "status", result.Status.Name } };
        ModbusTelemetry.ClientRequests.Add(1, tags);
        ModbusTelemetry.ClientRequestDuration.Record(elapsed, new TagList { { "function", function } });
        LogRequest(_logger, unitId, function, result.Status, result.Exception, elapsed);
        if (result.Status == StatusCodes.BadDecodingError && _logger.IsEnabled(LogLevel.Warning))
        {
            LogBadResponse(_logger, unitId, function, response is null ? string.Empty : Convert.ToHexString(response));
        }
        return (result, response ?? []);
    }

    private void TrackTimeouts(ModbusConnection connection, StatusCode status)
    {
        if (status != StatusCodes.BadTimeout)
        {
            Interlocked.Exchange(ref _consecutiveTimeouts, 0);
            return;
        }
        var timeouts = Interlocked.Increment(ref _consecutiveTimeouts);
        if (_options.TimeoutsBeforeReconnect > 0 && timeouts >= _options.TimeoutsBeforeReconnect)
        {
            LogTimeoutsReconnect(_logger, timeouts, Host ?? string.Empty, Port);
            connection.Fail(StatusCodes.BadTimeout);
        }
    }

    private void OnDiscarded(ushort transactionId)
    {
        ModbusTelemetry.ClientDiscardedResponses.Add(1);
        LogDiscarded(_logger, transactionId);
    }

    private void OnConnectionLost(ModbusConnection connection, StatusCode reason)
    {
        lock (_gate)
        {
            if (_connection != connection || _disposed != 0)
            {
                return; // an old connection, or we are shutting down
            }
            _connection = null;
            _ = connection.DisposeAsync().AsTask();
            if (!_options.Reconnect.Enabled)
            {
                SetState(ModbusConnectionState.Disconnected, reason);
                LogLost(_logger, Host ?? string.Empty, Port, reason.Name, false);
                return;
            }
            SetState(ModbusConnectionState.Reconnecting, reason);
            LogLost(_logger, Host ?? string.Empty, Port, reason.Name, true);
            _reconnect = new CancellationTokenSource();
            var token = _reconnect.Token;
            _reconnectLoop = Task.Run(() => ReconnectLoopAsync(token), CancellationToken.None);
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; !cancellationToken.IsCancellationRequested; attempt++)
        {
            var delay = _options.Reconnect.DelayFor(attempt, _random);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                var connection = await ModbusConnection.OpenAsync(Host!, Port, _options.ConnectTimeout, OnConnectionLost, OnDiscarded, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (cancellationToken.IsCancellationRequested || _state != ModbusConnectionState.Reconnecting)
                    {
                        _ = connection.DisposeAsync().AsTask();
                        return;
                    }
                    _connection = connection;
                    _consecutiveTimeouts = 0;
                    _reconnect?.Dispose();
                    _reconnect = null;
                    SetState(ModbusConnectionState.Connected, StatusCodes.Good);
                }
                LogReconnected(_logger, Host!, Port, attempt);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ModbusException ex)
            {
                LogReconnectFailed(_logger, attempt, ex.Message);
            }
        }
    }

    private void SetState(ModbusConnectionState state, StatusCode reason)
    {
        _state = state;
        ModbusTelemetry.ClientStateChanges.Add(1, new KeyValuePair<string, object?>("state", state.ToString()));
        _events.Writer.TryWrite(new(state, reason));
    }

    private async Task PumpEventsAsync()
    {
        await foreach (var change in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                StateChanged?.Invoke(this, change);
            }
#pragma warning disable CA1031 // A failing handler must not stop later state changes from being delivered.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogHandlerFailed(_logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Modbus connected to {Host}:{Port}")]
    private static partial void LogConnected(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Modbus connection to {Host}:{Port} failed: {Reason}")]
    private static partial void LogConnectFailed(ILogger logger, string host, int port, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Modbus connection to {Host}:{Port} lost: {Reason}; reconnecting: {Reconnecting}")]
    private static partial void LogLost(ILogger logger, string host, int port, string reason, bool reconnecting);

    [LoggerMessage(Level = LogLevel.Information, Message = "Modbus reconnected to {Host}:{Port} after {Attempts} attempts")]
    private static partial void LogReconnected(ILogger logger, string host, int port, int attempts);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Modbus reconnect attempt {Attempt} failed: {Reason}")]
    private static partial void LogReconnectFailed(ILogger logger, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} Modbus requests in a row timed out on {Host}:{Port}; rebuilding the connection")]
    private static partial void LogTimeoutsReconnect(ILogger logger, int count, string host, int port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Modbus unit {UnitId} function {Function}: {Status}, exception {Exception}, in {Elapsed:0.0} ms")]
    private static partial void LogRequest(ILogger logger, byte unitId, byte function, StatusCode status, ModbusExceptionCode exception, double elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Modbus unit {UnitId} answered function {Function} with a response that does not fit the request: {Response}")]
    private static partial void LogBadResponse(ILogger logger, byte unitId, byte function, string response);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Modbus response with transaction {TransactionId} discarded: nobody waits for it (late after a timeout)")]
    private static partial void LogDiscarded(ILogger logger, ushort transactionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A StateChanged handler failed")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
