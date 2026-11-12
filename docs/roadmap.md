# Roadmap

The plan for the whole stack, with the exit criteria that close each phase. The README shows
current status; this file is the reference for scope. Change it only when a decision changes,
and note the date.

## Goal

A solid, fully owned industrial connectivity stack for CICTNA RDC: OPC UA client and server
layers in C# over open62541, a pure C# Modbus TCP client and server, a configurable PLC simulator
built on those layers, and a store-and-forward buffer for collectors. Usable in paid client
projects, including closed products, without licence problems, and robust enough for live plants.
Every phase closes only when its tests pass.

## Decisions (12 Nov 2026)

1. New codebase. The earlier OpcUaConnector library is a reference only; its good ideas carry over
   (one client interface, the 9 trigger types, mock-first testing).
2. OPC UA protocol core: open62541 (C, MPL-2.0), wrapped from C#. Proven, certified stack,
   continuously fuzzed, client and server in one library, MPL-2.0 allows combination with
   proprietary code. Everything above it is ours.
3. Rejected for the core: writing OPC UA from scratch (no field hours on encryption, certificates
   and vendor quirks); OPC Foundation .NET SDK (GPL 2.0 unless paid membership); commercial SDKs
   whose licence forbids SDK-like products. node-opcua (MIT) is used as an independent interop
   test server, not as a base.
4. Modbus TCP: our own pure C# client and server.
5. Siemens S7: later, not version 1.
6. Delivery is remote-first: remote install, an outbound-only data push option, good logs.
7. .NET 10 LTS (supported to November 2028). Proprietary licence, all rights reserved.
   Platforms: Windows x64, Linux x64, Linux ARM64. Store-and-forward on SQLite (WAL mode).
   Simulator UI minimal at first, always in the cictnardc.net palette.

## Architecture

```
src/
  Cictna.Industrial.Core/          value model: Variant, DataValue, StatusCode, NodeId...;
                                   result types, logging and metrics abstractions
  Cictna.Industrial.OpcUa.Native/  native binaries + P/Invoke into the cictna_ua shim
  Cictna.Industrial.OpcUa/         client (connect, browse, read, write, subscribe, discover)
                                   and server (address space from config)
  Cictna.Industrial.Modbus/        Modbus TCP client and server
  Cictna.Industrial.Triggers/      the 9 trigger types, protocol-independent
  Cictna.Industrial.Buffer/        store-and-forward: durable queue, ordered replay, dedup
  Cictna.Industrial.Simulator/     PLC simulator host: plant config, behaviours, faults, web UI
tests/
  Unit/ Integration/ Interop/ Chaos/ Soak/ Fuzz/
```

Design rules:
- One protocol-agnostic tag model on top (name, type, unit, address per protocol, quality,
  timestamp). It uses the OPC UA built-in type system as the common value model; Modbus values
  map into it. Collectors and the simulator speak this model.
- Every network call: timeout, CancellationToken, structured log entry, metric.
- Native boundary: SafeHandle for every native object, no raw pointers in public API, one worker
  thread per native client or server that owns all native calls, results collected by polling
  (no native-to-managed callbacks). Values cross the boundary as standard OPC UA binary encoding.
- No code, names, protocols or data from any third party or client. Simulator and samples use
  invented plants only.

## Simulator (version 1)

- Plant defined in one config file (YAML or JSON): folders, tags with name, type (all OPC UA
  built-in scalar types and arrays), unit, OPC UA NodeId and/or Modbus address, read/write flag.
- Value behaviours: constant, random walk, sine, ramp, counter, step sequence, formula from other
  tags, writable setpoint.
- Served at the same time over the same tags: OPC UA server and Modbus TCP server. S7 later.
- Fault injection (the reason it exists): added latency, dropped and refused connections,
  bad/uncertain quality, frozen values, timeouts, server "reboot" that resets values and sessions,
  certificate expiry, scripted scenarios with timing.
- Web UI to see and change values live; REST endpoint to drive scenarios from tests.
- Runs as a Docker container and as a plain executable on Windows and Linux.
- Sample plants: a generic MSF desalination unit, an acid dosing skid, a packaging line.

## Phases

Global definition of done for every phase: CI green on Windows and Linux; unit and integration
tests for everything in the phase; interop tests against at least one independent implementation
where relevant; public API documented with examples; changelog entry; build record; version tag.

| Phase | Content | Exit criteria |
|---|---|---|
| 0. Foundation | Repo, solution, CI, pinned open62541 built from source with mbedTLS, native packaging, coding standards, licence, notices | CI builds native and managed on Windows and Linux; smoke test loads the native library and prints its version |
| 1. OPC UA client core | Connect (None, Basic256Sha256 Sign and SignAndEncrypt), endpoint discovery, read/write of all built-in types and arrays, browse with continuation points, batches chunked to server limits, quality on every value | Tests pass against our own test server, node-opcua and Prosys |
| 2. Subscriptions and resilience | Subscriptions and monitored items, keep-alive, automatic reconnect with subscription transfer or recreate, exponential backoff, connection state events, the 9 triggers | Chaos tests: server killed mid-subscription, network cut, server reboot; no lost notifications beyond what the server could not deliver; reconnect within a defined time |
| 3. Security and certificates | Own application certificate creation, trust lists (trusted, rejected, issuer stores), username/password, certificate expiry handling, no auto-accept outside development mode | Tests for trusted, rejected, expired and renewed certificates on both OSes |
| 4. Modbus TCP | Client and server, function codes 1, 2, 3, 4, 5, 6, 15, 16 (and 23 if useful), word/byte order options, exceptions, timeouts, reconnect, unit IDs | Unit tests per function code, interop against a third-party Modbus tool, parser fuzzing without crashes |
| 5. Simulator | As specified above, on the Phase 1 to 4 layers | Integration tests drive scenarios through REST; Docker image builds in CI |
| 6. Store-and-forward and soak | Durable buffer, replay, dedup; reference collector sample (PLC to PostgreSQL) | 24-hour soak with injected faults: zero lost values, stable memory and handle counts |
| 7. Packaging and docs | Versioned NuGet packages, docs, samples, licence notices | A clean machine installs the packages and runs a sample in under 10 minutes |
| Later | Siemens S7 client, OPC UA server features for products, MQTT/Sparkplug publishing, Linux and Windows service hosts | |

The order can change when a real project needs it (for example Modbus before subscriptions if a
client's plant is mostly Modbus). Record such a change here with the date and the reason.
