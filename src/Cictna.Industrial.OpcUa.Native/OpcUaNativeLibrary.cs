namespace Cictna.Industrial.OpcUa.Native;

/// <summary>
/// Version, build features and health checks of the native OPC UA library
/// (open62541 with mbedTLS, behind the cictna_ua shim).
/// </summary>
/// <remarks>
/// <para>
/// Where this sits: <c>Cictna.Industrial.OpcUa.Native</c> is the lowest managed layer. It owns the
/// native binaries (one per runtime, under <c>runtimes/&lt;rid&gt;/native/</c>) and the P/Invoke
/// declarations. The OPC UA client and server types arrive in <c>Cictna.Industrial.OpcUa</c>
/// (Phase 1) and build on this assembly; applications normally never call it directly, except
/// for <see cref="EnsureCompatible"/> at startup and the self test in health checks.
/// </para>
/// <para>
/// Why the checks matter: the managed and native halves are built separately (C# by
/// <c>dotnet</c>, C by CMake on three platforms) and meet only at run time. A stale DLL copied
/// next to a newer assembly would otherwise fail with confusing access violations deep inside a
/// call. The ABI version and feature flags turn that into one clear error at startup.
/// </para>
/// <para>
/// Thread safety: every member is safe to call from any thread. The properties read static data
/// from the library; <see cref="RunLoopbackSelfTest"/> creates its own server and client and
/// shares nothing with other calls.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// OpcUaNativeLibrary.EnsureCompatible();
/// Console.WriteLine($"open62541 {OpcUaNativeLibrary.StackVersion}, crypto {OpcUaNativeLibrary.EncryptionBackend}");
/// </code>
/// </example>
public static class OpcUaNativeLibrary
{
    /// <summary>The shim ABI version this assembly was written against.</summary>
    public const int ExpectedAbiVersion = 3;

    /// <summary>ABI version reported by the loaded native library.</summary>
    public static int AbiVersion => NativeMethods.AbiVersion();

    /// <summary>open62541 version, for example "v1.5.10".</summary>
    public static string StackVersion => NativeMethods.ReadStaticString(NativeMethods.StackVersion());

    /// <summary>Encryption backend compiled into the native library: "mbedtls", "openssl" or "none".</summary>
    public static string EncryptionBackend => NativeMethods.ReadStaticString(NativeMethods.EncryptionBackend());

    /// <summary>Features compiled into the native library.</summary>
    public static NativeFeatures Features => (NativeFeatures)NativeMethods.Features();

    /// <summary>Runtime id used to locate the native library, for example "win-x64".</summary>
    public static string RuntimeId => NativeLibraryResolver.CurrentRid;

    /// <summary>Symbolic name of an OPC UA status code, for example "BadTimeout".</summary>
    /// <param name="statusCode">The 32-bit OPC UA status code.</param>
    public static string GetStatusName(uint statusCode) =>
        NativeMethods.ReadStaticString(NativeMethods.StatusName(statusCode));

    /// <summary>
    /// Loads the native library and checks that it matches this assembly.
    /// Call once at startup so a broken install fails early with a clear message.
    /// </summary>
    /// <exception cref="DllNotFoundException">The native library for this platform is not installed.</exception>
    /// <exception cref="InvalidOperationException">The native library is a different ABI version.</exception>
    public static void EnsureCompatible()
    {
        var abi = AbiVersion;
        if (abi != ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"Native library cictna_ua has ABI version {abi}, but this assembly expects {ExpectedAbiVersion}. " +
                "The native binaries and the managed package come from different builds.");
        }
    }

    /// <summary>
    /// Starts an OPC UA server on 127.0.0.1, connects a client to it, reads the server's
    /// current time and shuts both down. Proves that the stack, networking and encoding
    /// work end to end on this machine. Blocks the calling thread for at most <paramref name="timeout"/>.
    /// </summary>
    /// <param name="port">A free TCP port on the loopback interface.</param>
    /// <param name="timeout">Upper bound for the whole test.</param>
    public static LoopbackSelfTestResult RunLoopbackSelfTest(ushort port, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfZero(port);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var timeoutMs = (uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue);

        var status = NativeMethods.SelfTestLoopback(port, timeoutMs, out var serverTime);

        // OPC UA DateTime is 100 ns ticks since 1601-01-01 UTC, the same epoch and unit as a
        // Windows FILETIME, so FromFileTimeUtc converts it exactly on every OS.
        DateTimeOffset? time = status == 0 && serverTime > 0
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(serverTime))
            : null;
        return new LoopbackSelfTestResult(status, GetStatusName(status), time);
    }
}

/// <summary>Features compiled into the native library.</summary>
[Flags]
public enum NativeFeatures : uint
{
    /// <summary>No optional features.</summary>
    None = 0,

    /// <summary>Sign and SignAndEncrypt security policies (Basic256Sha256 and others).</summary>
    Encryption = 0x0001,

    /// <summary>open62541 built with internal locking (UA_MULTITHREADING &gt;= 100).</summary>
    Multithreading = 0x0002,

    /// <summary>Subscriptions and monitored items.</summary>
    Subscriptions = 0x0004,

    /// <summary>Method calls.</summary>
    MethodCalls = 0x0008,

    /// <summary>Historical access.</summary>
    Historizing = 0x0010,
}

/// <summary>Outcome of <see cref="OpcUaNativeLibrary.RunLoopbackSelfTest"/>.</summary>
/// <param name="StatusCode">0 (Good) on success, otherwise the first bad OPC UA status code.</param>
/// <param name="StatusName">Symbolic name of <paramref name="StatusCode"/>.</param>
/// <param name="ServerTime">The server's current time as read by the client, when the test succeeded.</param>
public readonly record struct LoopbackSelfTestResult(uint StatusCode, string StatusName, DateTimeOffset? ServerTime)
{
    /// <summary>True when the client connected and read the server time.</summary>
    public bool Succeeded => StatusCode == 0;
}
