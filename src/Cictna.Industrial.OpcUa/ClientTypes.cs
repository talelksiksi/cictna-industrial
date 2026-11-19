using System.Diagnostics.Metrics;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Native;
using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.OpcUa;

/// <summary>An OPC UA operation failed as a whole (not just one item of a batch).</summary>
/// <remarks>
/// Batch operations report per-item problems in their results (a DataValue's status, a write's
/// StatusCode) and only throw this exception when the whole call failed: not connected, timed
/// out, rejected by the server, or the connection was lost.
/// </remarks>
public sealed class OpcUaException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="statusCode">Why the operation failed.</param>
    /// <param name="message">Context; the status name is appended.</param>
    public OpcUaException(StatusCode statusCode, string? message = null)
        : base(message is null ? statusCode.ToString() : $"{message}: {statusCode}")
    {
        StatusCode = statusCode;
    }

    /// <summary>Creates the exception with an inner exception.</summary>
    /// <param name="statusCode">Why the operation failed.</param>
    /// <param name="message">Context.</param>
    /// <param name="innerException">The underlying exception.</param>
    public OpcUaException(StatusCode statusCode, string message, Exception innerException)
        : base($"{message}: {statusCode}", innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>Creates the exception with BadUnexpectedError.</summary>
    public OpcUaException() : this(StatusCodes.BadUnexpectedError) { }

    /// <summary>Creates the exception with BadUnexpectedError.</summary>
    /// <param name="message">Context.</param>
    public OpcUaException(string message) : this(StatusCodes.BadUnexpectedError, message) { }

    /// <summary>Creates the exception with BadUnexpectedError.</summary>
    /// <param name="message">Context.</param>
    /// <param name="innerException">The underlying exception.</param>
    public OpcUaException(string message, Exception innerException) : this(StatusCodes.BadUnexpectedError, message, innerException) { }

    /// <summary>The OPC UA status code describing the failure.</summary>
    public StatusCode StatusCode { get; }
}

/// <summary>Connection state of an <see cref="OpcUaClient"/>.</summary>
public enum ConnectionState
{
    /// <summary>Not connected (initially, after a disconnect, or after the connection was lost).</summary>
    Disconnected,
    /// <summary>Opening the secure channel and activating the session.</summary>
    Connecting,
    /// <summary>Session active: requests can be sent.</summary>
    Connected,
    /// <summary>Closing the session and channel.</summary>
    Disconnecting,
    /// <summary>
    /// The connection was lost and the client is trying to get it back (see
    /// <see cref="OpcUaClientOptions.Reconnect"/>). Calls fail with BadNotConnected meanwhile;
    /// subscriptions resume by themselves once connected again.
    /// </summary>
    Reconnecting,
}

/// <summary>Data of <see cref="OpcUaClient.StateChanged"/>.</summary>
/// <param name="State">The new state.</param>
/// <param name="Reason">Why: Good for normal transitions, the failure for lost or refused connections.</param>
public sealed record ConnectionStateChange(ConnectionState State, StatusCode Reason);

/// <summary>A user identity for the session.</summary>
public abstract record UserIdentity
{
    private protected UserIdentity() { }

    /// <summary>No user (the server must allow anonymous access).</summary>
    public static UserIdentity Anonymous { get; } = new AnonymousIdentity();

    /// <summary>User name and password. Sent encrypted when the endpoint's token policy has security.</summary>
    /// <param name="userName">The user name.</param>
    /// <param name="password">The password.</param>
    public static UserIdentity UserName(string userName, string password) => new UserNameIdentity(userName, password);

    /// <summary>Anonymous identity.</summary>
    public sealed record AnonymousIdentity : UserIdentity;

    /// <summary>User name and password identity.</summary>
    /// <param name="Name">The user name.</param>
    /// <param name="Password">The password.</param>
    public sealed record UserNameIdentity(string Name, string Password) : UserIdentity
    {
        /// <summary>Hides the password from logs.</summary>
        public override string ToString() => $"UserName {{ Name = {Name} }}";
    }
}

