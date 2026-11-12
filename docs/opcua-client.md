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

Phase 1 does not reconnect by itself: after a lost connection the state is `Disconnected` with the
reason, and `ConnectAsync` can be called again. Automatic reconnection with subscription recovery
is Phase 2.

## Security

```csharp
var certificate = ApplicationCertificate.CreateSelfSigned(
    applicationUri: "urn:plant-logger:client", commonName: "Plant Data Logger", organization: "CICTNA RDC");
// Store certificate.Certificate and certificate.PrivateKey and reuse them: servers trust a
// certificate, not an application name. (Certificate stores arrive in Phase 3.)

await using var client = await OpcUaClient.CreateAsync(new OpcUaClientOptions
{
    ApplicationUri = "urn:plant-logger:client",     // must match the certificate
    Certificate = certificate,
    SecurityMode = MessageSecurityMode.SignAndEncrypt,
    SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
    Identity = UserIdentity.UserName("operator", password),
    AcceptAnyServerCertificateForDevelopment = true,  // until Phase 3 trust lists; never in production
});
```

How connecting works: the client asks the server for its endpoints (`GetEndpointsAsync`), picks the
one that matches the options (highest security level first), and connects with exactly that
endpoint. The TCP connection always goes to the URL you passed, even if the server advertises a
different host name. If nothing matches, the exception says what the server offers, for example:

```
No usable endpoint: none matches mode SignAndEncrypt and policy Basic256Sha256.
The server offers None/None (Anonymous); Sign/Basic128Rsa15 (Anonymous,UserName)
```

Without `AcceptAnyServerCertificateForDevelopment`, untrusted server certificates are rejected.
Trust lists (and checking the server against them) come in Phase 3.

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
status, connection state changes, dropped native log lines. Watch them live with
`dotnet-counters monitor --counters Cictna.Industrial.OpcUa`.

## A server for tests and demos

```csharp
await using var server = await OpcUaServer.StartAsync(new OpcUaServerOptions { Port = 4840 });
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
