using System.Diagnostics;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.IntegrationTests;

/// <summary>
/// Phase 2 exit criteria: the connection breaks while a subscription is running, and the client
/// must reconnect within a defined time and lose no notification the server could still deliver.
/// </summary>
/// <remarks>
/// <para>
/// Each test runs a producer that writes 1000001, 1000002, ... to one variable on the server every 20 ms,
/// and a consumer that records every value the subscription delivers. After the outage the
/// producer stops, the consumer waits for the last value, and the recorded values must contain
/// every number in between, in order (a value may repeat: after a transfer the server resends the
/// current value).
/// </para>
/// <para>
/// Security: TransferSubscriptions, which is what makes an outage loss-free, is only allowed by the
/// server when it can tell that the new session belongs to the same user or application. For an
/// anonymous user OPC UA requires a secure channel (Part 4, 5.13.7), and open62541 enforces it.
/// The loss-free tests therefore run on SignAndEncrypt, as a plant connection should;
/// <see cref="WithoutSecurityAnonymousSubscriptionsAreRecreated"/> shows the fallback.
/// </para>
/// <para>
/// Defined times: the client reconnects within <see cref="ReconnectBound"/> of the network or
/// server coming back. With the reconnect settings used here (first retry after 200 ms, at most
/// 1 s between attempts) that leaves plenty of margin on a loaded CI machine.
/// </para>
/// </remarks>
[Trait("Category", "Chaos")]
public sealed class ChaosTests
{
    private static readonly TimeSpan ReconnectBound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private const string ServerUri = "urn:cictna:test:chaos-server";
    private const string ClientUri = "urn:cictna:test:chaos-client";
    private const string User = "operator";
    private const string Password = "Pump-01!";
    private static readonly ApplicationCertificate ServerCertificate = ApplicationCertificate.CreateSelfSigned(ServerUri, "Cictna Chaos Server", dnsName: "localhost");
    private static readonly ApplicationCertificate ClientCertificate = ApplicationCertificate.CreateSelfSigned(ClientUri, "Cictna Chaos Client");

    /// <summary>A test server with a certificate (secure endpoints), anonymous and user name logins.</summary>
    private static async Task<TestServer> StartSecureServerAsync(int? port = null)
    {
        var server = new TestServer
        {
            Port = port,
            Options = p => new OpcUaServerOptions
            {
                Port = p,
                ApplicationUri = ServerUri,
                Certificate = ServerCertificate,
                AllowAnonymous = true,
                User = new UserIdentity.UserNameIdentity(User, Password),
                MaxMonitoredItemQueueSize = 10_000, // 200 s of the producer's 50 values per second
            },
        };
        await server.InitializeAsync();
        return server;
    }

    /// <summary>SignAndEncrypt with our certificate; anonymous unless an identity is given.</summary>
    private static OpcUaClientOptions SecureOptions(TestServer server, UserIdentity? identity = null) => ClientOptions(server) with
    {
        ApplicationUri = ClientUri,
        Certificate = ClientCertificate,
        SecurityMode = MessageSecurityMode.SignAndEncrypt,
        SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
        AcceptAnyServerCertificateForDevelopment = true,
        Identity = identity ?? UserIdentity.Anonymous,
    };

    private static OpcUaClientOptions ClientOptions(TestServer server) => new()
    {
        LoggerFactory = server.Logs,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        RequestTimeout = TimeSpan.FromSeconds(3),
        Reconnect = new ReconnectOptions { InitialDelay = TimeSpan.FromMilliseconds(200), MaxDelay = TimeSpan.FromSeconds(1) },
    };

    // 100 ms publishing, keep-alive after 5 quiet intervals (0.5 s), lifetime 30 s: long enough
    // for the subscription to wait on the server through the outages below.
    private static readonly SubscriptionOptions Options = new()
    {
        PublishingInterval = TimeSpan.FromMilliseconds(100),
        KeepAliveCount = 5,
        LifetimeCount = 300,
    };

    [Fact]
    public async Task NetworkCutMidSubscriptionLosesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartSecureServerAsync();
        await using var proxy = new TcpProxy(server.ActualPort);
        await using var client = await OpcUaClient.CreateAsync(SecureOptions(server));
        var states = new StateRecorder(client);
        await client.ConnectAsync(proxy.Url, ct);
        await using var subscription = await client.CreateSubscriptionAsync(Options, ct);
        var events = new EventRecorder(subscription);
        var node = server.ManyTag(10);
        await subscription.AddItemAsync(new MonitoredItemRequest(node) { SamplingInterval = TimeSpan.Zero, QueueSize = 10_000 }, ct);
        await using var run = new ProducerConsumer(server, subscription, node);