/// <summary>
/// An application instance certificate and its private key, both DER-encoded. Every OPC UA
/// application that uses Sign or SignAndEncrypt needs one; its subject alternative name carries the
/// application URI, which must equal the URI the application announces.
/// </summary>
/// <param name="Certificate">The X.509 certificate (DER).</param>
/// <param name="PrivateKey">The private key (DER). Keep it secret: anyone with it can impersonate the application.</param>
public sealed record ApplicationCertificate(byte[] Certificate, byte[] PrivateKey)
{
    /// <summary>
    /// Creates a self-signed certificate (RSA, SHA-256). Fine for tests and for plants that trust
    /// certificates one by one; Phase 3 adds certificate stores and renewal.
    /// </summary>
    /// <param name="applicationUri">The application URI (also the URI the application announces).</param>
    /// <param name="commonName">Subject common name, usually the application name.</param>
    /// <param name="organization">Subject organization, optional.</param>
    /// <param name="dnsName">Host name, optional but recommended for servers.</param>
    /// <param name="keySizeBits">2048 or 4096.</param>
    /// <param name="validity">Lifetime; one year by default.</param>
    /// <exception cref="OpcUaException">The native library could not create the certificate.</exception>
    public static ApplicationCertificate CreateSelfSigned(string applicationUri, string commonName,
        string? organization = null, string? dnsName = null, int keySizeBits = 2048, TimeSpan? validity = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationUri);
        ArgumentException.ThrowIfNullOrEmpty(commonName);
        if (keySizeBits is not (2048 or 4096))
        {
            throw new ArgumentOutOfRangeException(nameof(keySizeBits), keySizeBits, "Use 2048 or 4096.");
        }
        var days = (uint)Math.Clamp((validity ?? TimeSpan.FromDays(365)).TotalDays, 1, 65535);
        var status = NativeMethods.CreateCertificate(commonName, organization, applicationUri, dnsName,
            (uint)keySizeBits, days, out var cert, out var certLength, out var key, out var keyLength);
        var certificate = NativeMethods.TakeBuffer(cert, certLength);
        var privateKey = NativeMethods.TakeBuffer(key, keyLength);
        if (status != 0)
        {
            throw new OpcUaException(new StatusCode(status), "Could not create the certificate");
        }
        return new ApplicationCertificate(certificate, privateKey);
    }

    /// <summary>Size only; never prints key material.</summary>
    public override string ToString() => $"ApplicationCertificate({Certificate.Length} bytes)";
}

/// <summary>Settings of an <see cref="OpcUaClient"/>. Immutable once the client is created.</summary>
public sealed record OpcUaClientOptions
{
    /// <summary>Name shown to servers (for example in their session lists).</summary>
    public string ApplicationName { get; init; } = "Cictna OPC UA Client";

    /// <summary>Globally unique URI of this client; must match the certificate's URI when one is used.</summary>
    public string ApplicationUri { get; init; } = $"urn:{Environment.MachineName}:cictna:opcua-client";

    /// <summary>
    /// Required channel security. <see cref="MessageSecurityMode.Invalid"/> picks the first endpoint the
    /// server offers. None is the default for easy first connections on a trusted network; use
    /// SignAndEncrypt on plant networks.
    /// </summary>
    public MessageSecurityMode SecurityMode { get; init; } = MessageSecurityMode.None;

    /// <summary>Required security policy (see <see cref="SecurityPolicies"/>); null accepts any matching the mode.</summary>
    public string? SecurityPolicyUri { get; init; }

    /// <summary>This client's certificate. Required for Sign and SignAndEncrypt.</summary>
    public ApplicationCertificate? Certificate { get; init; }

    /// <summary>
    /// Accept any server certificate without checking it. For development and tests only: it allows
    /// a man-in-the-middle. Phase 3 adds trust lists; until then secure connections need this flag.
    /// </summary>
    public bool AcceptAnyServerCertificateForDevelopment { get; init; }

    /// <summary>The user identity for the session.</summary>
    public UserIdentity Identity { get; init; } = UserIdentity.Anonymous;

    /// <summary>Upper bound for one request (server round trip). Default 10 s.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for <see cref="OpcUaClient.ConnectAsync"/>. Default 15 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Requested session timeout; zero keeps open62541's default.</summary>
    public TimeSpan SessionTimeout { get; init; }

    /// <summary>
    /// Nodes per Read request. Null uses the server's MaxNodesPerRead (or 1000 if it does not say).
    /// Large batches are split automatically.
    /// </summary>
    public int? MaxNodesPerRead { get; init; }

    /// <summary>Nodes per Write request; null uses the server's limit (or 1000).</summary>
    public int? MaxNodesPerWrite { get; init; }

    /// <summary>Nodes per Browse request; null uses the server's limit (or 100).</summary>
    public int? MaxNodesPerBrowse { get; init; }

    /// <summary>References the server should return per node before using a continuation point.</summary>
    public uint MaxReferencesPerNode { get; init; } = 1000;

