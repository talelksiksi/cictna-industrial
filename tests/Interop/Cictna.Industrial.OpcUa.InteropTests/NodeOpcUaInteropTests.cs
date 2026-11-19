using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.IntegrationTests;

namespace Cictna.Industrial.OpcUa.InteropTests;

/// <summary>
/// The client against node-opcua: an independent implementation, so problems shared by our codec
/// and open62541 (or by our client and our own server) would show up here.
/// </summary>
public sealed class NodeOpcUaInteropTests(NodeOpcUaServer server) : IClassFixture<NodeOpcUaServer>
{
    /// <summary>The values server.js publishes. Keep in step with server.js.</summary>
    private static readonly (string Name, Variant Value)[] Expected =
    [
        ("Boolean", true),
        ("SByte", Variant.From((sbyte)-5)),
        ("Byte", Variant.From((byte)200)),
        ("Int16", Variant.From((short)-1234)),
        ("UInt16", Variant.From((ushort)54321)),
        ("Int32", -123456),
        ("UInt32", 4000000000u),
        ("Int64", 123456789L),
        ("UInt64", Variant.From(4294967296UL)),
        ("Float", 3.25f),
        ("Double", Math.PI),
        ("String", "Pompe d'acide, خزان 2"),
        ("DateTime", new DateTime(2026, 11, 12, 10, 30, 15, 123, DateTimeKind.Utc)),
        ("Guid", Variant.From(Guid.Parse("09087E75-8E5E-499B-954F-F2A9603DB28A"))),
        ("ByteString", Variant.From(new byte[] { 0, 1, 2, 254, 255 })),
        ("NodeId", Variant.From(new NodeId("Pump01", 1))),
        ("StatusCode", Variant.From(StatusCodes.UncertainLastUsableValue)),
        ("QualifiedName", Variant.From(new QualifiedName(1, "Level"))),
        ("LocalizedText", Variant.From(new LocalizedText("fr", "Débit"))),
        ("BooleanArray", Variant.FromArray(new[] { true, false, true })),
        ("Int32Array", Variant.FromArray(new[] { 1, 2, 3, 4 })),
        ("DoubleArray", Variant.FromArray(new[] { 0.1, -1.5, 1e300 })),
        ("StringArray", Variant.FromArray(new string?[] { "a", "", "عربي" })),
        ("DoubleMatrix", Variant.Matrix(BuiltInType.Double, new[] { 1.0, 2, 3, 4, 5, 6 }, 2, 3)),
    ];

    private NodeId Node(string id) => new(id, server.Ns);

    private async Task<OpcUaClient> ConnectAsync(OpcUaClientOptions? options = null, string? url = null)
    {
        server.EnsureAvailable();
        var client = await OpcUaClient.CreateAsync(options);
        await client.ConnectAsync(url ?? server.Url, TestContext.Current.CancellationToken);
        return client;
    }

    [Fact]
    public async Task ConnectsThroughAPortForward()
    {
        // Like NAT or a Docker port mapping: the client dials a port the server does not know.
        // node-opcua advertises its own machine name and port in FindServers; open62541 would
        // switch to that address and fail. The client must stay on the URL it was given.
        server.EnsureAvailable();
        await using var forward = new TcpProxy(new Uri(server.Url).Port, "localhost");
        await using var client = await ConnectAsync(url: forward.Url);
        Assert.Equal(-123456, (await client.ReadAsync(Node("Int32"), TestContext.Current.CancellationToken)).Value.GetValue<int>());
        Assert.True(forward.AcceptedConnections >= 1);
    }

    [Fact]
    public async Task ConnectsAndReadsNamespacesAndOperationLimits()
    {
        await using var client = await ConnectAsync();
        Assert.Equal(server.NamespaceUri, client.NamespaceUris[server.Ns]);
        Assert.Equal(new OperationLimits(50, 50, 10) { MaxMonitoredItemsPerCall = 20 }, client.Limits);
    }

