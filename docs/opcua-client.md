# OPC UA client guide

How to use `Cictna.Industrial.OpcUa` (Phase 1: connect, read, write, browse, discover, security).
Every public type and member also has XML documentation with the details.

## Connect and read

```csharp
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa;

await using var client = await OpcUaClient.CreateAsync(new OpcUaClientOptions
{
    ApplicationName = "Plant Data Logger",
});
await client.ConnectAsync("opc.tcp://plc01:4840");

DataValue speed = await client.ReadAsync(NodeId.Parse("ns=2;s=Pump01.Speed"));
if (speed.Status.IsGood)
{
    double rpm = speed.Value.GetValue<double>();
    DateTime? when = speed.SourceTimestamp;
}
```

Every value comes with its quality (`Status`) and timestamps. Check the status before using the
value: a sensor fault shows up as a Bad or Uncertain status, not as an exception.

## Read and write many tags

```csharp
var tags = new[] { "Pump01.Speed", "Pump01.Current", "Tank02.Level" }
    .Select(t => new ReadValueId(new NodeId(t, 2)))
    .ToArray();
IReadOnlyList<DataValue> values = await client.ReadAsync(tags);   // same order as tags

IReadOnlyList<StatusCode> results = await client.WriteAsync(
[
    new WriteValue(new NodeId("Pump01.Setpoint", 2), new DataValue(1450.0)),
    new WriteValue(new NodeId("Valve03.Open", 2), new DataValue(true)),
]);
```

Batches of any size are split to the server's limits (`client.Limits`, read at connect). If a server
advertises a limit it does not really accept, the client halves the request size and retries.

Writes must use the variable's exact data type: writing a `Double` to a `Float` variable returns
`BadTypeMismatch`. Read the `DataType` attribute when in doubt.

## Browse

```csharp
BrowseResult objects = await client.BrowseAsync(WellKnownNodes.ObjectsFolder);
foreach (var r in objects.References)
{
    Console.WriteLine($"{r.BrowseName} {r.NodeClass} {r.NodeId}");
}
```

All references are returned: continuation points are followed automatically (and released if the
call is cancelled).

## Errors, timeouts, cancellation

- Per-item problems are results: `BadNodeIdUnknown` in a `DataValue`, `BadTypeMismatch` from a write.
- `OpcUaException` (with `StatusCode`) means the whole call failed: not connected, timeout,
  connection lost, request rejected, no matching endpoint.
- Every call has a timeout (`RequestTimeout`, default 10 s; `ConnectTimeout`, 15 s) and takes a
  `CancellationToken`.

## Connection state

```csharp
client.StateChanged += (_, change) =>
    logger.LogInformation("OPC UA {State} ({Reason})", change.State, change.Reason.Name);
```

States: `Disconnected`, `Connecting`, `Connected`, `Disconnecting`, and `Reconnecting` after an
established connection was lost.

### Automatic reconnect

