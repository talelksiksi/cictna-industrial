using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Cictna.Industrial.Modbus.InteropTests;

/// <summary>
/// Runs the independent Modbus implementations in tests/Interop/modbus-peers: server.js as a
/// server for our client, client.js as a client of our server. Installs their npm dependencies
/// on first use (npm ci, from the committed lock file).
/// </summary>
internal static class ModbusPeers
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static string? _directory;
    private static string? _unavailable;
    private static bool _checked;

    /// <summary>Call at the start of every test: skips (or fails in CI) when Node.js cannot run.</summary>
    public static async Task<string> EnsureAvailableAsync()
    {
        await InstallLock.WaitAsync();
        try
        {
            if (!_checked)
            {
                _checked = true;
                _unavailable = await PrepareAsync();
            }
        }
        finally
        {
            InstallLock.Release();
        }
        if (_unavailable is not null)
        {
            if (Environment.GetEnvironmentVariable("CICTNA_REQUIRE_INTEROP") == "1")
            {
                Assert.Fail($"Modbus interop peers required but unavailable: {_unavailable}");
            }
            Assert.Skip($"Modbus interop peers unavailable: {_unavailable}");
        }
        return _directory!;
    }

    private static async Task<string?> PrepareAsync()
    {
        _directory = FindDirectory();
        if (_directory is null)
        {
            return "tests/Interop/modbus-peers not found";
        }
        if (await RunAsync("node", "--version", TimeSpan.FromSeconds(20)) is not { ExitCode: 0 })
        {
            return "Node.js (node) is not installed or not on PATH; install Node.js 22.13 or newer";
        }
        if (!Directory.Exists(Path.Combine(_directory, "node_modules")) &&
            await RunAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", "ci --no-audit --no-fund", TimeSpan.FromMinutes(5)) is not { ExitCode: 0 })
        {
            return "npm ci failed in tests/Interop/modbus-peers";
        }
        return null;
    }

    /// <summary>Runs client.js against <paramref name="port"/> and returns its JSON report.</summary>
    public static async Task<JsonElement> RunClientAsync(string lib, int port)
    {
        var result = await RunAsync("node", $"client.js {lib} {port}", TimeSpan.FromSeconds(90));
        Assert.True(result is { ExitCode: 0 }, $"client.js {lib} failed: {result?.Error}");
        using var json = JsonDocument.Parse(result!.Value.Output.Trim());
        return json.RootElement.Clone();
    }

    /// <summary>Starts server.js with <paramref name="lib"/> on a free port.</summary>
    public static async Task<PeerServer> StartServerAsync(string lib)
    {
        var port = FreePort();
        var process = Process.Start(new ProcessStartInfo("node", $"server.js {lib} {port}")
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // closing stdin stops the server
            UseShellExecute = false,
        })!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var line = await process.StandardOutput.ReadLineAsync(cts.Token);
        if (line is null || !line.StartsWith("{\"ready\":true", StringComparison.Ordinal))
        {
            var error = await process.StandardError.ReadToEndAsync(cts.Token);
            process.Kill(entireProcessTree: true);
            process.Dispose();
            Assert.Fail($"server.js {lib} did not start: {line} {error}");
        }
        return new PeerServer(process, port);
    }

    private static async Task<(int ExitCode, string Output, string Error)?> RunAsync(string file, string args, TimeSpan timeout)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                WorkingDirectory = _directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            using var cts = new CancellationTokenSource(timeout);
            var output = p.StandardOutput.ReadToEndAsync(cts.Token);
            var error = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, await output, await error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    private static string? FindDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Interop", "modbus-peers");
            if (File.Exists(Path.Combine(candidate, "server.js")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>A running server.js; disposing closes its stdin, which stops it.</summary>
internal sealed class PeerServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly System.Text.StringBuilder _errors = new();
    private readonly Task _drain;

    public PeerServer(Process process, int port)
    {
        _process = process;
        Port = port;
        _drain = DrainAsync();
    }

    public int Port { get; }

    /// <summary>Why the server is gone, or null while it runs.</summary>
    public string? Exited
    {
        get
        {
            if (!_process.HasExited)
            {
                return null;
            }
            lock (_errors)
            {
                return $"exit code {_process.ExitCode}: {_errors}";
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }
        await _drain;
        _process.Dispose();
    }

    private async Task DrainAsync()
    {
        try
        {
            var output = _process.StandardOutput.ReadToEndAsync();
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync()) is not null)
            {
                lock (_errors)
                {
                    _errors.AppendLine(line);
                }
            }
            await output;
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