    [Fact]
    public async Task ReadsEveryTypeExactlyAsNodeOpcUaPublishesIt()
    {
        await using var client = await ConnectAsync();
        var results = await client.ReadAsync(Expected.Select(e => new ReadValueId(Node(e.Name))).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < Expected.Length; i++)
        {
            Assert.True(results[i].Status.IsGood, $"{Expected[i].Name}: {results[i].Status}");
            Assert.True(Expected[i].Value.Equals(results[i].Value), $"{Expected[i].Name}: expected {Expected[i].Value}, got {results[i].Value}");
        }
    }

    [Fact]
    public async Task WritesAreAcceptedAndReadBack()
    {
        await using var client = await ConnectAsync();
        var cases = new (string Name, Variant Value)[]
        {
            ("Double", 2.5), ("Int32", 42), ("String", "écrit par Cictna"), ("Boolean", false),
            ("Int64", long.MinValue + 7), ("DateTime", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("Int32Array", Variant.FromArray(new[] { 9, 8, 7 })),
            ("DoubleMatrix", Variant.Matrix(BuiltInType.Double, new[] { 6.0, 5, 4, 3, 2, 1 }, 2, 3)),
        };
        foreach (var (name, value) in cases)
        {
            var status = await client.WriteAsync(Node(name), value, TestContext.Current.CancellationToken);
            Assert.True(status.IsGood, $"{name}: {status}");
            Assert.Equal(value, (await client.ReadAsync(Node(name), TestContext.Current.CancellationToken)).Value);
        }
        // Restore the published values for the other tests.
        foreach (var (name, _) in cases)
        {
            var original = Expected.First(e => e.Name == name).Value;
            Assert.True((await client.WriteAsync(Node(name), original, TestContext.Current.CancellationToken)).IsGood, name);
        }
    }

    [Fact]
    public async Task WrongTypeIsRejectedPerItem()
    {
        await using var client = await ConnectAsync();
        Assert.Equal(StatusCodes.BadTypeMismatch, await client.WriteAsync(Node("Int32"), 1.5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LargeBatchesRespectNodeOpcUaLimits()
    {
        await using var client = await ConnectAsync();
        var nodes = Enumerable.Range(0, 250).Select(i => new ReadValueId(Node($"Many.Tag{i:000}"))).ToArray();
        var values = await client.ReadAsync(nodes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, 250), values.Select(v => v.Value.GetValue<int>()));
    }

    [Fact]
    public async Task BrowseFollowsNodeOpcUaContinuationPoints()
    {
        await using var client = await ConnectAsync(new OpcUaClientOptions { MaxReferencesPerNode = 20 });
        var result = await client.BrowseAsync(Node("Many"), TestContext.Current.CancellationToken);
        Assert.True(result.Status.IsGood, result.Status.ToString());
        Assert.Equal(250, result.References.Count(r => r.BrowseName.Name!.StartsWith("Tag", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EndpointsIncludeBasic256Sha256()
    {
        server.EnsureAvailable();
        var endpoints = await OpcUaClient.GetEndpointsAsync(server.Url, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(endpoints, e => e.SecurityMode == MessageSecurityMode.SignAndEncrypt && e.SecurityPolicyUri == SecurityPolicies.Basic256Sha256);
        Assert.Contains(endpoints, e => e.SecurityMode == MessageSecurityMode.None);
    }

    [Theory]
    [InlineData(MessageSecurityMode.SignAndEncrypt)]
    [InlineData(MessageSecurityMode.Sign)]
    public async Task SecureChannelToNodeOpcUaWorks(MessageSecurityMode mode)
    {
        const string uri = "urn:cictna:interop:client";
        await using var client = await ConnectAsync(new OpcUaClientOptions
        {
            ApplicationUri = uri,
            Certificate = ApplicationCertificate.CreateSelfSigned(uri, "Cictna Interop Client"),
            SecurityMode = mode,
            SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
            AcceptAnyServerCertificateForDevelopment = true,
        });
        Assert.Equal(mode, client.Endpoint!.SecurityMode);
        var values = await client.ReadAsync(Expected.Select(e => new ReadValueId(Node(e.Name))).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(values, v => Assert.True(v.Status.IsGood));
    }
}