        await Task.Delay(1000, ct);
        proxy.SeverAll(); // every connection dies at once
        var cut = Stopwatch.StartNew();
        await states.WaitForAsync(ConnectionState.Reconnecting, ct);
        await states.WaitForAsync(ConnectionState.Connected, ct, after: ConnectionState.Reconnecting);
        Assert.True(cut.Elapsed < ReconnectBound, $"reconnected after {cut.Elapsed}");
        await Task.Delay(1000, ct);

        var values = await run.StopAndCollectAsync(ct);
        AssertContiguous(values, ProducerConsumer.FirstWritten, run.LastWritten,
            $"events {string.Join(",", events.Kinds)}, lost messages {subscription.LostNotificationMessages}, logs: {string.Join(" | ", server.Logs.Lines.Select(l => l.Message).Where(m => m.Contains("ubscription") || m.Contains("Transfer") || m.Contains("econnect") || m.Contains("lost") || m.Contains("Publish")).Take(40))}");
        Assert.Contains(events.Kinds, k => k == SubscriptionEventKind.Transferred);
        Assert.Equal(0, subscription.LostNotificationMessages);
    }

    [Fact]
    public async Task ASilentNetworkDropIsCaughtByTheKeepAliveWatchdog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartSecureServerAsync();
        await using var proxy = new TcpProxy(server.ActualPort);
        await using var client = await OpcUaClient.CreateAsync(SecureOptions(server, UserIdentity.UserName(User, Password)));
        var states = new StateRecorder(client);
        await client.ConnectAsync(proxy.Url, ct);
        await using var subscription = await client.CreateSubscriptionAsync(Options, ct);
        var events = new EventRecorder(subscription);
        var node = server.ManyTag(11);
        var item = await subscription.AddItemAsync(new MonitoredItemRequest(node) { SamplingInterval = TimeSpan.Zero, QueueSize = 10_000 }, ct);
        Assert.Equal(10_000u, item.RevisedQueueSize); // the server keeps everything the outage produces
        await using var run = new ProducerConsumer(server, subscription, node);

        await Task.Delay(1000, ct);
        proxy.Blackhole(true); // no error anywhere: packets just vanish
        await states.WaitForAsync(ConnectionState.Reconnecting, ct);
        Assert.Contains(events.Kinds, k => k == SubscriptionEventKind.KeepAliveMissed);
        await Task.Delay(2000, ct); // stays broken a little longer; reconnect attempts time out
        proxy.Blackhole(false);
        var restored = Stopwatch.StartNew();
        await states.WaitForAsync(ConnectionState.Connected, ct, after: ConnectionState.Reconnecting);
        Assert.True(restored.Elapsed < ReconnectBound, $"reconnected {restored.Elapsed} after the network came back");
        await Task.Delay(1000, ct);

        var values = await run.StopAndCollectAsync(ct);
        AssertContiguous(values, ProducerConsumer.FirstWritten, run.LastWritten,
            $"events {string.Join(",", events.Kinds)}, lost messages {subscription.LostNotificationMessages}, logs: {string.Join(" | ", server.Logs.Lines.Select(l => l.Message).Where(m => m.Contains("ubscription") || m.Contains("Transfer") || m.Contains("econnect") || m.Contains("lost") || m.Contains("Publish")).Take(40))}");
        // The old session was left alone (not closed), so the server still had the subscription.
        Assert.Contains(events.Kinds, k => k == SubscriptionEventKind.Transferred);
        Assert.Equal(0, subscription.LostNotificationMessages);
    }

    [Fact]
    public async Task AServerRestartRecreatesTheSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = TestServer.FreePort();
        var server = new TestServer { Port = port };
        await server.InitializeAsync();
        await using var client = await OpcUaClient.CreateAsync(ClientOptions(server));
        var states = new StateRecorder(client);
        await client.ConnectAsync(server.Url, ct);
        var namespaces = client.NamespaceUris.ToArray();
        await using var subscription = await client.CreateSubscriptionAsync(Options, ct);
        var events = new EventRecorder(subscription);
        var node = server.ManyTag(12);
        var item = await subscription.AddItemAsync(new MonitoredItemRequest(node), ct);
        await SubscriptionTests.DrainInitialAsync(subscription, 1, ct);
        var oldServerId = subscription.Id;

        await server.DisposeAsync(); // the server process dies: sessions and subscriptions are gone
        await states.WaitForAsync(ConnectionState.Reconnecting, ct);
        await Task.Delay(1500, ct);
        await using var rebooted = new TestServer { Port = port };
        await rebooted.InitializeAsync();
        var back = Stopwatch.StartNew();
        await states.WaitForAsync(ConnectionState.Connected, ct, after: ConnectionState.Reconnecting);
        Assert.True(back.Elapsed < ReconnectBound, $"reconnected {back.Elapsed} after the server came back");

        // Transfer is impossible (new server process), so the subscription was created again with
        // the same item objects; the item reports its current value again.
        await events.WaitForAsync(SubscriptionEventKind.Recreated, ct);
        var first = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        Assert.Same(item, first.Item);
        Assert.Equal(12, first.Value.Value.GetValue<int>());
        await rebooted.Server.WriteValueAsync(node, new DataValue(1212));
        var next = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        Assert.Equal(1212, next.Value.Value.GetValue<int>());
        Assert.True(item.Status.IsGood);
        Assert.Equal(namespaces, client.NamespaceUris); // read again from the new server
        _ = oldServerId; // the new id may or may not equal the old one; nothing to assert
    }

    [Fact]
    public async Task WithoutSecurityAnonymousSubscriptionsAreRecreated()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await SubscriptionTests.StartServerAsync();
        await using var proxy = new TcpProxy(server.ActualPort);
        await using var client = await OpcUaClient.CreateAsync(ClientOptions(server)); // None, anonymous
        var states = new StateRecorder(client);
        await client.ConnectAsync(proxy.Url, ct);
        await using var subscription = await client.CreateSubscriptionAsync(Options, ct);
        var events = new EventRecorder(subscription);
        var item = await subscription.AddItemAsync(new MonitoredItemRequest(server.ManyTag(13)), ct);
        await SubscriptionTests.DrainInitialAsync(subscription, 1, ct);

        proxy.SeverAll();
        await states.WaitForAsync(ConnectionState.Connected, ct, after: ConnectionState.Reconnecting);
        // The server refuses the transfer (BadUserAccessDenied: anonymous without a secure channel
        // could be anyone), so the client recreates the subscription and the item reports again.
        await events.WaitForAsync(SubscriptionEventKind.Recreated, ct);
        Assert.DoesNotContain(SubscriptionEventKind.Transferred, events.Kinds);
        var again = await subscription.DataChanges.ReadAsync(ct).AsTask().WaitAsync(Generous, ct);
        Assert.Same(item, again.Item);
        Assert.Equal(13, again.Value.Value.GetValue<int>());
    }

    [Fact]
    public async Task DisconnectStopsReconnecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await SubscriptionTests.StartServerAsync();
        await using var proxy = new TcpProxy(server.ActualPort);
        await using var client = await OpcUaClient.CreateAsync(ClientOptions(server));
        var states = new StateRecorder(client);
        await client.ConnectAsync(proxy.Url, ct);
        proxy.Blackhole(true);
        proxy.SeverAll();
        await states.WaitForAsync(ConnectionState.Reconnecting, ct);
        await client.DisconnectAsync(ct);
        Assert.Equal(ConnectionState.Disconnected, client.State);
        var accepted = proxy.AcceptedConnections;
        await Task.Delay(3000, ct); // longer than the reconnect delay
        Assert.Equal(accepted, proxy.AcceptedConnections);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task CallsFailFastWhileReconnectingAndWorkAfterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await SubscriptionTests.StartServerAsync();
        await using var proxy = new TcpProxy(server.ActualPort);
        await using var client = await OpcUaClient.CreateAsync(ClientOptions(server));
        var states = new StateRecorder(client);
        await client.ConnectAsync(proxy.Url, ct);
        proxy.Blackhole(true);
        proxy.SeverAll();
        await states.WaitForAsync(ConnectionState.Reconnecting, ct);
        var ex = await Assert.ThrowsAsync<OpcUaException>(() => client.ReadAsync(server.ManyTag(1), ct));
        Assert.Equal(StatusCodes.BadNotConnected, ex.StatusCode);
        proxy.Blackhole(false);
        await states.WaitForAsync(ConnectionState.Connected, ct, after: ConnectionState.Reconnecting);
        Assert.Equal(1, (await client.ReadAsync(server.ManyTag(1), ct)).Value.GetValue<int>());
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Every number from first to last appears, in non-decreasing order.</summary>
    private static void AssertContiguous(IReadOnlyList<int> values, int first, int last, string context = "")
    {
        Assert.True(last > first + 50, $"too few values written ({first}..{last})");
        for (var i = 1; i < values.Count; i++)
        {
            Assert.True(values[i] >= values[i - 1], $"out of order at {i}: {values[i - 1]} then {values[i]}");
        }
        var missing = Enumerable.Range(first, last - first + 1).Except(values).ToArray();
        Assert.True(missing.Length == 0, $"{missing.Length} values lost, e.g. {string.Join(", ", missing.Take(10))}; {context}");
    }

    /// <summary>Writes 1000001, 1000002, ... to a variable every 20 ms and records what the subscription delivers.</summary>
    private sealed class ProducerConsumer : IAsyncDisposable
    {
        private readonly TestServer _server;
        private readonly NodeId _node;
        private readonly OpcUaSubscription _subscription;
        private readonly CancellationTokenSource _stopProducer = new();
        private readonly CancellationTokenSource _stopConsumer = new();
        private readonly List<int> _values = [];
        private readonly Task _producer;
        private readonly Task _consumer;
        private int _written = FirstWritten - 1;

        public ProducerConsumer(TestServer server, OpcUaSubscription subscription, NodeId node)
        {
            _server = server;
            _subscription = subscription;
            _node = node;
            _consumer = Task.Run(ConsumeAsync);
            _producer = Task.Run(ProduceAsync);
        }

        public const int FirstWritten = 1_000_001; // far above every initial value of the test server

        public int LastWritten => Volatile.Read(ref _written);

        private async Task ProduceAsync()
        {
            while (!_stopProducer.IsCancellationRequested)
            {
                var next = _written + 1;
                await _server.Server.WriteValueAsync(_node, new DataValue(next));
                Volatile.Write(ref _written, next);
                await Task.Delay(20, CancellationToken.None);
            }
        }

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var change in _subscription.DataChanges.ReadAllAsync(_stopConsumer.Token))
                {
                    var v = change.Value.Value.GetValue<int>();
                    if (v < FirstWritten)
                    {
                        continue; // the variable's initial value before the producer started
                    }
                    lock (_values)
                    {
                        _values.Add(v);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
        }

        public async Task<IReadOnlyList<int>> StopAndCollectAsync(CancellationToken ct)
        {
            await _stopProducer.CancelAsync();
            await _producer;
            var last = LastWritten;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < Generous)
            {
                lock (_values)
                {
                    if (_values.Count > 0 && _values[^1] >= last)
                    {
                        return _values.ToArray();
                    }
                }
                await Task.Delay(50, ct);
            }
            lock (_values)
            {
                Assert.Fail($"last value {last} never arrived; got {_values.Count} values, last {(_values.Count > 0 ? _values[^1] : -1)}");
                return [];
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopProducer.CancelAsync();
            await _stopConsumer.CancelAsync();
            await _producer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await _consumer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>Records connection states in order and waits for one.</summary>
    private sealed class StateRecorder
    {
        private readonly List<ConnectionState> _states = [];

        public StateRecorder(OpcUaClient client) => client.StateChanged += (_, e) =>
        {
            lock (_states)
            {
                _states.Add(e.State);
            }
        };

        public async Task WaitForAsync(ConnectionState state, CancellationToken ct, ConnectionState? after = null)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < Generous)
            {
                lock (_states)
                {
                    var start = after is { } a ? _states.LastIndexOf(a) : 0;
                    if (start >= 0 && _states.Skip(start).Contains(state))
                    {
                        return;
                    }
                }
                await Task.Delay(25, ct);
            }
            lock (_states)
            {
                Assert.Fail($"state {state} not reached; states: {string.Join(", ", _states)}");
            }
        }
    }

    /// <summary>Records subscription events.</summary>
    private sealed class EventRecorder
    {
        private readonly List<SubscriptionEventKind> _kinds = [];

        public EventRecorder(OpcUaSubscription subscription) => subscription.EventOccurred += (_, e) =>
        {
            lock (_kinds)
            {
                _kinds.Add(e.Kind);
            }
        };

        public SubscriptionEventKind[] Kinds
        {
            get
            {
                lock (_kinds)
                {
                    return _kinds.ToArray();
                }
            }
        }

        public async Task WaitForAsync(SubscriptionEventKind kind, CancellationToken ct)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < Generous && !Kinds.Contains(kind))
            {
                await Task.Delay(25, ct);
            }
            Assert.Contains(kind, Kinds);
        }
    }
}
