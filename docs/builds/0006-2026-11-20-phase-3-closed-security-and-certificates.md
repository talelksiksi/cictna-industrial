# Build 0006: Phase 3 closed, security and certificates

<!-- commit: e3ba7de7a4fb7dd733018ec4ba7b897bc4935021 -->

| | |
|---|---|
| Result | **PASS** |
| Date (UTC) | 2026-11-20 09:14 |
| Commit | `e3ba7de` Phase 3 closed on Windows and Linux |
| Uncommitted changes | no |
| Built on | Linux x86_64, Ubuntu 24.04.5 LTS |
| .NET SDK | 10.0.115 |
| CMake | 3.28.3 |
| open62541 | v1.5.10 (`93c3bdfc9ea4`) |
| mbedTLS | v3.6.7 (`068ff080b369`) |

## Notes

Phase 3 closed: security and certificates, verified on Windows and Linux.

- Exit criteria (docs/roadmap.md): trusted, rejected, expired and renewed certificates, on both
  sides of the connection, on both systems. 12 trust tests and 11 store tests, here and in CI run
  [44708296967](https://github.com/talelksiksi/cictna-industrial/actions/runs/44708296967) on the Windows runner.
- Certificate store in the OPC UA Part 12 folder layout; own certificate created and renewed;
  refused peers saved to rejected/certs on both sides; a running server picks up newly trusted
  clients within a second; plant CA, issuer-only CAs and CRLs supported.
- Secure by default: the server no longer accepts every client certificate; only explicit
  development flags skip the checks, and they log a warning.
- The client checks the server certificate with open62541's own rules before connecting, so a
  refusal names the exact reason and the side that refused.
- Private keys readable by the owner only on Linux (mode 600) and Windows (ACL without
  inheritance: current account, SYSTEM, Administrators), checked by tests on both.
- Native shim ABI 4. Why the store is in C# rather than open62541's Filestore: the Filestore has
  no change detection on Windows.
- Windows CI: 201 tests, 194 passed, 7 Prosys skipped, 0 failed. Optional Prosys check on the PC:
  docs/pc-steps.md step 6 (7 Prosys tests).
- Next: Phase 4, Modbus TCP.

## Changes (since build 0005 (`ea8be34`))

- `e3ba7de` Phase 3 closed on Windows and Linux
- `7c7ef57` Phase 3: private key folder locked down on Windows too; docs; Prosys store test
- `762443e` Phase 3: trust tests on both sides; own certificate settings; endpoint selection with stored certificates
- `cf31c05` Phase 3: certificate store, trust lists for client and server (ABI 4)
- `bf75605` Build record 0005: Phase 2 closed, subscriptions, reconnect and triggers

## Steps

| Step | Result | Seconds |
|---|---|---|
| native linux-x64 | PASS | 2 |
| native linux-arm64 | PASS | 27 |
| dotnet build | PASS | 13 |
| tests | PASS | 50 |
| managed smoke | PASS | 1 |
| native smoke x64 | PASS | 1 |
| native smoke arm64 (qemu) | PASS | 1 |
| pack | PASS | 3 |

Tests: 194 passed, 0 failed, 7 skipped, 201 total.

Managed smoke test output:

```
Runtime: linux-x64, .NET 10.0.15
[PASS] native library loads: open62541 v1.5.10
[PASS] ABI version: 4 (expected 4)
[PASS] encryption backend: mbedtls
[PASS] required features: Encryption, Multithreading, Subscriptions, MethodCalls, Historizing
[PASS] loopback server + client read: port 37937, Good, server time 2026-11-20T09:14:15.0817761+00:00
[PASS] server time is plausible: skew 503 ms
SMOKE OK
```

## Native binaries

| RID | File | Bytes | SHA-256 | Compiler | Built (UTC) |
|---|---|---|---|---|---|
| linux-x64 | `libcictna_ua.so` | 2655064 | `a72362028dd8b0b40fdbc62b415c9debe2be7d27c76c435f2400311183c94d0b` | cc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-20T09:12:43Z |
| linux-arm64 | `libcictna_ua.so` | 2371944 | `3a6380d71d8c367149bba94c6bcc59608f2abdafbbb0b15396e8d1198332e366` | aarch64-linux-gnu-gcc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-20T09:13:11Z |
| win-x64 (CI) | `cictna_ua.dll` | 2016768 | `42498f381ff34e5862cbe7bb5d87b983caa982814d3e64b71d47bb2d9b569666` | MSVC 19.51.36260.0 | 2026-11-20T09:04:54Z |

Exported symbols (linux-x64): cictna_ua_abi_version cictna_ua_client_cancel cictna_ua_client_close_channel cictna_ua_client_connect cictna_ua_client_delete cictna_ua_client_disconnect cictna_ua_client_get_endpoints cictna_ua_client_iterate cictna_ua_client_new cictna_ua_client_poll cictna_ua_client_poll_log cictna_ua_client_send cictna_ua_client_state cictna_ua_codec_roundtrip cictna_ua_create_certificate cictna_ua_encryption_backend cictna_ua_features cictna_ua_free cictna_ua_selftest_loopback cictna_ua_server_add_namespace cictna_ua_server_add_node cictna_ua_server_delete cictna_ua_server_get_rejected cictna_ua_server_iterate cictna_ua_server_new cictna_ua_server_poll_log cictna_ua_server_read cictna_ua_server_set_trust_list cictna_ua_server_start cictna_ua_server_stop cictna_ua_server_write cictna_ua_stack_version cictna_ua_status_name cictna_ua_verify_certificate

Minimum glibc: GLIBC_2.34

## Platform verification

| Platform | Status |
|---|---|
| linux-x64 | verified here: native build, all test suites, managed and native smoke |
| linux-arm64 | cross-built, native smoke PASS under qemu; managed tests not run on ARM64 hardware |
| win-x64 | CI run [44708296967](https://github.com/talelksiksi/cictna-industrial/actions/runs/44708296967) on the self-hosted runner: native build success, managed build and tests success (194 passed, 0 failed, 7 skipped, 201 total, SMOKE OK), from `762443e` |
