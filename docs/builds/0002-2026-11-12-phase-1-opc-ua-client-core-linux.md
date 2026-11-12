# Build 0002: Phase 1 OPC UA client core (Linux)

<!-- commit: 74d270bd5b39e7bd8dc266e8d43d811dc95871b2 -->

| | |
|---|---|
| Result | **PASS** |
| Date (UTC) | 2026-11-12 12:25 |
| Commit | `74d270b` Phase 1 documentation: client guide, PC steps for Prosys, changelog, notices |
| Uncommitted changes | no |
| Built on | Linux x86_64, Ubuntu 24.04.5 LTS |
| .NET SDK | 10.0.115 |
| CMake | 3.28.3 |
| open62541 | v1.5.10 (`93c3bdfc9ea4`) |
| mbedTLS | v3.6.7 (`068ff080b369`) |

## Notes

Phase 1 (OPC UA client core) build, Linux side. Code complete; the phase closes after the Windows
CI run and the Prosys interop run on the PC (docs/pc-steps.md, steps 1 to 5).

- New: Cictna.Industrial.Core (value model), Cictna.Industrial.OpcUa (client, test server, codec),
  native shim ABI 2 (client, server, codec round trip, certificates, log queues).
- Verified here: codec byte for byte against open62541 (differential tests), client against our
  own server over TCP (every built-in type, batching, continuation points, errors, lost server),
  security (Basic256Sha256 Sign/SignAndEncrypt, user login, secure default), node-opcua interop.
- Decisions taken during the phase: the client selects endpoints itself and presets them in
  open62541; untrusted server certificates are rejected by default; open62541's own reconnect is
  off until Phase 2 designs reconnection explicitly (and must use a fresh native client per
  connection because of the namespace mapping, see docs/native-layer.md).
- Known and documented open62541 behaviours: zero matrix dimensions rejected, ExpandedNodeId
  normalisation, client namespace mapping, accept-all default verification (handled).

## Changes (since build 0001 (`1fbde18`))

- `74d270b` Phase 1 documentation: client guide, PC steps for Prosys, changelog, notices
- `9663151` Phase 1e: interop with node-opcua and Prosys, CI runs every test suite
- `c56f799` Phase 1e: secure channels, user login, endpoint selection, secure default
- `ab6d909` Phase 1d: OpcUaClient and OpcUaServer on dedicated native worker threads
- `0a0b6f5` Phase 1b: OPC UA binary codec with differential tests against open62541
- `aa0423c` Phase 1a: core value model (Variant, DataValue, StatusCode, NodeId and friends)
- `8a9f509` Add roadmap: decisions, architecture, simulator spec, phases with exit criteria
- `b90ca70` Build record 0001: Phase 0 foundation

## Steps

| Step | Result | Seconds |
|---|---|---|
| native linux-x64 | PASS | 35 |
| native linux-arm64 | PASS | 58 |
| dotnet build | PASS | 8 |
| tests (unit, integration, interop) | PASS | 14 |
| managed smoke | PASS | 1 |
| native smoke x64 | PASS | 0 |
| native smoke arm64 (qemu) | PASS | 1 |
| pack | PASS | 2 |

Tests: 105 passed, 0 failed, 5 skipped, 110 total.

Managed smoke test output:

```
Runtime: linux-x64, .NET 10.0.15
[PASS] native library loads: open62541 v1.5.10
[PASS] ABI version: 2 (expected 2)
[PASS] encryption backend: mbedtls
[PASS] required features: Encryption, Multithreading, Subscriptions, MethodCalls, Historizing
[PASS] loopback server + client read: port 39365, Good, server time 2026-11-12T12:25:07.8849129+00:00
[PASS] server time is plausible: skew 503 ms
SMOKE OK
```

## Native binaries

| RID | File | Bytes | SHA-256 | Compiler | Built (UTC) |
|---|---|---|---|---|---|
| linux-x64 | `libcictna_ua.so` | 2659160 | `66a6e599a24bd0b32fc5535f8aba1a0919a239421f94c65f0d14d24cbc73fdfd` | cc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-12T12:23:47Z |
| linux-arm64 | `libcictna_ua.so` | 2371944 | `0e47b61e3eb38bbaa35af0e52dae79d08f8106b8aaae3f5256c2cdde66a0abe5` | aarch64-linux-gnu-gcc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-12T12:24:45Z |
| win-x64 | not built here | | | | |

Exported symbols (linux-x64): cictna_ua_abi_version cictna_ua_client_cancel cictna_ua_client_connect cictna_ua_client_delete cictna_ua_client_disconnect cictna_ua_client_get_endpoints cictna_ua_client_iterate cictna_ua_client_new cictna_ua_client_poll cictna_ua_client_poll_log cictna_ua_client_send cictna_ua_client_state cictna_ua_codec_roundtrip cictna_ua_create_certificate cictna_ua_encryption_backend cictna_ua_features cictna_ua_free cictna_ua_selftest_loopback cictna_ua_server_add_namespace cictna_ua_server_add_node cictna_ua_server_delete cictna_ua_server_iterate cictna_ua_server_new cictna_ua_server_poll_log cictna_ua_server_read cictna_ua_server_start cictna_ua_server_stop cictna_ua_server_write cictna_ua_stack_version cictna_ua_status_name

Minimum glibc: GLIBC_2.34

## Platform verification

| Platform | Status |
|---|---|
| linux-x64 | verified here: native build, all test suites, managed and native smoke |
| linux-arm64 | cross-built, native smoke PASS under qemu; managed tests not run on ARM64 hardware |
| win-x64 | **pending**: needs the self-hosted runner (docs/pc-steps.md) |
