using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus;

/// <summary>Connection state of a <see cref="ModbusTcpClient"/>.</summary>
public enum ModbusConnectionState
{
    /// <summary>Not connected (initially, after a disconnect, or after a loss without reconnection).</summary>
    Disconnected,
    /// <summary>Opening the TCP connection.</summary>
    Connecting,
    /// <summary>Connected: requests are sent.</summary>
    Connected,
    /// <summary>
    /// The connection was lost and the client is getting it back (see
    /// <see cref="ModbusClientOptions.Reconnect"/>). Requests fail with BadNotConnected meanwhile.
    /// </summary>
    Reconnecting,
}

/// <summary>Data of <see cref="ModbusTcpClient.StateChanged"/>.</summary>
/// <param name="State">The new state.</param>
/// <param name="Reason">Good for normal transitions, otherwise why the connection was lost or failed.</param>
public sealed record ModbusConnectionStateChange(ModbusConnectionState State, StatusCode Reason);

/// <summary>
/// Automatic reconnection after a lost connection (not after
/// <see cref="ModbusTcpClient.DisconnectAsync"/>). Attempts are spaced with exponential backoff
/// and random jitter, so many clients that lost the same device do not all return at once.
/// </summary>
public sealed record ModbusReconnectOptions
{
    /// <summary>Reconnect automatically. When false the client stays Disconnected after a loss.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Wait before the first attempt. Default 1 s.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait between attempts. Default 30 s.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Factor by which the wait grows after each failed attempt. Default 2.</summary>
    public double Multiplier { get; init; } = 2;

    /// <summary>Random spread of each wait, as a fraction (0.2 means plus or minus 20 %). Default 0.2.</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>The wait before attempt <paramref name="attempt"/> (1-based), with jitter from <paramref name="random"/>.</summary>
    internal TimeSpan DelayFor(int attempt, Random random)
    {
        var baseMs = InitialDelay.TotalMilliseconds * Math.Pow(Math.Max(1, Multiplier), Math.Max(0, attempt - 1));
        baseMs = Math.Min(baseMs, MaxDelay.TotalMilliseconds);
        var spread = Math.Clamp(Jitter, 0, 1) * (random.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(Math.Max(0, baseMs * (1 + spread)));
    }
}

/// <summary>Settings of a <see cref="ModbusTcpClient"/>.</summary>
public sealed record ModbusClientOptions
{
    /// <summary>Time allowed to open the TCP connection. Default 5 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Time allowed for each answer, from sending the request. Default 3 s.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Requests sent before earlier ones are answered (pipelining), matched by transaction ID.
    /// Default 1, which every device handles; raise it only for devices and gateways that
    /// process several requests at once.
    /// </summary>
    public int MaxConcurrentRequests { get; init; } = 1;

    /// <summary>
    /// Consecutive timeouts after which the connection is considered dead and rebuilt (a cut cable
    /// often leaves the TCP connection looking open). Default 3; 0 never rebuilds for timeouts.
    /// </summary>
    public int TimeoutsBeforeReconnect { get; init; } = 3;

    /// <summary>
    /// Always use function codes 15 and 16 for writes, even for a single coil or register, for
    /// devices that do not support 5 and 6. Default false.
    /// </summary>
    public bool AlwaysWriteMultiple { get; init; }

    /// <summary>Coils or inputs per read request; larger reads are split. Default 2000, the protocol maximum.</summary>
    public int MaxBitsPerRead { get; init; } = 2000;

    /// <summary>Registers per read request; larger reads are split. Default 125, the protocol maximum.</summary>
    public int MaxRegistersPerRead { get; init; } = 125;

    /// <summary>Coils per write request; larger writes are split. Default 1968, the protocol maximum.</summary>
    public int MaxBitsPerWrite { get; init; } = 1968;

    /// <summary>Registers per write request; larger writes are split. Default 123, the protocol maximum.</summary>
    public int MaxRegistersPerWrite { get; init; } = 123;

    /// <summary>Automatic reconnection after a loss.</summary>
    public ModbusReconnectOptions Reconnect { get; init; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentRequests, 1, nameof(MaxConcurrentRequests));
        ArgumentOutOfRangeException.ThrowIfNegative(TimeoutsBeforeReconnect, nameof(TimeoutsBeforeReconnect));
        CheckLimit(MaxBitsPerRead, 2000, nameof(MaxBitsPerRead));
        CheckLimit(MaxRegistersPerRead, 125, nameof(MaxRegistersPerRead));
        CheckLimit(MaxBitsPerWrite, 1968, nameof(MaxBitsPerWrite));
        CheckLimit(MaxRegistersPerWrite, 123, nameof(MaxRegistersPerWrite));
        if (RequestTimeout <= TimeSpan.Zero || ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Timeouts must be positive.");
        }
    }

    private static void CheckLimit(int value, int max, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, max, name);
    }
}
