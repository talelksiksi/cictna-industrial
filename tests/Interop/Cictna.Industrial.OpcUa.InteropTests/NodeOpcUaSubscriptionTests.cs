using System.Diagnostics;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.IntegrationTests;

namespace Cictna.Industrial.OpcUa.InteropTests;

/// <summary>
/// Subscriptions against node-opcua: publish handling, keep-alives, chunking to the server's
/// MaxMonitoredItemsPerCall (20 in server.js) and TransferSubscriptions after a network cut, all
/// against a server implementation we did not write.
/// </summary>
/// <remarks>
/// node-opcua samples plain variables on a timer: a sampling interval of zero is revised to its
/// minimum, and several writes between two samples report only the last one. The tests that need
/// every value therefore space their writes by twice the revised sampling interval.
/// </remarks>
public sealed class NodeOpcUaSubscriptionTests(NodeOpcUaServer server) : IClassFixture<NodeOpcUaServer>
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private static readonly SubscriptionOptions Fast = new() { PublishingInterval = TimeSpan.FromMilliseconds(100), KeepAliveCount = 5, LifetimeCount = 300 };
    private const string ClientUri = "urn:cictna:test:interop-subscriptions";
    private static readonly ApplicationCertificate ClientCertificate = ApplicationCertificate.CreateSelfSigned(ClientUri, "Cictna Interop Subscriptions");

    private NodeId Tag(int i) => new($"Many.Tag{i:000}", server.Ns);

    /// <summary>Time between writes so that every write is sampled: twice the server's sampling interval.</summary>
    private static TimeSpan Pace(MonitoredItem item) => TimeSpan.FromMilliseconds(Math.Max(40, item.RevisedSamplingInterval.TotalMilliseconds * 2));

    private async Task<OpcUaClient> ConnectAsync(OpcUaClientOptions? options = null, string? url = null)
    {
        server.EnsureAvailable();
        var client = await OpcUaClient.CreateAsync(options);
        await client.ConnectAsync(url ?? server.Url, TestContext.Current.CancellationToken);
        return client;
    }

    [Fact]
    public async Task ItemsAreCreatedInChunksOfTheServerLimitAndAllReport()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync();
        Assert.Equal(20, client.Limits.MaxMonitoredItemsPerCall);
        await using var subscription = await client.CreateSubscriptionAsync(Fast, ct);
        var items = await subscription.AddItemsAsync(Enumerable.Range(0, 50).Select(i => new MonitoredItemRequest(Tag(i)) { Tag = i }).ToArray(), ct);
        Assert.All(items, i => Assert.True(i.Status.IsGood, i.ToString()));
        Assert.Equal(50, items.Select(i => i.ServerId).Distinct().Count());
        var seen = new Dictionary<int, int>();
        while (seen.Count < 50)
        {
            var change = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
            seen[(int)change.Item.Tag!] = change.Value.Value.GetValue<int>();
        }
        Assert.All(seen, kv => Assert.Equal(kv.Key, kv.Value)); // every tag's value is its index
    }

    [Fact]
    public async Task WrittenValuesArriveInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync();
        var node = Tag(240);
        await using var subscription = await client.CreateSubscriptionAsync(Fast, ct);
        var item = await subscription.AddItemAsync(new MonitoredItemRequest(node) { SamplingInterval = TimeSpan.Zero, QueueSize = 100 }, ct);
        var pace = Pace(item);
        Assert.Equal(240, (await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct)).Value.Value.GetValue<int>());
        try
        {
            for (var v = 5000; v < 5020; v++)
            {
                Assert.True((await client.WriteAsync(node, v, ct)).IsGood);
                await Task.Delay(pace, ct);
            }
            var seen = new List<int>();
            while (seen.Count == 0 || seen[^1] != 5019)
            {
                seen.Add((await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct)).Value.Value.GetValue<int>());
            }
            Assert.Equal(Enumerable.Range(5000, 20), seen);
        }
        finally
        {
            await client.WriteAsync(node, 240, CancellationToken.None); // other tests expect value = index
        }
    }

    [Fact]
    public async Task AQuietSubscriptionStaysAlive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync();
        await using var subscription = await client.CreateSubscriptionAsync(Fast with { KeepAliveCount = 3 }, ct);
        var events = 0;
        subscription.EventOccurred += (_, _) => Interlocked.Increment(ref events);
        await subscription.AddItemAsync(new MonitoredItemRequest(Tag(241)), ct);
        await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.Equal(0, Volatile.Read(ref events));
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task ANetworkCutTransfersTheSubscriptionWithoutLoss()
    {
        var ct = TestContext.Current.CancellationToken;
        server.EnsureAvailable();
        await using var proxy = new TcpProxy(new Uri(server.Url).Port, "localhost");
        var options = new OpcUaClientOptions
        {
            ApplicationUri = ClientUri,
            Certificate = ClientCertificate,
            SecurityMode = MessageSecurityMode.SignAndEncrypt,
            SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
            AcceptAnyServerCertificateForDevelopment = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Reconnect = new ReconnectOptions { InitialDelay = TimeSpan.FromMilliseconds(200), MaxDelay = TimeSpan.FromSeconds(1) },
        };
        await using var client = await ConnectAsync(options, proxy.Url);
        await using var writer = await ConnectAsync(); // writes directly, not through the proxy
        var node = Tag(242);
        await using var subscription = await client.CreateSubscriptionAsync(Fast, ct);
        var kinds = new List<SubscriptionEventKind>();
        subscription.EventOccurred += (_, e) =>
        {
            lock (kinds)
            {
                kinds.Add(e.Kind);
            }
        };
        var item = await subscription.AddItemAsync(new MonitoredItemRequest(node) { SamplingInterval = TimeSpan.Zero, QueueSize = 1000 }, ct);
        var pace = Pace(item);
        await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct); // initial

        try
        {
            var values = new List<int>();
            var consumer = Task.Run(async () =>
            {
                await foreach (var c in subscription.DataChanges.ReadAllAsync(ct))
                {
                    lock (values)
                    {
                        values.Add(c.Value.Value.GetValue<int>());
                    }
                }
            }, ct);
            var next = 10_000;
            for (var i = 0; i < 25; i++)
            {
                await writer.WriteAsync(node, next++, ct);
                await Task.Delay(pace, ct);
            }
            proxy.SeverAll();
            for (var i = 0; i < 50; i++) // keep writing through the outage and the reconnect
            {
                await writer.WriteAsync(node, next++, ct);
                await Task.Delay(pace, ct);
            }
            var deadline = Stopwatch.StartNew();
            while (client.State != ConnectionState.Connected && deadline.Elapsed < Generous)
            {
                await Task.Delay(50, ct);
            }
            for (var i = 0; i < 25; i++)
            {
                await writer.WriteAsync(node, next++, ct);
                await Task.Delay(pace, ct);
            }
            var last = next - 1;
            while (deadline.Elapsed < Generous)
            {
                lock (values)
                {
                    if (values.Count > 0 && values[^1] == last)
                    {
                        break;
                    }
                }
                await Task.Delay(50, ct);
            }
            int[] got;
            lock (values)
            {
                got = values.Where(v => v >= 10_000).ToArray();
            }
            var missing = Enumerable.Range(10_000, last - 10_000 + 1).Except(got).ToArray();
            lock (kinds)
            {
                Assert.True(missing.Length == 0, $"{missing.Length} values lost, e.g. {string.Join(", ", missing.Take(10))}; events {string.Join(",", kinds)}");
                Assert.Contains(SubscriptionEventKind.Transferred, kinds);
            }
            Assert.Equal(0, subscription.LostNotificationMessages);
        }
        finally
        {
            await writer.WriteAsync(node, 242, CancellationToken.None);
        }
    }
}