When a connection is lost (the server closes it, the network breaks, or subscriptions stop
receiving keep-alives), the client reconnects by itself. Each attempt uses a fresh native client,
so nothing from the old connection (such as the server's namespace table) leaks into the new one.
Attempts are spaced with exponential backoff and random jitter:

```csharp
var options = new OpcUaClientOptions
{
    Reconnect = new ReconnectOptions
    {
        InitialDelay = TimeSpan.FromSeconds(1),  // first attempt
        MaxDelay = TimeSpan.FromSeconds(30),     // the wait never grows beyond this
        Multiplier = 2,                          // 1 s, 2 s, 4 s, 8 s ... 30 s
        Jitter = 0.2,                            // plus or minus 20 %, so many clients do not retry in step
    },
};
```

While `Reconnecting`, calls fail fast with `BadNotConnected`; retry them or wait for `Connected`.
`DisconnectAsync` stops reconnecting. Set `Reconnect = new ReconnectOptions { Enabled = false }` to
handle reconnection yourself. A first `ConnectAsync` that fails is not retried: that is usually a
configuration problem, not an outage.

## Subscriptions

The server watches values and sends changes; nothing is polled.

```csharp
await using var subscription = await client.CreateSubscriptionAsync(new SubscriptionOptions
{
    PublishingInterval = TimeSpan.FromMilliseconds(500), // how often changes are sent
    KeepAliveCount = 10,                                 // an empty message after 10 quiet intervals
    LifetimeCount = 120,                                 // the server keeps it 60 s without a client
});

var items = await subscription.AddItemsAsync(
[
    new MonitoredItemRequest(NodeId.Parse("ns=2;s=Pump01.Speed")) { Tag = "pump01.speed" },
    new MonitoredItemRequest(NodeId.Parse("ns=2;s=Tank02.Level"))
    {
        SamplingInterval = TimeSpan.FromMilliseconds(100),
        QueueSize = 100,                       // keep up to 100 changes between publishes
        DeadbandType = DeadbandType.Absolute,  // ignore changes smaller than 0.5 (server side)
        DeadbandValue = 0.5,
    },
]);
// Items the server refuses (unknown node...) come back with a Bad Status; the call does not throw.

await foreach (var change in subscription.DataChanges.ReadAllAsync(stoppingToken))
{
    // In the order the server sent them. change.Item.Tag is what you put in the request.
    Console.WriteLine($"{change.Item.Tag} = {change.Value.Value} ({change.Value.Status})");
}
```

Every item first reports its current value. Changes go through a bounded buffer
(`SubscriptionOptions.BufferCapacity`, 10 000 by default): if your code reads slower than changes
arrive, the client stops asking the server for more until it catches up, so values wait on the
server instead of being dropped in the client.

### What happens during an outage

The subscription object survives reconnects. After a new session the client:

1. asks the server to move the subscription to the new session (TransferSubscriptions);
2. fetches the notification messages it missed (Republish), in order;
3. carries on. Nothing is lost that the server still had.

If the server cannot transfer it (it restarted, the subscription expired, or the server refuses),
the client creates the subscription again with the same items. Changes during the outage are then
gone, and each item reports its current value again. `subscription.EventOccurred` says which
happened (`Transferred`, `Recreated`, `NotificationsLost`, `KeepAliveMissed`...).

Two things decide how much an outage can cost:

- **Security.** For an anonymous user, OPC UA allows a transfer only over a secure channel
  (Sign or SignAndEncrypt): over SecurityPolicy None anyone could claim the subscription.
  open62541 enforces this, so with None and anonymous every outage means a recreate. Use
  SignAndEncrypt, or a user name, for loss-free reconnects.
- **The server's queue.** While no client is connected, each item keeps at most its queue size of
  changes (`QueueSize`, capped by the server: open62541 allows 100 by default,
  `OpcUaServerOptions.MaxMonitoredItemQueueSize` raises it for our server). Older changes are
  discarded and the next value carries the Overflow bit. And the subscription itself lives
  `LifetimeCount` publishing intervals without a client.

## Triggers

`Cictna.Industrial.Triggers` decides when a stream of values is worth acting on. It works on
`DataValue`s from any source (OPC UA now, Modbus later):

```csharp
var overheat = new ThresholdTrigger(ThresholdComparison.Above, 80)
{
    Name = "Pump 01 overheating", Hysteresis = 2, NotifyOnReturn = true,
}.CreateEvaluator();                                   // one evaluator per tag

await foreach (var change in subscription.DataChanges.ReadAllAsync(stoppingToken))
{
    if (overheat.OnValue(change.Value) is { } e)
        logger.LogWarning("{Trigger}: {Reason} at {Time}", e.Name, e.Reason, e.Time);
}
```

The nine kinds: `AnyChangeTrigger`, `ThresholdTrigger` (edge or level, hysteresis),
`DeadbandTrigger` (absolute or percent), `RateOfChangeTrigger`, `DirectionTrigger`,
`ValueChangeTrigger` (optionally one transition, such as state 3 to 5), `BitTrigger` (one bit of a
status word), `TimeBasedTrigger` (every interval, optionally on round clock times; drive it with
`OnTick`) and `ConnectionTrigger` (drive it with `OnConnectionChanged` from `client.StateChanged`).
Values with a Bad status are ignored by default, so a communication error does not look like a
real change.

## Security

Plant connections should use SignAndEncrypt with certificates checked on both sides. That takes a
certificate store on each side and one decision per peer: trust it or not.

### The certificate store

```csharp
var store = new CertificateStore(@"C:\ProgramData\PlantLogger\pki");   // or /var/lib/plant-logger/pki

await using var client = await OpcUaClient.CreateAsync(new OpcUaClientOptions
{
    ApplicationUri = "urn:plant-logger:client",
    ApplicationName = "Plant Data Logger",
    SecurityMode = MessageSecurityMode.SignAndEncrypt,
    SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
    CertificateStore = store,           // own certificate and trust list
    Identity = UserIdentity.UserName("operator", password),
});
```

The store is a folder in the layout OPC UA uses (Part 12):

```
pki/
  own/certs        this application's certificate      own/private   its key (owner only)
  own/old          certificates replaced at renewal
  trusted/certs    trusted peers and trusted CAs       trusted/crl   their revocation lists
  issuer/certs     CAs needed for chains, not trusted  issuer/crl    their revocation lists
  rejected/certs   refused peers, waiting for a decision
```

- **Own certificate.** Created on first use (self-signed, two years) and renewed 30 days before it
  expires; the old one moves to own/old. `OwnCertificate` changes that, for example to have it
  signed by a plant CA. `Certificate` can still be given directly instead.
- **Trust.** The client reads the trust list again for every connection, so a change applies from
  the next attempt on, also during automatic reconnects.

### Trusting a server the first time

The first connection to a new server fails, on purpose:

```
OpcUaException BadCertificateUntrusted: The server certificate CN=Line 3 PLC Gateway
[3F2A...C1] was refused (BadCertificateUntrusted). It was saved to ...\pki\rejected\certs; to trust
this server, move it to ...\pki\trusted\certs (or call CertificateStore.TrustRejected).
```

Check the thumbprint with whoever runs the server (it is shown in the server's own tools), then
move the file, or `store.TrustRejected("3F2A...C1")`. The server does the same for clients: a
Cictna server saves refused client certificates in its own rejected/certs, and picks up a file
moved to trusted/certs within a second, without a restart. Other servers (Prosys, PLCs) have
their own screen for this.

The client checks the server certificate itself before connecting, with exactly the rules
open62541 uses in the handshake. That is how the error can say which side refused: when our
check passes and the server then closes the channel, the message says the server refused this
client's certificate and shows its thumbprint.

### What is checked

Every certificate in the chain: signature, trust (a trusted certificate, or one issued by a
trusted CA), validity period (`BadCertificateTimeInvalid` for expired or not yet valid),
revocation, and use (a CA certificate cannot be an application certificate). Expired certificates
are refused even when trusted.

### Using a plant CA

With many devices, trusting every certificate one by one gets tedious, and every renewal of a
self-signed certificate has to be trusted again. A CA solves both: trust the CA once, and every
certificate it signs is trusted, renewed ones included.

```csharp
var ca = ApplicationCertificate.CreateAuthority("Plant 3 CA", "CICTNA RDC");   // keep its key safe
var options = new OpcUaClientOptions
{
    CertificateStore = store,
    OwnCertificate = new OwnCertificateSettings("urn:plant-logger:client", "Plant Data Logger") { Issuer = ca },
};
// On each server: trust ca.Certificate (trusted/certs).
```

A CA in issuer/certs (instead of trusted/certs) only helps build chains: the certificates it
signed are still trusted one by one.

### Revocation lists

Put CRLs in trusted/crl (or issuer/crl): `store.AddRevocationList(ca.CreateRevocationList(revoked, number))`.
Once any CRL is present, open62541 expects one from every CA in a chain and refuses certificates
of a CA without one (`BadCertificateRevocationUnknown`); issue an empty list for a CA that has
revoked nothing. With no CRLs at all, revocation is not checked.

### Development shortcuts

`AcceptAnyServerCertificateForDevelopment` (client) and `AcceptAnyClientCertificateForDevelopment`
(server) skip the checks. They allow a man-in-the-middle, log a warning on every use, and do not
belong on a plant network. Without a store and without these flags, a secure connection is refused
on both sides: secure by default.

### Choosing the endpoint

The client asks the server for its endpoints, picks the one that matches the options (highest
security level first), and connects with exactly that endpoint. The TCP connection always goes to
the URL you passed, even if the server advertises a different host name. If nothing matches, the
exception says what the server offers, for example:

```
No usable endpoint: none matches mode SignAndEncrypt and policy Basic256Sha256.
The server offers None/None (Anonymous); Sign/Basic128Rsa15 (Anonymous,UserName)
```

## Logging and metrics

```csharp
var options = new OpcUaClientOptions
{
    LoggerFactory = loggerFactory,          // Microsoft.Extensions.Logging
    NativeLogLevel = LogLevel.Information,  // open62541's own log lines, default Warning
};
```

Native lines appear as `[client/securechannel] ...` with the open62541 category. Metrics are
published on the meter `Cictna.Industrial.OpcUa`: request count and duration per service and
status, connection state changes, reconnect attempts, publish responses, notifications delivered,
messages republished or lost, keep-alive misses, subscriptions transferred or recreated, and
dropped native log lines. Watch them live with
`dotnet-counters monitor --counters Cictna.Industrial.OpcUa`.

## A server for tests and demos

```csharp
await using var server = await OpcUaServer.StartAsync(new OpcUaServerOptions
{
    Port = 4840,
    CertificateStore = new CertificateStore("pki"),   // server certificate and trusted clients
});
var ns = await server.AddNamespaceAsync("urn:demo:plant");
var pumps = await server.AddFolderAsync(WellKnownNodes.ObjectsFolder, new NodeId("Pumps", ns), "Pumps");
var speed = await server.AddVariableAsync(pumps, new NodeId("Pump01.Speed", ns), "Speed", 0.0);
await server.WriteValueAsync(speed, new DataValue(1450.5, StatusCodes.Good, DateTime.UtcNow));
```

The variable's data type follows its initial value. The PLC simulator (Phase 5) is built on this.

## Health check

```csharp
OpcUaNativeLibrary.EnsureCompatible();                                   // at startup
var check = OpcUaNativeLibrary.RunLoopbackSelfTest(port: 48400, TimeSpan.FromSeconds(5));
// check.Succeeded: the native stack, sockets and encoding work on this machine
```
