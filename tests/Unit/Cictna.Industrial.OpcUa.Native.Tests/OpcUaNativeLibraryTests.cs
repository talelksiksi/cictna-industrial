using System.Net;
using System.Net.Sockets;

namespace Cictna.Industrial.OpcUa.Native.Tests;

public sealed class OpcUaNativeLibraryTests
{
    [Fact]
    public void AbiVersionMatchesManagedExpectation()
    {
        Assert.Equal(OpcUaNativeLibrary.ExpectedAbiVersion, OpcUaNativeLibrary.AbiVersion);
        OpcUaNativeLibrary.EnsureCompatible();
    }

    [Fact]
    public void StackVersionIsThePinnedOpen62541Release()
    {
        var pinned = File.ReadLines(Path.Combine(RepoRoot(), "native", "versions.env"))
            .Single(l => l.StartsWith("OPEN62541_TAG=", StringComparison.Ordinal))
            .Split('=', 2)[1]
            .Trim();
        Assert.Equal(pinned, OpcUaNativeLibrary.StackVersion);
    }

    [Fact]
    public void EncryptionIsMbedTls()
    {
        Assert.Equal("mbedtls", OpcUaNativeLibrary.EncryptionBackend);
        Assert.True(OpcUaNativeLibrary.Features.HasFlag(NativeFeatures.Encryption));
    }

    [Fact]
    public void RequiredFeaturesAreCompiledIn()
    {
        const NativeFeatures required =
            NativeFeatures.Encryption | NativeFeatures.Multithreading | NativeFeatures.Subscriptions;
        Assert.Equal(required, OpcUaNativeLibrary.Features & required);
    }

    [Theory]
    [InlineData(0x00000000u, "Good")]
    [InlineData(0x800A0000u, "BadTimeout")]
    [InlineData(0x80340000u, "BadNodeIdUnknown")]
    public void StatusCodesHaveSymbolicNames(uint code, string expected)
    {
        Assert.Equal(expected, OpcUaNativeLibrary.GetStatusName(code));
    }

    [Fact]
    public void LoopbackServerAndClientExchangeTheServerTime()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var result = OpcUaNativeLibrary.RunLoopbackSelfTest(FreeLoopbackPort(), TimeSpan.FromSeconds(10));
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.True(result.Succeeded, result.StatusName);
        Assert.NotNull(result.ServerTime);
        Assert.InRange(result.ServerTime.Value, before, after);
    }

    [Fact]
    public void LoopbackReportsAFailureInsteadOfHangingWhenThePortIsTaken()
    {
        using var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        var port = (ushort)((IPEndPoint)blocker.LocalEndpoint).Port;

        var started = DateTimeOffset.UtcNow;
        var result = OpcUaNativeLibrary.RunLoopbackSelfTest(port, TimeSpan.FromSeconds(3));

        Assert.False(result.Succeeded);
        Assert.StartsWith("Bad", result.StatusName, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(6), "self test overran its timeout");
    }

    [Fact]
    public void LoopbackRejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OpcUaNativeLibrary.RunLoopbackSelfTest(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => OpcUaNativeLibrary.RunLoopbackSelfTest(4840, TimeSpan.Zero));
    }

    [Fact]
    public void RuntimeIdMatchesTheProcess()
    {
        var rid = OpcUaNativeLibrary.RuntimeId;
        Assert.Matches("^(win|linux|osx)-(x64|arm64|x86|arm)$", rid);
    }

    private static ushort FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
