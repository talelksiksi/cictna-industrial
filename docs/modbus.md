# Modbus TCP guide

How to use `Cictna.Industrial.Modbus` (Phase 4): a Modbus TCP client and server in pure C#, no
native code. Every public type and member also has XML documentation with the details.

Function codes, both directions: 1 (read coils), 2 (read discrete inputs), 3 (read holding
registers), 4 (read input registers), 5 (write single coil), 6 (write single register),
15 (write multiple coils), 16 (write multiple registers) and 23 (read/write multiple registers).

## Connect and read

```csharp
using Cictna.Industrial.Core;
using Cictna.Industrial.Modbus;

await using var client = new ModbusTcpClient(new ModbusClientOptions
{
    RequestTimeout = TimeSpan.FromSeconds(2),
}, logger);
await client.ConnectAsync("192.168.10.21", 502, cancellationToken);

ModbusResult<ushort[]> registers = await client.ReadHoldingRegistersAsync(unitId: 1, address: 0, count: 10, cancellationToken);
if (registers.IsGood)
{
    ushort first = registers.Value![0];
}
else
{
    logger.LogWarning("Read failed: {Result}", registers); // "BadOutOfRange (Modbus exception 02 IllegalDataAddress)"
}
```

Addresses are the protocol addresses, from 0. Device manuals often count from 1 or use the old
notation: holding register 40001 is address 0, input register 30010 is address 9.

Expected failures never throw. A result carries a `Status` and, when the device sent one, the
Modbus `Exception` code:

| What happened | Status | Exception |
|---|---|---|
| No answer within `RequestTimeout` | BadTimeout | None |
| Not connected (or reconnecting) | BadNotConnected | None |
| Connection lost while waiting | BadConnectionClosed | None |
| The answer does not fit the request (wrong length, count or echo) | BadDecodingError | None |
| 01 Illegal function | BadNotSupported | IllegalFunction |
| 02 Illegal data address | BadOutOfRange | IllegalDataAddress |
| 03 Illegal data value | BadInvalidArgument | IllegalDataValue |
| 05 Acknowledge, 06 Server busy | BadResourceUnavailable | (the code) |
| 0A, 0B Gateway path or target failed | BadNoCommunication | (the code) |
| 04 and anything else | BadDeviceFailure | (the code) |

Only programming errors throw: a count below 1, a range past address 65535, a function code 23
request larger than the protocol allows, and cancellation through the `CancellationToken`.
`ConnectAsync` throws `ModbusException` when the first connection fails.

## Write

```csharp
await client.WriteSingleCoilAsync(1, address: 12, value: true);
await client.WriteSingleRegisterAsync(1, address: 100, value: 1450);
await client.WriteMultipleCoilsAsync(1, address: 20, new[] { true, false, true });
await client.WriteMultipleRegistersAsync(1, address: 200, new ushort[] { 1, 2, 3 });

// Function code 23: write, then read, in one request.
ModbusResult<ushort[]> after = await client.ReadWriteMultipleRegistersAsync(1, readAddress: 200, readCount: 3, writeAddress: 203, new ushort[] { 4 });
```

Some devices only accept function codes 15 and 16; set `AlwaysWriteMultiple = true` for those.

## Large reads and writes

Reads and writes larger than one request allows are split: 2000 bits or 125 registers per read,
1968 bits or 123 registers per write. Devices that accept less get smaller limits:

```csharp
new ModbusClientOptions { MaxRegistersPerRead = 60, MaxRegistersPerWrite = 60 }
```

A split write is not atomic. If a later part fails, the earlier parts are already written; the
result reports the failure.

## Typed values, word and byte order

Modbus itself only knows bits and 16-bit registers. A `ModbusPoint` says where a value lives and
how it is laid out, and the client returns a `DataValue` like every other source in the stack:

```csharp
var flow = new ModbusPoint(ModbusTable.HoldingRegisters, 100, ModbusDataType.Float32)
{
    WordOrder = ModbusWordOrder.LowWordFirst,   // CDAB, common on many controllers
};
DataValue value = await client.ReadValueAsync(1, flow);
ModbusResult written = await client.WriteValueAsync(1, flow, Variant.From(12.5f));
```

| Type | Registers | Variant |
|---|---|---|
| Boolean | a coil or discrete input, or one bit of a register (`BitIndex` 0 to 15, read only) | Boolean |
| Int16, UInt16 | 1 | Int16, UInt16 |
| Int32, UInt32, Float32 | 2 | Int32, UInt32, Float |
| Int64, UInt64, Float64 | 4 | Int64, UInt64, Double |
| String | `StringLength` characters, 2 per register, Latin-1, padded with zero bytes | String |

`ArrayLength` reads consecutive elements as an array. The four layouts of a 32-bit value
(bytes A B C D, most significant first):

