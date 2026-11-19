# Changelog

All notable changes are listed here. Versions follow semantic versioning; tags are `vX.Y.Z`.

## [Unreleased]

## [0.1.0] - 2026-11-19

First release: Phase 0 (foundation) and Phase 1 (OPC UA client core). Packages:
`Cictna.Industrial.Core`, `Cictna.Industrial.OpcUa.Native` (win-x64, linux-x64, linux-arm64),
`Cictna.Industrial.OpcUa`, published to the private GitHub Packages feed of talelksiksi.

### Added (Phase 1: OPC UA client core, closed 19 Nov 2026)

Phase 1 exit criteria met: tests pass against our own test server, node-opcua (CI, Windows and
Linux) and the Prosys OPC UA Simulation Server (5 of 5 on the Windows PC, including the
Basic256Sha256 secure channel). Build record 0004.


- `Cictna.Industrial.Core`: the common value model (OPC UA built-in types): `Variant` (scalars,
  arrays, matrices), `DataValue` with quality and timestamps, `StatusCode` with the full
  specification table, `NodeId`, `ExpandedNodeId`, `QualifiedName`, `LocalizedText`,
  `ExtensionObject`, `DiagnosticInfo`.
- OPC UA binary codec in C#, checked byte for byte against open62541 (differential tests over
  thousands of random values) and hardened against hostile input (limits, mutation fuzzing).
- `OpcUaClient`: connect with endpoint selection (None, Basic256Sha256 Sign and SignAndEncrypt,
  anonymous or user name), read, write and browse of any size within server limits, browse
  continuation points, GetEndpoints, timeouts and cancellation on every call, connection state
  events, native logs to ILogger, metrics.
- `OpcUaServer`: folders and variables of every built-in type from code, local value updates;
  used as the test server and the base of the Phase 5 simulator.
- `ApplicationCertificate.CreateSelfSigned`, `UserIdentity`, `OpcUaException`.
- Native shim ABI 2: generic asynchronous service call with polled completions, client and server
  objects, codec round trip, certificate creation, bounded native log queues.
- Tests: 110 in total. 105 run everywhere (unit, differential, integration over TCP, security,
  node-opcua interop); 5 Prosys interop tests run where a Prosys server is configured (the PC).

### Fixed (found by the tests)

- Untrusted server certificates were accepted when no trust list was set (open62541 default);
  now rejected unless `AcceptAnyServerCertificateForDevelopment` is set.
- Connecting to a server that advertises another host name (node-opcua, many plant servers)
  failed during discovery; endpoint discovery and selection now always use the given URL.

### Changed (CI)

- The Windows build cache now saves and restores (gzip from Git\usr\bin on PATH for actions/cache);
  the self-hosted runner installs the .NET SDK into its tool cache instead of the service profile.

### Added (documentation)

- `docs/pc-steps.md`: checklist of everything to do on the Windows PC, with done/open status.
- Build records: `scripts/build-and-record.sh` runs the full pipeline and writes a numbered record
  to `docs/builds/`; CI writes a record for every run (job summary and artifact). The native build
  scripts now write `build-info.txt` (versions, compiler, options, SHA-256) next to each binary.
- `docs/native-layer.md`: design of the native layer, version bump and ABI change procedures.
- Context comments throughout the shim, build scripts and C# code explaining why, not only what.

### Added (Phase 0: foundation)

- Native layer: open62541 v1.5.10 and mbedTLS v3.6.7 built from source at pinned commits,
  statically linked with our C shim into one library per runtime
  (`cictna_ua.dll`, `libcictna_ua.so`). Only `cictna_ua_*` symbols are exported.
  Built with encryption (mbedTLS), internal locking (`UA_MULTITHREADING=100`), reduced namespace 0, no PubSub.
- Build scripts for linux-x64, linux-arm64 (cross-compiled) and win-x64 (MSVC, static C runtime).
- `Cictna.Industrial.OpcUa.Native`: P/Invoke bindings, native library resolver for NuGet and
  project-reference layouts, `OpcUaNativeLibrary` (version, features, ABI check, loopback self test).
  The NuGet package carries the binaries under `runtimes/<rid>/native/` and all licence texts.
- Tests: 11 xUnit v3 unit tests, a console smoke test, and a native smoke test for ARM64 under qemu.
- CI workflow (on demand and on tags), Windows self-hosted runner setup script, GitHub Packages publishing.
- Proprietary LICENSE, third-party notices, coding standards.
