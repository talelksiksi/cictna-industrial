# Changelog

All notable changes are listed here. Versions follow semantic versioning; tags are `vX.Y.Z`.

## [Unreleased]

### Added (Phase 3: security and certificates, closed 20 Nov 2026)

Phase 3 exit criteria met on Windows and Linux (CI run 44708296967, build record 0006): trusted, rejected,
expired and renewed certificates, checked on both sides of the connection.


- `CertificateStore`: the OPC UA Part 12 folder layout on disk (own, trusted, issuer, rejected,
  CRLs). Trust, reject, `TrustRejected`, `Untrust`; at most 100 rejected certificates kept.
- Own certificate from the store: created on first use, renewed before it expires (old one kept
  in own/old), optionally signed by a CA (`OwnCertificateSettings`), private keys readable by the
  owner only.
- `ApplicationCertificate.Create` (self-signed or CA-signed, any validity), `CreateAuthority`,
  `CreateRevocationList`, and `Subject`, `Thumbprint`, `NotBefore`, `NotAfter`, `ApplicationUri`,
  `IsAuthority`, `ExpiresWithin`. `TrustList` with `Verify`, using open62541's rules.
- Client: `CertificateStore` and `OwnCertificate` options; the server certificate is checked
  before connecting, refused ones are saved to rejected/certs with an error that says what to do,
  and a refusal by the server names the client certificate to trust there.
- Server: `CertificateStore`, `OwnCertificate`, `AcceptAnyClientCertificateForDevelopment`;
  client certificates checked against the store's trust list, changes applied while running,
  refused clients saved to rejected/certs; `ReloadTrustListAsync`, `ServerCertificate`.
- Native shim ABI 4 (trust lists, rejected list, certificate check).
- Tests: 12 trust tests (trusted, rejected, expired, not yet valid, renewed, CA chains, issuer
  only, revoked) on both sides of the connection; 11 store tests.
- The private key folder is restricted to its owner on Windows too (no inherited permissions;
  the current account, SYSTEM and Administrators only).

### Changed (Phase 3)

- The server no longer accepts every client certificate by default. Without a certificate store
  and without `AcceptAnyClientCertificateForDevelopment`, clients using Sign or SignAndEncrypt are
  refused. SecurityPolicy None is unchanged.

### Added (Phase 2: subscriptions and resilience, closed 19 Nov 2026)

Phase 2 exit criteria met on Windows and Linux (CI run 44548900001, build record 0005): chaos tests with
the server killed mid-subscription, the network cut, a silent network drop and a server restart;
no lost notifications when the server can transfer; reconnect within 10 s of the network or
server coming back.


- Subscriptions: `OpcUaClient.CreateSubscriptionAsync`, `OpcUaSubscription` with monitored items
  (sampling interval, queue size, deadband, data change trigger, monitoring mode, application tag).
  Changes arrive in order through a bounded channel; a slow reader slows publishing down instead
  of losing values. Items are created and deleted in chunks of the server's
  MaxMonitoredItemsPerCall.
- Publish engine in C#: Publish requests kept in flight, acknowledgements, Republish of sequence
  gaps, keep-alive watchdog, BadTooManyPublishRequests and expired subscriptions handled.
- Automatic reconnect (`OpcUaClientOptions.Reconnect`): fresh native client per connection,
  exponential backoff with jitter, `ConnectionState.Reconnecting`. Subscriptions are transferred to
  the new session (TransferSubscriptions plus Republish of what was missed) or recreated with the
  same items; `OpcUaSubscription.EventOccurred` reports which.
- `Cictna.Industrial.Triggers`: the nine trigger types (any change, threshold, deadband, rate of
  change, direction, value change, bit, time based, connection), protocol independent.
- `OpcUaServerOptions.MaxMonitoredItemQueueSize`.
- Metrics: publish responses, notifications, republished and lost messages, keep-alive misses,
  subscription recoveries, reconnect attempts.
- Native shim ABI 3: `cictna_ua_client_close_channel`, server queue size limit.
- Tests: subscription codecs against open62541; subscriptions on our server and node-opcua; chaos
  tests (network cut, silent drop, server restart, disconnect while reconnecting) with zero lost
  values when the server can transfer; 23 trigger tests.

### Fixed (Phase 2)

- The client switched to the URL a server advertises in FindServers when it differed from the one
  dialled, which breaks connections through NAT, port forwards, Docker port mappings and proxies.
  It now always stays on the given URL.
- The Windows native build could fail after succeeding (a stray `$LASTEXITCODE` from
  `cmake --version | Select-Object -First 1`).
- The Windows CI cache never saved (gzip missing from the runner service's PATH).

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