| WordOrder | ByteOrder | Registers |
|---|---|---|
| HighWordFirst | HighByteFirst | AB CD (the default) |
| LowWordFirst | HighByteFirst | CD AB |
| HighWordFirst | LowByteFirst | BA DC |
| LowWordFirst | LowByteFirst | DC BA |

Writes convert numbers only when they fit exactly: 7.0 into an Int16 works, 7.5 is
BadTypeMismatch and 70000 is BadOutOfRange. Modbus carries no timestamps, so a value's
`ServerTimestamp` is the time the answer arrived and `SourceTimestamp` is null.

`ModbusDataConverter` does the same conversion on registers you read yourself.

## Unit IDs, gateways

Every request names a unit ID. A device on its own usually answers 1 (or any ID); a gateway to a
serial line forwards the request to the unit with that ID. A gateway that gets no answer from
the unit replies with exception 0B (BadNoCommunication). A unit that does not exist on a plain
device usually stays silent (BadTimeout).

## Timeouts, pipelining, reconnect

- `RequestTimeout` (default 3 s) applies to each request from the moment it is sent.
- `MaxConcurrentRequests` (default 1) sends several requests before the first is answered. The
  answers are matched by transaction ID, so a device that answers out of order is fine. Raise it
  only for devices and gateways that process requests in parallel.
- A late answer after a timeout is recognised by its transaction ID and discarded, never taken
  for the answer to the next request.
- After `TimeoutsBeforeReconnect` timeouts in a row (default 3) the connection is considered dead
  and rebuilt. A cut cable often leaves a TCP connection that looks open; some small devices stop
  answering on a connection after a request they do not understand. TCP keep-alive is also on.
- A lost connection is restored in the background with exponential backoff and jitter
  (`ModbusClientOptions.Reconnect`). Meanwhile requests return BadNotConnected at once and
  `State` is `Reconnecting`. `StateChanged` reports every change, in order.
- `DisconnectAsync` closes the connection and stops reconnecting; waiting requests return
  BadConnectionClosed.

## Function codes without a method

`SendRawAsync` sends any request PDU (function code first) and returns the response PDU, for
example function code 43 (device identification) or 8 (diagnostics). An exception response comes
back as a failed result with its code.

## Server

```csharp
var store = new ModbusDataStore();                       // 65536 of everything, all zero
store.SetInputRegisters(0, [215, 230]);
store.Written += (_, write) => logger.LogInformation("{Table} {Address} written by a client", write.Table, write.Address);

await using var server = await ModbusTcpServer.StartAsync(new ModbusServerOptions
{
    Port = 5020,                                          // 502 needs administrator rights on Linux
    DefaultUnit = store,                                  // answers every unit ID
});
```

- `Units` gives each unit ID its own `IModbusDataStore`; `DefaultUnit` answers the rest. Without
  a default, unknown units get exception 0B, or silence with `UnknownUnit = Ignore`.
- Requests are checked as the specification says, in its order: function code (01), quantity and
  length (03), address range (02), then the store (02 or 04). Other function codes get 01.
- `ModbusDataStore` sizes its tables (addresses past the end give 02) and is thread-safe. Implement
  `IModbusDataStore` for anything else, for example to serve live values; an exception thrown by
  a store becomes exception 04 and is logged.
- `MaxConnections` (default 32), `IdleTimeout` (5 minutes) and `FrameTimeout` (5 s for the rest of
  a frame once it started) protect the server from clients that misbehave. A frame with an
  impossible length ends that connection; other connections are not affected.
- `DisconnectAll` closes every client connection while the server keeps listening, which is
  useful to test a client's reconnect.

## Logs and metrics

Logs go through `Microsoft.Extensions.Logging`: connections, losses and reconnects at Information
or Warning, each request at Debug. Metrics use the meter `Cictna.Industrial.Modbus`
(`dotnet-counters monitor --counters Cictna.Industrial.Modbus`): client requests by function and
status, request duration, state changes and discarded late answers; server requests by function
and exception, open, refused and broken connections.

## Tested against

- Our own server, including frames split into single bytes, several frames in one packet, late,
  duplicated and missing answers, and broken networks (tests/Integration).
- Two independent implementations, in both directions: modbus-serial (ISC) and jsmodbus (MIT),
  run with Node.js from tests/Interop/modbus-peers. What they showed:
  - jsmodbus answers a read past the end of its data with fewer registers than asked, instead of
    exception 02. The client refuses that answer (BadDecodingError) rather than shifting values.
  - jsmodbus stops answering on a connection after a function code it does not know. The client
    rebuilds the connection after `TimeoutsBeforeReconnect` timeouts.
  - modbus-serial's server ignores unit IDs other than its own, as a single device would.
- Mutation fuzzing of both parsers and of the server and client over sockets (tests/Fuzz). A run
  is seeded: a failure prints `CICTNA_FUZZ_SEED=...` to replay it; `CICTNA_FUZZ_SCALE=10` runs ten
  times longer.