    /// <summary>Where log lines go (client and native stack). Null disables logging.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Lowest native (open62541) log level forwarded to the logger. Default Warning.</summary>
    public LogLevel NativeLogLevel { get; init; } = LogLevel.Warning;

    /// <summary>What to do when an established connection is lost. Reconnects by default.</summary>
    public ReconnectOptions Reconnect { get; init; } = new();
}

/// <summary>
/// Automatic reconnection after a lost connection (not after <see cref="OpcUaClient.DisconnectAsync"/>).
/// Attempts are spaced with exponential backoff and random jitter, so many clients that lost the same
/// server do not all hit it at the same moment when it comes back.
/// </summary>
public sealed record ReconnectOptions
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

/// <summary>Effective per-request limits, from the server's OperationLimits and the options.</summary>
/// <param name="MaxNodesPerRead">Nodes per Read request.</param>
/// <param name="MaxNodesPerWrite">Nodes per Write request.</param>
/// <param name="MaxNodesPerBrowse">Nodes per Browse request.</param>
public sealed record OperationLimits(int MaxNodesPerRead, int MaxNodesPerWrite, int MaxNodesPerBrowse)
{
    /// <summary>Monitored items per CreateMonitoredItems or DeleteMonitoredItems request.</summary>
    public int MaxMonitoredItemsPerCall { get; init; } = 1000;

    internal static OperationLimits Default { get; } = new(1000, 1000, 100);
}

/// <summary>
/// Metrics of the OPC UA layer (System.Diagnostics.Metrics). Collect them with OpenTelemetry or
/// <c>dotnet-counters monitor --counters Cictna.Industrial.OpcUa</c>.
/// </summary>
public static class OpcUaTelemetry
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "Cictna.Industrial.OpcUa";

    internal static readonly Meter Meter = new(MeterName, typeof(OpcUaTelemetry).Assembly.GetName().Version?.ToString());

    /// <summary>Requests sent, tagged with service and status.</summary>
    internal static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "cictna.opcua.client.requests", description: "OPC UA service requests completed, by service and status.");

    /// <summary>Round-trip time of requests in milliseconds, tagged with service.</summary>
    internal static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "cictna.opcua.client.request.duration", unit: "ms", description: "OPC UA service request round-trip time.");

    /// <summary>Connection state changes, tagged with the new state.</summary>
    internal static readonly Counter<long> StateChanges = Meter.CreateCounter<long>(
        "cictna.opcua.client.state_changes", description: "Client connection state changes, by new state.");

    /// <summary>Publish responses received.</summary>
    internal static readonly Counter<long> PublishResponses = Meter.CreateCounter<long>(
        "cictna.opcua.client.publish_responses", description: "Publish responses received (data and keep-alive).");

    /// <summary>Value changes delivered to subscriptions.</summary>
    internal static readonly Counter<long> Notifications = Meter.CreateCounter<long>(
        "cictna.opcua.client.notifications", description: "Monitored item value changes delivered.");

    /// <summary>Notification messages fetched again with Republish.</summary>
    internal static readonly Counter<long> Republished = Meter.CreateCounter<long>(
        "cictna.opcua.client.republished", description: "Notification messages recovered with Republish.");

    /// <summary>Notification messages that could not be recovered.</summary>
    internal static readonly Counter<long> LostNotificationMessages = Meter.CreateCounter<long>(
        "cictna.opcua.client.lost_notification_messages", description: "Notification messages lost (no longer available on the server).");

    /// <summary>Subscriptions that missed their keep-alive.</summary>
    internal static readonly Counter<long> KeepAliveMisses = Meter.CreateCounter<long>(
        "cictna.opcua.client.keepalive_misses", description: "Subscriptions without keep-alive in time.");

    /// <summary>Subscriptions moved to a new session, tagged transferred or recreated.</summary>
    internal static readonly Counter<long> SubscriptionRecoveries = Meter.CreateCounter<long>(
        "cictna.opcua.client.subscription_recoveries", description: "Subscriptions transferred or recreated after a new session.");

    /// <summary>Reconnect attempts, tagged with the result.</summary>
    internal static readonly Counter<long> ReconnectAttempts = Meter.CreateCounter<long>(
        "cictna.opcua.client.reconnect_attempts", description: "Automatic reconnect attempts, by result.");

    /// <summary>Native log lines dropped because the native queue was full.</summary>
    internal static readonly Counter<long> DroppedLogLines = Meter.CreateCounter<long>(
        "cictna.opcua.native.dropped_log_lines", description: "Native log lines lost to a full queue.");
}
