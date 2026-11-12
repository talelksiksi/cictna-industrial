using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Cictna.Industrial.OpcUa.InteropTests;

/// <summary>
/// Starts tests/Interop/node-opcua-server/server.js on a free port and stops it afterwards.
/// Installs its npm dependencies on first use (npm ci, from the committed lock file).
/// </summary>
public sealed class NodeOpcUaServer : IAsyncLifetime
{
    private Process? _process;

    public int Port { get; private set; }
    public string Url => $"opc.tcp://localhost:{Port}";
    public string NamespaceUri { get; private set; } = string.Empty;
    public ushort Ns { get; private set; }

    /// <summary>Why the server is not available, or null when it is running.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>Call at the start of every test: skips (or fails in CI) when node-opcua cannot run.</summary>
    public void EnsureAvailable()
    {
        if (Unavailable is null)
        {
            return;
        }
        if (Environment.GetEnvironmentVariable("CICTNA_REQUIRE_INTEROP") == "1")
        {
            Assert.Fail($"Interop server required but unavailable: {Unavailable}");
        }
        Assert.Skip($"node-opcua interop server unavailable: {Unavailable}");
    }

    public async ValueTask InitializeAsync()
    {
        var dir = FindServerDirectory();
        if (dir is null)
        {
            Unavailable = "tests/Interop/node-opcua-server not found";
            return;
        }
        if (!await RunAsync("node", "--version", dir, TimeSpan.FromSeconds(20)))
        {
            Unavailable = "Node.js (node) is not installed or not on PATH; install Node.js 22.13 or newer";
            return;
        }
        if (!Directory.Exists(Path.Combine(dir, "node_modules")) &&
            !await RunAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", "ci --no-audit --no-fund", dir, TimeSpan.FromMinutes(5)))
        {
            Unavailable = "npm ci failed in tests/Interop/node-opcua-server";
            return;
        }

        Port = FreePort();
        _process = Process.Start(new ProcessStartInfo("node", $"server.js {Port}")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // closing stdin stops the server (see server.js)
            UseShellExecute = false,
        })!;

        // The first start generates the server certificate, which can take a while.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!cts.IsCancellationRequested)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cts.Token);
            if (line is null)
            {
                Unavailable = "node-opcua server exited: " + await _process.StandardError.ReadToEndAsync(cts.Token);
                return;
            }
            if (line.StartsWith("{\"ready\":true", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(line);
                NamespaceUri = json.RootElement.GetProperty("namespaceUri").GetString()!;
                Ns = json.RootElement.GetProperty("namespaceIndex").GetUInt16();
                _ = DrainAsync(_process);
                return;
            }
        }
        Unavailable = "node-opcua server did not report ready within 90 s";
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }
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
        _process.Dispose();
    }

    private static async Task DrainAsync(Process process)
    {
        // Keep reading so a chatty server never blocks on a full pipe.
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is not null) { }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<bool> RunAsync(string file, string args, string dir, TimeSpan timeout)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            using var cts = new CancellationTokenSource(timeout);
            await Task.WhenAll(p.StandardOutput.ReadToEndAsync(cts.Token), p.StandardError.ReadToEndAsync(cts.Token), p.WaitForExitAsync(cts.Token));
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return false;
        }
    }

    private static string? FindServerDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Interop", "node-opcua-server");
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
