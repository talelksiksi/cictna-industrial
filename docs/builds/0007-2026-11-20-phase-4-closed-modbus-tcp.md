# Build 0007: Phase 4 closed, Modbus TCP

<!-- commit: 905045282c11f454b2c61886d38206ac7517264e -->

| | |
|---|---|
| Result | **PASS** |
| Date (UTC) | 2026-11-20 09:58 |
| Commit | `9050452` Phase 4 closed on Windows and Linux |
| Uncommitted changes | no |
| Built on | Linux x86_64, Ubuntu 24.04.5 LTS |
| .NET SDK | 10.0.115 |
| CMake | 3.28.3 |
| open62541 | v1.5.10 (`93c3bdfc9ea4`) |
| mbedTLS | v3.6.7 (`068ff080b369`) |

## Notes

Phase 4 closed: Modbus TCP client and server, verified on Windows and Linux.

- Exit criteria (docs/roadmap.md): unit tests per function code (110, checked against the
  examples of the Modbus Application Protocol Specification V1.1b3), interop against
  independent implementations, parser fuzzing without crashes. All pass here and in CI run
  [44714362878](https://github.com/talelksiksi/cictna-industrial/actions/runs/44714362878) on the Windows runner.
- Function codes 1, 2, 3, 4, 5, 6, 15, 16 and 23 on both sides, unit IDs, `SendRawAsync` for
  anything else. Pure C#, no native code; the native library is unchanged (ABI 4).
- Client: results instead of exceptions, per-request timeouts, pipelining matched by transaction
  ID (late answers discarded), reads and writes split to protocol or device limits, typed values
  in all four word and byte orders, reconnect with backoff, connection rebuilt after repeated
  timeouts.
- Server: one data store per unit, checks in the order the specification gives, connection
  limit, idle and frame timeouts; an internal error costs one connection, never the server.
- Interop in both directions with modbus-serial (ISC) and jsmodbus (MIT). Found: jsmodbus answers
  reads past its data with short responses (refused as BadDecodingError) and goes silent on a
  connection after an unknown function code (the client rebuilds it); it also crashed on a reset
  connection, so the test peer guards against that.
- Fuzzing: about 650,000 mutated and random inputs per run for the two parsers, garbage over real
  sockets in both directions; seeded and repeatable. Two planted parser bugs were found by it.
- Windows CI: 359 tests, 352 passed, 7 Prosys skipped, 0 failed. Nothing to do on the PC.
- Next: Phase 5, the simulator.

## Changes (since build 0006 (`e3ba7de`))

- `9050452` Phase 4 closed on Windows and Linux
- `5919386` Phase 4: Modbus guide, changelog, notices; CI installs the Modbus peers and packs Modbus
- `f15c362` Phase 4: Modbus fuzz suite; internal errors cost one connection, never the server
- `f5a9d03` Phase 4: Modbus interop against modbus-serial and jsmodbus, both directions
- `e334041` Phase 4: Modbus TCP client and server, integration tests
- `806d0f6` Phase 4: Modbus protocol layer, data store and value conversion
- `0d4f245` Build record 0006: Phase 3 closed, security and certificates

## Steps

| Step | Result | Seconds |
|---|---|---|
| native linux-x64 | PASS | 2 |
| native linux-arm64 | PASS | 1 |
| dotnet build | PASS | 16 |
| tests | PASS | 52 |
| managed smoke | PASS | 1 |
| native smoke x64 | PASS | 0 |
| native smoke arm64 (qemu) | PASS | 1 |
| pack | PASS | 2 |

Tests: 352 passed, 0 failed, 7 skipped, 359 total.

Managed smoke test output:

```
Runtime: linux-x64, .NET 10.0.15
[PASS] native library loads: open62541 v1.5.10
[PASS] ABI version: 4 (expected 4)
[PASS] encryption backend: mbedtls
[PASS] required features: Encryption, Multithreading, Subscriptions, MethodCalls, Historizing
[PASS] loopback server + client read: port 37177, Good, server time 2026-11-20T09:57:55.8040246+00:00
[PASS] server time is plausible: skew 503 ms
SMOKE OK
```

## Native binaries

| RID | File | Bytes | SHA-256 | Compiler | Built (UTC) |
|---|---|---|---|---|---|
| linux-x64 | `libcictna_ua.so` | 2655064 | `a72362028dd8b0b40fdbc62b415c9debe2be7d27c76c435f2400311183c94d0b` | cc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-20T09:56:45Z |
| linux-arm64 | `libcictna_ua.so` | 2371944 | `3a6380d71d8c367149bba94c6bcc59608f2abdafbbb0b15396e8d1198332e366` | aarch64-linux-gnu-gcc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-20T09:56:47Z |
| win-x64 (CI) | `cictna_ua.dll` | 2016768 | `42498f381ff34e5862cbe7bb5d87b983caa982814d3e64b71d47bb2d9b569666` | MSVC 19.51.36260.0 | 2026-11-20T09:04:54Z |

Exported symbols (linux-x64): cictna_ua_abi_version cictna_ua_client_cancel cictna_ua_client_close_channel cictna_ua_client_connect cictna_ua_client_delete cictna_ua_client_disconnect cictna_ua_client_get_endpoints cictna_ua_client_iterate cictna_ua_client_new cictna_ua_client_poll cictna_ua_client_poll_log cictna_ua_client_send cictna_ua_client_state cictna_ua_codec_roundtrip cictna_ua_create_certificate cictna_ua_encryption_backend cictna_ua_features cictna_ua_free cictna_ua_selftest_loopback cictna_ua_server_add_namespace cictna_ua_server_add_node cictna_ua_server_delete cictna_ua_server_get_rejected cictna_ua_server_iterate cictna_ua_server_new cictna_ua_server_poll_log cictna_ua_server_read cictna_ua_server_set_trust_list cictna_ua_server_start cictna_ua_server_stop cictna_ua_server_write cictna_ua_stack_version cictna_ua_status_name cictna_ua_verify_certificate

Minimum glibc: GLIBC_2.34

## Platform verification

| Platform | Status |
|---|---|
| linux-x64 | verified here: native build, all test suites, managed and native smoke |
| linux-arm64 | cross-built, native smoke PASS under qemu; managed tests not run on ARM64 hardware |
| win-x64 | CI run [44714362878](https://github.com/talelksiksi/cictna-industrial/actions/runs/44714362878) on the self-hosted runner: native build success, managed build and tests success (352 passed, 0 failed, 7 skipped, 359 total, SMOKE OK), from `762443e` |
