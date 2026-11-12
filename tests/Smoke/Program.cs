// Phase 0 smoke test, run by CI on every platform and by hand after installing on a new machine:
//
//   dotnet run --project tests/Smoke -c Release
//
// It answers one question: does the native OPC UA stack load and work on this machine?
// Deliberately a console app with no test framework, so it also runs where only the .NET
// runtime is installed (a plant PC, an edge box). Prints one PASS/FAIL line per check and
// exits with 0 only if everything passed, so scripts and CI can rely on the exit code.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cictna.Industrial.OpcUa.Native;

var failures = 0;

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
    if (!ok)
    {
        failures++;
    }
}

Console.WriteLine($"Runtime: {OpcUaNativeLibrary.RuntimeId}, .NET {Environment.Version}");

int abi;
try
{
    abi = OpcUaNativeLibrary.AbiVersion;
}
catch (DllNotFoundException ex)
{
    Console.WriteLine($"[FAIL] load native library: {ex.Message}");
    Console.WriteLine("SMOKE FAILED");
    return 1;
}

Check("native library loads", true, $"open62541 {OpcUaNativeLibrary.StackVersion}");
Check("ABI version", abi == OpcUaNativeLibrary.ExpectedAbiVersion,
    $"{abi} (expected {OpcUaNativeLibrary.ExpectedAbiVersion})");
Check("encryption backend", OpcUaNativeLibrary.EncryptionBackend == "mbedtls", OpcUaNativeLibrary.EncryptionBackend);

var features = OpcUaNativeLibrary.Features;
const NativeFeatures required = NativeFeatures.Encryption | NativeFeatures.Multithreading | NativeFeatures.Subscriptions;
Check("required features", (features & required) == required, features.ToString());

var port = FreeLoopbackPort();
var result = OpcUaNativeLibrary.RunLoopbackSelfTest(port, TimeSpan.FromSeconds(10));
var serverTime = result.ServerTime?.ToString("O", CultureInfo.InvariantCulture) ?? "none";
var clockSkew = result.ServerTime is { } t ? (DateTimeOffset.UtcNow - t).Duration() : TimeSpan.MaxValue;
Check("loopback server + client read", result.Succeeded,
    $"port {port}, {result.StatusName}, server time {serverTime}");
Check("server time is plausible", clockSkew < TimeSpan.FromMinutes(1), $"skew {clockSkew.TotalMilliseconds:F0} ms");

Console.WriteLine(failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({failures})");
return failures == 0 ? 0 : 1;

static ushort FreeLoopbackPort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return (ushort)port;
}
