using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.IntegrationTests;

/// <summary>
/// Subscriptions against our own open62541 server. Each test starts its own server, because the
/// tests change values on the server side and must not see each other's changes.
/// </summary>
public sealed class SubscriptionTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);
    private static readonly SubscriptionOptions Fast = new() { PublishingInterval = TimeSpan.FromMilliseconds(50), KeepAliveCount = 5 };

    [Fact]
    public async Task InitialValueAndEveryChangeArriveInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(server);
        await using var subscription = await client.CreateSubscriptionAsync(Fast, ct);
        var node = server.ManyTag(1);
        var item = await subscription.AddItemAsync(new MonitoredItemRequest(node) { SamplingInterval = TimeSpan.Zero, QueueSize = 100 }, ct);
        Assert.True(item.Status.IsGood, item.Status.ToString());
        Assert.NotEqual(0u, item.ServerId);

        var first = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        Assert.Same(item, first.Item);
        Assert.Equal(1, first.Value.Value.GetValue<int>());

        for (var v = 100; v < 120; v++)
        {
            await server.Server.WriteValueAsync(node, new DataValue(v));
        }
        var seen = new List<int>();
        while (seen.Count == 0 || seen[^1] != 119)
        {
            var change = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
            seen.Add(change.Value.Value.GetValue<int>());
        }
        // The queue holds every sampled value, so all twenty arrive and in order; sequence numbers
        // never go backwards.
        Assert.Equal(Enumerable.Range(100, 20), seen);
        Assert.Equal(0, subscription.LostNotificationMessages);
    }

    [Fact]
    public async Task AnUnknownNodeFailsOnlyItsOwnItem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(server);
        await using var subscription = await client.CreateSubscriptionAsync(Fast, ct);
        var items = await subscription.AddItemsAsync(
        [
            new MonitoredItemRequest(server.ManyTag(2)) { Tag = "good" },
            new MonitoredItemRequest(new NodeId("DoesNotExist", server.Ns)) { Tag = "bad" },
        ], ct);
        Assert.True(items[0].Status.IsGood);
        Assert.Equal(StatusCodes.BadNodeIdUnknown, items[1].Status);
        Assert.Equal("bad", items[1].Tag);
        var change = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        Assert.Same(items[0], change.Item);
    }

    [Fact]
    public async Task ManyItemsInSeveralSubscriptionsAllReport()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(server);
        await using var a = await client.CreateSubscriptionAsync(Fast, ct);
        await using var b = await client.CreateSubscriptionAsync(Fast with { PublishingInterval = TimeSpan.FromMilliseconds(80) }, ct);
        await a.AddItemsAsync(Enumerable.Range(0, 125).Select(i => new MonitoredItemRequest(server.ManyTag(i))).ToArray(), ct);
        await b.AddItemsAsync(Enumerable.Range(125, 125).Select(i => new MonitoredItemRequest(server.ManyTag(i))).ToArray(), ct);
        var values = new HashSet<int>();
        foreach (var subscription in new[] { a, b })
        {
            while (values.Count < (subscription == a ? 125 : 250))
            {
                var change = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
                values.Add(change.Value.Value.GetValue<int>());
            }
        }
        Assert.Equal(Enumerable.Range(0, 250), values.Order());
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public async Task RemovedItemsStopReporting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(server);
        await using var subscription = await client.CreateSubscriptionAsync(Fast, ct);
        var keep = await subscription.AddItemAsync(new MonitoredItemRequest(server.ManyTag(3)), ct);
        var drop = await subscription.AddItemAsync(new MonitoredItemRequest(server.ManyTag(4)), ct);
        await DrainInitialAsync(subscription, 2, ct);

        await subscription.RemoveItemsAsync([drop], ct);
        Assert.DoesNotContain(drop, subscription.Items);
        await server.Server.WriteValueAsync(server.ManyTag(4), new DataValue(40));
        await server.Server.WriteValueAsync(server.ManyTag(3), new DataValue(30));
        var change = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        Assert.Same(keep, change.Item);
        Assert.Equal(30, change.Value.Value.GetValue<int>());
    }

    [Fact]
    public async Task AQuietSubscriptionStaysAliveOnKeepAlives()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(server);
        var options = new SubscriptionOptions { PublishingInterval = TimeSpan.FromMilliseconds(100), KeepAliveCount = 3 };
        await using var subscription = await client.CreateSubscriptionAsync(options, ct);
        var events = new List<SubscriptionEvent>();
        subscription.EventOccurred += (_, e) =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };
        await subscription.AddItemAsync(new MonitoredItemRequest(server.ManyTag(5)), ct);
        await DrainInitialAsync(subscription, 1, ct);

        // Several keep-alive periods (3 x 100 ms) without any change: only keep-alives arrive,
        // and the watchdog must not think the connection is dead.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.Equal(ConnectionState.Connected, client.State);
        lock (events)
        {
            Assert.Empty(events);
        }
        Assert.Equal(TimeSpan.FromMilliseconds(100), subscription.RevisedPublishingInterval);
    }

    [Fact]
    public async Task DisposingCompletesTheChannelAndDeletesOnTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(server);
        var subscription = await client.CreateSubscriptionAsync(Fast, ct);
        await subscription.AddItemAsync(new MonitoredItemRequest(server.ManyTag(6)), ct);
        Assert.Contains(subscription, client.Subscriptions);
        await subscription.DisposeAsync();
        Assert.DoesNotContain(subscription, client.Subscriptions);
        await subscription.DataChanges.Completion.WaitAsync(Generous, ct);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => subscription.AddItemAsync(new MonitoredItemRequest(server.ManyTag(6)), ct));
    }

    [Fact]
    public async Task ASlowReaderLosesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(server);
        // A buffer of 4 for 60 changes: delivery must wait for the reader instead of dropping.
        await using var subscription = await client.CreateSubscriptionAsync(Fast with { BufferCapacity = 4 }, ct);
        var node = server.ManyTag(7);
        await subscription.AddItemAsync(new MonitoredItemRequest(node) { SamplingInterval = TimeSpan.Zero, QueueSize = 1000 }, ct);
        await DrainInitialAsync(subscription, 1, ct);
        for (var v = 0; v < 60; v++)
        {
            await server.Server.WriteValueAsync(node, new DataValue(1000 + v));
        }
        var seen = new List<int>();
        while (seen.Count < 60)
        {
            var change = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
            seen.Add(change.Value.Value.GetValue<int>());
            await Task.Delay(20, ct); // slow consumer
        }
        Assert.Equal(Enumerable.Range(1000, 60), seen);
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task ADeadbandFiltersSmallChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync();
        var node = await server.Server.AddVariableAsync(WellKnownNodes.ObjectsFolder, new NodeId("Level", server.Ns), "Level", 10.0);
        await using var client = await ConnectAsync(server);
        await using var subscription = await client.CreateSubscriptionAsync(Fast, ct);
        await subscription.AddItemAsync(new MonitoredItemRequest(node)
        {
            SamplingInterval = TimeSpan.Zero,
            QueueSize = 10,
            DeadbandType = DeadbandType.Absolute,
            DeadbandValue = 1.0,
        }, ct);
        await DrainInitialAsync(subscription, 1, ct);
        await server.Server.WriteValueAsync(node, new DataValue(10.4)); // within the deadband
        await Task.Delay(200, ct);
        await server.Server.WriteValueAsync(node, new DataValue(12.0)); // outside
        var change = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        Assert.Equal(12.0, change.Value.Value.GetValue<double>());
    }

    // ---- helpers ------------------------------------------------------------------------------

    internal static async Task<TestServer> StartServerAsync()
    {
        var server = new TestServer();
        await server.InitializeAsync();
        return server;
    }

    private static async Task<OpcUaClient> ConnectAsync(TestServer server)
    {
        var client = await OpcUaClient.CreateAsync(new OpcUaClientOptions { LoggerFactory = server.Logs });
        await client.ConnectAsync(server.Url, TestContext.Current.CancellationToken);
        return client;
    }

    internal static async Task DrainInitialAsync(OpcUaSubscription subscription, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        }
    }
}
