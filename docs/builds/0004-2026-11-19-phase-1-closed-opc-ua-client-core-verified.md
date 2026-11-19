# Build 0004: Phase 1 closed, OPC UA client core verified

<!-- commit: 44a2f9d368c69323bf45ecb01cc553e4f5740cb7 -->

| | |
|---|---|
| Result | **PASS** |
| Date (UTC) | 2026-11-19 14:14 |
| Commit | `44a2f9d` Phase 1 closed: Prosys interop passes on the PC |
| Uncommitted changes | no |
| Built on | Linux x86_64, Ubuntu 24.04.5 LTS |
| .NET SDK | 10.0.115 |
| CMake | 3.28.3 |
| open62541 | v1.5.10 (`93c3bdfc9ea4`) |
| mbedTLS | v3.6.7 (`068ff080b369`) |

## Notes

Phase 1 closed: OPC UA client core verified against three independent servers.

- Prosys OPC UA Simulation Server (Java SDK), on Talel's Windows PC, 19 Nov 2026, at
  `opc.tcp://TALEL-PC-NEW:53530/OPCUA/SimulationServer`: 5 passed, 0 failed.
  Tests: connect, browse and read the simulation signals; signals change over time; server info
  and operation limits; GetEndpoints; Basic256Sha256 secure channel.
- First Prosys run: 4 passed, the secure channel refused with BadSecurityChecksFailed, as designed
  (Prosys did not trust our client certificate yet). After trusting "Cictna Interop Client" in
  Prosys (Certificates tab): 5 of 5.
- node-opcua interop: green on Windows and Linux CI (skipped on the PC locally, no Node.js there).
- Our own test server: green on Windows and Linux CI.
- Windows CI results below are from run 43544594913 (commit 4375d69). Since then only docs changed.
- Phase 1 exit criterion met: tests pass against our own test server, node-opcua and Prosys.
- Next: Phase 2, subscriptions and resilience.

## Changes (since build 0003 (`47610d5`))

- `44a2f9d` Phase 1 closed: Prosys interop passes on the PC
- `eea9dc8` pc-steps: optional cleanup of the old .NET SDK in the NetworkService profile
- `4375d69` CI: Windows build cache saves (gzip on PATH); self-hosted SDK goes to the runner tool cache
- `9e3b116` Build record 0003: Phase 0 closed, Windows and Linux verified

## Steps

| Step | Result | Seconds |
|---|---|---|
| native linux-x64 | PASS | 7 |
| native linux-arm64 | PASS | 8 |
| dotnet build | PASS | 31 |
| tests | PASS | 16 |
| managed smoke | PASS | 1 |
| native smoke x64 | PASS | 1 |
| native smoke arm64 (qemu) | PASS | 2 |
| pack | PASS | 2 |

Tests: 105 passed, 0 failed, 5 skipped, 110 total.

Managed smoke test output:

```
Runtime: linux-x64, .NET 10.0.15
[PASS] native library loads: open62541 v1.5.10
[PASS] ABI version: 2 (expected 2)
[PASS] encryption backend: mbedtls
[PASS] required features: Encryption, Multithreading, Subscriptions, MethodCalls, Historizing
[PASS] loopback server + client read: port 40791, Good, server time 2026-11-19T14:14:26.8814402+00:00
[PASS] server time is plausible: skew 503 ms
SMOKE OK
```

## Native binaries

| RID | File | Bytes | SHA-256 | Compiler | Built (UTC) |
|---|---|---|---|---|---|
| linux-x64 | `libcictna_ua.so` | 2659160 | `66a6e599a24bd0b32fc5535f8aba1a0919a239421f94c65f0d14d24cbc73fdfd` | cc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-19T14:13:29Z |
| linux-arm64 | `libcictna_ua.so` | 2371944 | `0e47b61e3eb38bbaa35af0e52dae79d08f8106b8aaae3f5256c2cdde66a0abe5` | aarch64-linux-gnu-gcc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-19T14:13:38Z |
| win-x64 (CI) | `cictna_ua.dll` | 2014720 | `8d39c1394fe106cc9c66ab1dd7acd59a2e006e0b10955d25c1a581fa529be6fd` | MSVC 19.51.36260.0 | 2026-11-14T02:03:20Z |

Exported symbols (linux-x64): cictna_ua_abi_version cictna_ua_client_cancel cictna_ua_client_connect cictna_ua_client_delete cictna_ua_client_disconnect cictna_ua_client_get_endpoints cictna_ua_client_iterate cictna_ua_client_new cictna_ua_client_poll cictna_ua_client_poll_log cictna_ua_client_send cictna_ua_client_state cictna_ua_codec_roundtrip cictna_ua_create_certificate cictna_ua_encryption_backend cictna_ua_features cictna_ua_free cictna_ua_selftest_loopback cictna_ua_server_add_namespace cictna_ua_server_add_node cictna_ua_server_delete cictna_ua_server_iterate cictna_ua_server_new cictna_ua_server_poll_log cictna_ua_server_read cictna_ua_server_start cictna_ua_server_stop cictna_ua_server_write cictna_ua_stack_version cictna_ua_status_name

Minimum glibc: GLIBC_2.34

## Platform verification

| Platform | Status |
|---|---|
| linux-x64 | verified here: native build, all test suites, managed and native smoke |
| linux-arm64 | cross-built, native smoke PASS under qemu; managed tests not run on ARM64 hardware |
| win-x64 | CI run [43544594913](https://github.com/talelksiksi/cictna-industrial/actions/runs/43544594913) on the self-hosted runner: native build success, managed build and tests success (105 passed, 0 failed, 5 skipped, 110 total, SMOKE OK), from `4375d69` |
