# Build 0005: Phase 2 closed, subscriptions reconnect and triggers

<!-- commit: ea8be34873fa32b58e6c3f6cdb9435df145bac66 -->

| | |
|---|---|
| Result | **PASS** |
| Date (UTC) | 2026-11-19 15:02 |
| Commit | `ea8be34` Phase 2 closed: docs for subscriptions, reconnect and triggers; Prosys subscription test |
| Uncommitted changes | no |
| Built on | Linux x86_64, Ubuntu 24.04.5 LTS |
| .NET SDK | 10.0.115 |
| CMake | 3.28.3 |
| open62541 | v1.5.10 (`93c3bdfc9ea4`) |
| mbedTLS | v3.6.7 (`068ff080b369`) |

## Notes

Phase 2 closed: subscriptions, automatic reconnect and the nine triggers, verified on Windows and Linux.

- Exit criteria (docs/roadmap.md): chaos tests with the server killed mid-subscription, the network
  cut, a silent network drop and a server restart. With a secure channel (as OPC UA requires for
  transferring anonymous subscriptions) no value is lost: TransferSubscriptions plus Republish
  recover everything the server still had. Reconnect happens within 10 s of the network or server
  coming back (asserted). Verified here and on Windows in CI run [44548900001](https://github.com/talelksiksi/cictna-industrial/actions/runs/44548900001).
- Interop: subscriptions, chunking to MaxMonitoredItemsPerCall and a loss-free transfer after a
  network cut also pass against node-opcua, an implementation we did not write.
- Native shim ABI 3: close only the secure channel when a connection is in doubt (closing the
  client would delete its subscriptions on the server), and a configurable monitored item queue
  limit for our server (open62541 caps it at 100 by default).
- Found and fixed: open62541 switched to the URL a server advertises in FindServers, which breaks
  NAT, port forwards, Docker port mappings and proxies; the client now always stays on the given URL.
  The Windows native build could fail after succeeding because of a stray exit code from
  `cmake --version | Select-Object -First 1`.
- Tests: 176 on Windows (171 passed, 5 Prosys skipped), the same suites here. An optional sixth
  Prosys test (subscription) waits for the next Prosys run on the PC (docs/pc-steps.md, step 6).
- Next: Phase 3, security and certificates.

## Changes (since build 0004 (`fab6eef`))

- `ea8be34` Phase 2 closed: docs for subscriptions, reconnect and triggers; Prosys subscription test
- `531d51a` Phase 2: the nine trigger types; Windows build no longer fails on a stray exit code
- `82fe384` Phase 2: subscriptions, automatic reconnect and chaos tests
- `82bd77f` Phase 2: subscription and monitored item messages, checked against open62541
- `f78af9f` Release 0.1.0: Phase 0 and Phase 1
- `7defe47` Build record 0004: Phase 1 closed, OPC UA client core verified

## Steps

| Step | Result | Seconds |
|---|---|---|
| native linux-x64 | PASS | 1 |
| native linux-arm64 | PASS | 20 |
| dotnet build | PASS | 11 |
| tests | PASS | 29 |
| managed smoke | PASS | 1 |
| native smoke x64 | PASS | 1 |
| native smoke arm64 (qemu) | PASS | 1 |
| pack | PASS | 2 |

Tests: 171 passed, 0 failed, 6 skipped, 177 total.

Managed smoke test output:

```
Runtime: linux-x64, .NET 10.0.15
[PASS] native library loads: open62541 v1.5.10
[PASS] ABI version: 3 (expected 3)
[PASS] encryption backend: mbedtls
[PASS] required features: Encryption, Multithreading, Subscriptions, MethodCalls, Historizing
[PASS] loopback server + client read: port 40393, Good, server time 2026-11-19T15:02:26.1281009+00:00
[PASS] server time is plausible: skew 504 ms
SMOKE OK
```

## Native binaries

| RID | File | Bytes | SHA-256 | Compiler | Built (UTC) |
|---|---|---|---|---|---|
| linux-x64 | `libcictna_ua.so` | 2659160 | `3909695b2c62f054205c83e0212f371f876ca2ec7f9822385c510b2761865c34` | cc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-19T15:01:25Z |
| linux-arm64 | `libcictna_ua.so` | 2371944 | `83587d6dbcdae1630fa84699ad0b3c1a4fa2d7a852ca5068ee77ebf94d313c10` | aarch64-linux-gnu-gcc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-19T15:01:45Z |
| win-x64 (CI) | `cictna_ua.dll` | 2014720 | `42182c7377ed8070cdfa56264fa583c1dfae1b22cd11554e749a97c74aaa50e1` | MSVC 19.51.36260.0 | 2026-11-19T14:57:45Z |

Exported symbols (linux-x64): cictna_ua_abi_version cictna_ua_client_cancel cictna_ua_client_close_channel cictna_ua_client_connect cictna_ua_client_delete cictna_ua_client_disconnect cictna_ua_client_get_endpoints cictna_ua_client_iterate cictna_ua_client_new cictna_ua_client_poll cictna_ua_client_poll_log cictna_ua_client_send cictna_ua_client_state cictna_ua_codec_roundtrip cictna_ua_create_certificate cictna_ua_encryption_backend cictna_ua_features cictna_ua_free cictna_ua_selftest_loopback cictna_ua_server_add_namespace cictna_ua_server_add_node cictna_ua_server_delete cictna_ua_server_iterate cictna_ua_server_new cictna_ua_server_poll_log cictna_ua_server_read cictna_ua_server_start cictna_ua_server_stop cictna_ua_server_write cictna_ua_stack_version cictna_ua_status_name

Minimum glibc: GLIBC_2.34

## Platform verification

| Platform | Status |
|---|---|
| linux-x64 | verified here: native build, all test suites, managed and native smoke |
| linux-arm64 | cross-built, native smoke PASS under qemu; managed tests not run on ARM64 hardware |
| win-x64 | CI run [44548900001](https://github.com/talelksiksi/cictna-industrial/actions/runs/44548900001) on the self-hosted runner: native build success, managed build and tests success (171 passed, 0 failed, 5 skipped, 176 total, SMOKE OK), from `531d51a` |
