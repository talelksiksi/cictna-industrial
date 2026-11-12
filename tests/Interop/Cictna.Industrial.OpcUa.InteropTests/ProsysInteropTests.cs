using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.InteropTests;

/// <summary>
/// The client against the Prosys OPC UA Simulation Server, a widely used commercial-grade
/// implementation (Java SDK). Runs only when CICTNA_PROSYS_URL points to a running server, e.g.
/// <c>opc.tcp://localhost:53530/OPCUA/SimulationServer</c>; skipped otherwise. See docs/pc-steps.md.
/// </summary>
/// <remarks>
/// The tests discover what they need by browsing (the Simulation folder and its signals) instead
/// of hard-coding NodeIds, so they work across Prosys versions and configurations.
/// </remarks>
public sealed class ProsysInteropTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CICTNA_PROSYS_URL");

    private static void RequireServer() =>
        Assert.SkipWhen(string.IsNullOrEmpty(Url), "Set CICTNA_PROSYS_URL to run the Prosys interop tests (docs/pc-steps.md).");

    private static async Task<OpcUaClient> ConnectAsync(OpcUaClientOptions? options = null)
    {
        RequireServer();
        var client = await OpcUaClient.CreateAsync(options);
        await client.ConnectAsync(Url!, TestContext.Current.CancellationToken);
        return client;
    }

    private static async Task<IReadOnlyList<ReferenceDescription>> SimulationSignalsAsync(OpcUaClient client)
    {
        var objects = await client.BrowseAsync(WellKnownNodes.ObjectsFolder, TestContext.Current.CancellationToken);
        var simulation = objects.References.FirstOrDefault(r => r.BrowseName.Name == "Simulation");
        Assert.NotNull(simulation);
        var children = await client.BrowseAsync(simulation.NodeId.NodeId, TestContext.Current.CancellationToken);
        var variables = children.References.Where(r => r.NodeClass == NodeClass.Variable).ToList();
        Assert.NotEmpty(variables);
        return variables;
    }

    [Fact]
    public async Task ConnectsBrowsesAndReadsTheSimulationSignals()
    {
        await using var client = await ConnectAsync();
        var signals = await SimulationSignalsAsync(client);
        var values = await client.ReadAsync(signals.Select(s => new ReadValueId(s.NodeId.NodeId)).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(values, v => Assert.True(v.Status.IsGood, v.Status.ToString()));
        Assert.All(values, v => Assert.NotNull(v.SourceTimestamp));
    }

    [Fact]
    public async Task SignalsChangeOverTime()
    {
        await using var client = await ConnectAsync();
        var signals = await SimulationSignalsAsync(client);
        var nodes = signals.Select(s => new ReadValueId(s.NodeId.NodeId)).ToArray();
        var first = await client.ReadAsync(nodes, cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var second = await client.ReadAsync(nodes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(Enumerable.Range(0, nodes.Length), i => !first[i].Value.Equals(second[i].Value));
    }

    [Fact]
    public async Task ReadsServerInfoAndLimits()
    {
        await using var client = await ConnectAsync();
        Assert.Equal("http://opcfoundation.org/UA/", client.NamespaceUris[0]);
        Assert.True(client.NamespaceUris.Count > 2);
        Assert.True(client.Limits.MaxNodesPerRead > 0);
        var state = await client.ReadAsync(WellKnownNodes.ServerState, TestContext.Current.CancellationToken);
        Assert.Equal(0, state.Value.GetValue<int>()); // ServerState.Running
    }

    [Fact]
    public async Task GetEndpointsListsTheProsysEndpoints()
    {
        RequireServer();
        var endpoints = await OpcUaClient.GetEndpointsAsync(Url!, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, e => e.Server.ApplicationUri?.Contains("Prosys", StringComparison.OrdinalIgnoreCase) == true
            || e.Server.ApplicationName.Text?.Contains("Simulation", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task SecureChannelToProsysWorksOnceTheClientCertificateIsTrusted()
    {
        // Prosys rejects unknown client certificates until they are trusted in its Certificates
        // tab. The certificate is kept in a file so it is the same on every run; trust it once
        // (docs/pc-steps.md) and this test passes from then on.
        RequireServer();
        const string uri = "urn:cictna:interop:prosys-client";
        var dir = Path.Combine(Path.GetTempPath(), "cictna-interop");
        Directory.CreateDirectory(dir);
        var certFile = Path.Combine(dir, "client.der");
        var keyFile = Path.Combine(dir, "client.key.der");
        ApplicationCertificate certificate;
        if (File.Exists(certFile) && File.Exists(keyFile))
        {
            certificate = new ApplicationCertificate(await File.ReadAllBytesAsync(certFile, TestContext.Current.CancellationToken),
                await File.ReadAllBytesAsync(keyFile, TestContext.Current.CancellationToken));
        }
        else
        {
            certificate = ApplicationCertificate.CreateSelfSigned(uri, "Cictna Interop Client", "CICTNA RDC");
            await File.WriteAllBytesAsync(certFile, certificate.Certificate, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(keyFile, certificate.PrivateKey, TestContext.Current.CancellationToken);
        }

        await using var client = await OpcUaClient.CreateAsync(new OpcUaClientOptions
        {
            ApplicationUri = uri,
            Certificate = certificate,
            SecurityMode = MessageSecurityMode.SignAndEncrypt,
            SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
            AcceptAnyServerCertificateForDevelopment = true,
        });
        try
        {
            await client.ConnectAsync(Url!, TestContext.Current.CancellationToken);
        }
        catch (OpcUaException ex)
        {
            Assert.Fail($"Secure connection refused ({ex.StatusCode.Name}). If this is the first run, trust the certificate " +
                $"'Cictna Interop Client' in Prosys (Certificates tab), then run again. Certificate file: {certFile}");
        }
        Assert.True((await client.ReadAsync(WellKnownNodes.ServerCurrentTime, TestContext.Current.CancellationToken)).Status.IsGood);
    }
}
