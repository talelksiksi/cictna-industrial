using Cictna.Industrial.Core;
using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.OpcUa.IntegrationTests;

/// <summary>
/// The client against our own server over TCP: connection, every built-in type both ways,
/// batching against operation limits, browse continuation points, errors and cancellation.
/// </summary>
public sealed class ClientTests(TestServer server) : IClassFixture<TestServer>
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private async Task<OpcUaClient> ConnectAsync(OpcUaClientOptions? options = null)
    {
        var client = await OpcUaClient.CreateAsync(options ?? new OpcUaClientOptions { LoggerFactory = server.Logs });
        await client.ConnectAsync(server.Url, TestContext.Current.CancellationToken);
        return client;
    }

    [Fact]
    public async Task ConnectsAndReadsNamespacesAndLimits()
    {
        await using var client = await ConnectAsync();
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Contains(TestServer.NamespaceUri, client.NamespaceUris);
        Assert.Equal(server.Ns, client.NamespaceUris.ToList().IndexOf(TestServer.NamespaceUri));
        Assert.Equal("http://opcfoundation.org/UA/", client.NamespaceUris[0]);
        Assert.True(client.Limits.MaxNodesPerRead > 0);
    }

    [Fact]
    public async Task ReadsEveryBuiltInTypeScalarAndArray()
    {
        await using var client = await ConnectAsync();
        var names = server.Variables.Keys.ToArray();
        var results = await client.ReadAsync(names.Select(n => new ReadValueId(server.Variables[n].Node)).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < names.Length; i++)
        {
            Assert.True(results[i].Status.IsGood, $"{names[i]}: {results[i].Status}");
            Assert.Equal(server.Variables[names[i]].Initial, results[i].Value);
            Assert.NotNull(results[i].ServerTimestamp);
        }
    }

    [Fact]
    public async Task WritesEveryBuiltInTypeAndReadsItBack()
    {
        await using var client = await ConnectAsync();
        foreach (var (name, (node, initial)) in server.Variables)
        {
            var changed = Changed(initial);
            var status = await client.WriteAsync(node, changed, TestContext.Current.CancellationToken);
            Assert.True(status.IsGood, $"{name}: {status}");
            var back = await client.ReadAsync(node, TestContext.Current.CancellationToken);
            Assert.Equal(changed, back.Value);
            Assert.Equal(changed, (await server.Server.ReadValueAsync(node)).Value);
            Assert.True((await client.WriteAsync(node, initial, TestContext.Current.CancellationToken)).IsGood, $"{name} restore");
        }
    }

    [Fact]
    public async Task ServerSideUpdatesAreVisibleWithQualityAndTimestamps()
    {
        await using var client = await ConnectAsync();
        var node = server.Variables["Double"].Node;
        var source = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await server.Server.WriteValueAsync(node, new DataValue(42.5, StatusCodes.UncertainSensorNotAccurate, source));
        var value = await client.ReadAsync(node, TestContext.Current.CancellationToken);
        Assert.Equal(42.5, value.Value.GetValue<double>());
        Assert.Equal(StatusCodes.UncertainSensorNotAccurate, value.Status);
        Assert.Equal(source, value.SourceTimestamp);
        await server.Server.WriteValueAsync(node, new DataValue(Math.PI));
    }

    [Fact]
    public async Task ItemErrorsAreResultsNotExceptions()
    {
        await using var client = await ConnectAsync();
        var results = await client.ReadAsync(
            [new ReadValueId(new NodeId("DoesNotExist", server.Ns)), new ReadValueId(server.Variables["Int32"].Node)],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.BadNodeIdUnknown, results[0].Status);
        Assert.True(results[1].Status.IsGood);

        Assert.Equal(StatusCodes.BadTypeMismatch, await client.WriteAsync(server.Variables["Int32"].Node, 1.5, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.BadNotWritable, (await client.WriteAsync(server.ReadOnly, 8, TestContext.Current.CancellationToken)).WithoutFlags);
        Assert.Equal(StatusCodes.BadNodeIdUnknown, await client.WriteAsync(new NodeId("Nope", server.Ns), 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsOtherAttributes()
    {
        await using var client = await ConnectAsync();
        var node = server.Variables["DoubleMatrix"].Node;
        var r = await client.ReadAsync(
            [
                new ReadValueId(node, AttributeId.DisplayName),
                new ReadValueId(node, AttributeId.DataType),
                new ReadValueId(node, AttributeId.ValueRank),
                new ReadValueId(node, AttributeId.ArrayDimensions),
                new ReadValueId(node, AttributeId.NodeClass),
            ], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new LocalizedText(string.Empty, "DoubleMatrix").Text, r[0].Value.GetValue<LocalizedText>().Text);
        Assert.Equal(new NodeId(11), r[1].Value.GetValue<NodeId>());
        Assert.Equal(2, r[2].Value.GetValue<int>());
        Assert.Equal(new uint[] { 2, 3 }, r[3].Value.GetValue<uint[]>());
        Assert.Equal((int)NodeClass.Variable, r[4].Value.GetValue<int>());
    }

    [Fact]
    public async Task LargeReadsAndWritesAreSplitAndKeepOrder()
    {
        await using var client = await ConnectAsync();
        var nodes = Enumerable.Range(0, TestServer.ManyCount).Select(i => new ReadValueId(server.ManyTag(i))).ToArray();
        var values = await client.ReadAsync(nodes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TestServer.ManyCount, values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            Assert.Equal(i, values[i].Value.GetValue<int>());
        }

        var writes = Enumerable.Range(0, TestServer.ManyCount).Select(i => new WriteValue(server.ManyTag(i), new DataValue(i * 10))).ToArray();
        var statuses = await client.WriteAsync(writes, TestContext.Current.CancellationToken);
        Assert.All(statuses, s => Assert.True(s.IsGood));
        var after = await client.ReadAsync(nodes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, TestServer.ManyCount).Select(i => i * 10), after.Select(v => v.Value.GetValue<int>()));
        await client.WriteAsync(Enumerable.Range(0, TestServer.ManyCount).Select(i => new WriteValue(server.ManyTag(i), new DataValue(i))).ToArray(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClientSideLimitSmallerThanServerAlsoWorks()
    {
        await using var client = await ConnectAsync(new OpcUaClientOptions { MaxNodesPerRead = 7 });
        Assert.Equal(7, client.Limits.MaxNodesPerRead);
        var values = await client.ReadAsync(Enumerable.Range(0, 30).Select(i => new ReadValueId(server.ManyTag(i))).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, 30), values.Select(v => v.Value.GetValue<int>()));
    }

    [Fact]
    public async Task OversizedBatchIsRetriedInSmallerChunks()
    {
        // Pretend the server allows 1000 nodes per read: it answers BadTooManyOperations and the
        // client halves the chunk until requests fit, without losing order.
        await using var client = await ConnectAsync(new OpcUaClientOptions { MaxNodesPerRead = 1000 });
        var values = await client.ReadAsync(Enumerable.Range(0, TestServer.ManyCount).Select(i => new ReadValueId(server.ManyTag(i))).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, TestServer.ManyCount), values.Select(v => v.Value.GetValue<int>()));
        Assert.True(client.Limits.MaxNodesPerRead <= TestServer.MaxNodesPerRead);
    }

    [Fact]
    public async Task BrowseFollowsContinuationPoints()
    {
        await using var client = await ConnectAsync(new OpcUaClientOptions { MaxReferencesPerNode = 20 });
        var result = await client.BrowseAsync(server.ManyFolder, TestContext.Current.CancellationToken);
        Assert.True(result.Status.IsGood, result.Status.ToString());
        var names = result.References.Select(r => r.BrowseName.Name).Where(n => n!.StartsWith("Tag", StringComparison.Ordinal)).ToHashSet();
        Assert.Equal(TestServer.ManyCount, names.Count);
        Assert.All(result.References.Where(r => r.BrowseName.Name!.StartsWith("Tag", StringComparison.Ordinal)),
            r => Assert.Equal(NodeClass.Variable, r.NodeClass));
    }

    [Fact]
    public async Task BrowseFromTheObjectsFolderFindsTheTestFolders()
    {
        await using var client = await ConnectAsync();
        var result = await client.BrowseAsync(WellKnownNodes.ObjectsFolder, TestContext.Current.CancellationToken);
        var names = result.References.Select(r => r.BrowseName).ToList();
        Assert.Contains(new QualifiedName(server.Ns, "Types"), names);
        Assert.Contains(new QualifiedName(server.Ns, "Many"), names);
        Assert.Contains(new QualifiedName(0, "Server"), names);
    }

    [Fact]
    public async Task ManyBrowsesInOneCallAreSplitByTheServerLimit()
    {
        await using var client = await ConnectAsync();
        var nodes = Enumerable.Range(0, 25).Select(i => new BrowseDescription(server.ManyTag(i), BrowseDirection.Both)).ToArray();
        var results = await client.BrowseAsync(nodes, TestContext.Current.CancellationToken);
        Assert.Equal(25, results.Count);
        Assert.All(results, r => Assert.Contains(r.References, x => !x.IsForward && x.NodeId.NodeId == server.ManyFolder));
    }

    [Fact]
    public async Task GetEndpointsListsTheServerEndpoint()
    {
        var endpoints = await OpcUaClient.GetEndpointsAsync(server.Url, cancellationToken: TestContext.Current.CancellationToken);
        var none = Assert.Single(endpoints, e => e.SecurityMode == MessageSecurityMode.None);
        Assert.Equal(SecurityPolicies.None, none.SecurityPolicyUri);
        Assert.Contains(none.UserIdentityTokens, t => t.TokenType == UserTokenType.Anonymous);
        Assert.Equal(ApplicationType.Server, none.Server.ApplicationType);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneSession()
    {
        await using var client = await ConnectAsync();
        var tasks = Enumerable.Range(0, 200).Select(i => client.ReadAsync(server.ManyTag(i % TestServer.ManyCount), TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(i % TestServer.ManyCount, results[i].Value.GetValue<int>());
        }
    }

    [Fact]
    public async Task StateChangesAreReportedInOrder()
    {
        await using var client = await OpcUaClient.CreateAsync();
        var states = new List<ConnectionState>();
        var disconnected = new TaskCompletionSource();
        client.StateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.State);
            }
            if (e.State == ConnectionState.Disconnected)
            {
                disconnected.TrySetResult();
            }
        };
        await client.ConnectAsync(server.Url, TestContext.Current.CancellationToken);
        await client.DisconnectAsync(TestContext.Current.CancellationToken);
        await disconnected.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
        lock (states)
        {
            Assert.Equal([ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Disconnected], states);
        }
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task ReconnectAfterDisconnectWorks()
    {
        await using var client = await ConnectAsync();
        await client.DisconnectAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(server.Url, TestContext.Current.CancellationToken);
        Assert.Equal(-123456, (await client.ReadAsync(server.Variables["Int32"].Node, TestContext.Current.CancellationToken)).Value.GetValue<int>());
    }

    [Fact]
    public async Task CallsWhileDisconnectedFailWithBadNotConnected()
    {
        await using var client = await OpcUaClient.CreateAsync();
        var ex = await Assert.ThrowsAsync<OpcUaException>(() => client.ReadAsync(new NodeId(2258), TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.BadNotConnected, ex.StatusCode);
    }

    [Fact]
    public async Task ConnectingToAClosedPortFailsQuickly()
    {
        await using var client = await OpcUaClient.CreateAsync(new OpcUaClientOptions { ConnectTimeout = TimeSpan.FromSeconds(5) });
        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync($"opc.tcp://127.0.0.1:{TestServer.FreePort()}", TestContext.Current.CancellationToken));
        Assert.True(ex.StatusCode.IsBad);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(6));
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task CancelledCallsThrowOperationCanceled()
    {
        await using var client = await ConnectAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync(new NodeId(2258), cts.Token));
        // The client is still fine afterwards.
        Assert.True((await client.ReadAsync(new NodeId(2258), TestContext.Current.CancellationToken)).Status.IsGood);
    }

    [Fact]
    public async Task LosingTheServerIsReportedAndCallsFail()
    {
        await using var own = new TestServer();
        await own.InitializeAsync();
        // Without automatic reconnection the client stays Disconnected (see ChaosTests for reconnecting).
        await using var client = await OpcUaClient.CreateAsync(new OpcUaClientOptions { Reconnect = new ReconnectOptions { Enabled = false } });
        var lost = new TaskCompletionSource<ConnectionStateChange>();
        client.StateChanged += (_, e) =>
        {
            if (e.State == ConnectionState.Disconnected)
            {
                lost.TrySetResult(e);
            }
        };
        await client.ConnectAsync(own.Url, TestContext.Current.CancellationToken);
        await own.DisposeAsync();
        var change = await lost.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
        Assert.True(change.Reason.IsBad, change.Reason.ToString());
        var ex = await Assert.ThrowsAsync<OpcUaException>(() => client.ReadAsync(new NodeId(2258), TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.BadNotConnected, ex.StatusCode);
    }

    [Fact]
    public async Task NativeLogLinesReachTheLogger()
    {
        var logs = new ListLoggerFactory();
        await using (var client = await ConnectAsync(new OpcUaClientOptions { LoggerFactory = logs, NativeLogLevel = LogLevel.Information }))
        {
            await client.ReadAsync(new NodeId(2258), TestContext.Current.CancellationToken);
        }
        Assert.Contains(logs.Lines, l => l.Message.Contains("[client/", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, l => l.Message.StartsWith("OPC UA client Connected", StringComparison.Ordinal));
    }

    /// <summary>A different value of the same type and shape.</summary>
    private static Variant Changed(Variant v)
    {
        object? Bump(object? x) => x switch
        {
            bool b => !b,
            sbyte n => (sbyte)(n + 1),
            byte n => (byte)(n - 1),
            short n => (short)(n + 1),
            ushort n => (ushort)(n - 1),
            int n => n + 1,
            uint n => n - 1,
            long n => n + 1,
            ulong n => n - 1,
            float n => n * 2,
            double n => n + 1,
            string s => s + "!",
            DateTime t => t.AddHours(1),
            Guid g => Guid.Parse("11111111-2222-3333-4444-555555555555") == g ? Guid.Empty : Guid.Parse("11111111-2222-3333-4444-555555555555"),
            byte[] b => b.Append((byte)9).ToArray(),
            XmlElement x2 => new XmlElement("<changed/>"),
            NodeId id => new NodeId("Changed", 1),
            ExpandedNodeId e => new ExpandedNodeId(new NodeId(43), "urn:changed"),
            StatusCode s => s == StatusCodes.Good ? StatusCodes.BadInternalError : StatusCodes.Good,
            QualifiedName q => new QualifiedName(1, "Changed"),
            LocalizedText l => new LocalizedText("en", "Changed"),
            _ => x,
        };
        if (!v.IsArray)
        {
            return Variant.Scalar(v.Type, Bump(v.Value));
        }
        var source = (Array)v.Value!;
        var copy = Array.CreateInstance(source.GetType().GetElementType()!, source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            copy.SetValue(Bump(source.GetValue(i)), i);
        }
        return v.IsMatrix ? Variant.Matrix(v.Type, copy, [.. v.ArrayDimensions!]) : Variant.Array(v.Type, copy);
    }
}
