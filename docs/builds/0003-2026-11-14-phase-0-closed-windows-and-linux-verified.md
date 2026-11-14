# Build 0003: Phase 0 closed, Windows and Linux verified

<!-- commit: 5541671d75bca9e39a73f627cdddb2e8e21b0651 -->

| | |
|---|---|
| Result | **PASS** |
| Date (UTC) | 2026-11-14 01:57 |
| Commit | `5541671` CI prints build info; build records can include a CI run's Windows results (--ci-run) |
| Uncommitted changes | no |
| Built on | Linux x86_64, Ubuntu 24.04.5 LTS |
| .NET SDK | 10.0.115 |
| CMake | 3.28.3 |
| open62541 | v1.5.10 (`93c3bdfc9ea4`) |
| mbedTLS | v3.6.7 (`068ff080b369`) |

## Notes

Phase 0 closed: first builds verified on Windows and Linux.

- Windows: native win-x64 built with MSVC (Visual Studio 2026) on the self-hosted runner on the PC,
  and the managed build, all test suites and the smoke test run there in CI run
  [43543036264](https://github.com/talelksiksi/cictna-industrial/actions/runs/43543036264).
  Phase 0 exit criterion met: CI builds native and managed code on both OSes and the smoke test
  loads the native library and runs the loopback self test.
- Phase 1 status: green on Windows and Linux, including node-opcua interop. Closes after the
  Prosys run on the PC (docs/pc-steps.md, step 5).
- Windows setup lessons, now in the scripts: the runner service's localized account name
  (French Windows) blocked its start; the service cannot see per-user installs, so build.ps1
  finds CMake and Python itself and Python is installed for all users; setup-python needs admin
  on a self-hosted runner and is not used.
- Open item: the Windows build cache fails to save (tar on the runner), so win-x64 is rebuilt on
  every run (about 3 minutes). Harmless; to fix with the next CI change.

## Changes (since build 0002 (`74d270b`))

- `5541671` CI prints build info; build records can include a CI run's Windows results (--ci-run)
- `9742838` Phase 0 closed: CI green on Windows and Linux; runner setup handles localized service accounts
- `0209811` Windows build: find Python 3 like CMake instead of setup-python (needs admin on self-hosted)
- `90f00af` CI: install Python 3 for the Windows native build (open62541 code generators)
- `409dd2b` build.ps1: find CMake without relying on PATH (runner service does not see new PATH entries)
- `a0f6329` Runner setup and PC steps: use the talelksiksi gh account without switching
- `3ae2d78` Build record 0002: Phase 1 OPC UA client core (Linux)

## Steps

| Step | Result | Seconds |
|---|---|---|
| native linux-x64 | PASS | 7 |
| native linux-arm64 | PASS | 7 |
| dotnet build | PASS | 26 |
| tests | PASS | 15 |
| managed smoke | PASS | 2 |
| native smoke x64 | PASS | 1 |
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
[PASS] loopback server + client read: port 40205, Good, server time 2026-11-14T01:57:08.6066471+00:00
[PASS] server time is plausible: skew 503 ms
SMOKE OK
```

## Native binaries

| RID | File | Bytes | SHA-256 | Compiler | Built (UTC) |
|---|---|---|---|---|---|
| linux-x64 | `libcictna_ua.so` | 2659160 | `66a6e599a24bd0b32fc5535f8aba1a0919a239421f94c65f0d14d24cbc73fdfd` | cc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-14T01:56:17Z |
| linux-arm64 | `libcictna_ua.so` | 2371944 | `0e47b61e3eb38bbaa35af0e52dae79d08f8106b8aaae3f5256c2cdde66a0abe5` | aarch64-linux-gnu-gcc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-14T01:56:26Z |
| win-x64 (CI) | `cictna_ua.dll` | 2014720 | `bab147ec4bf2f1d6a266ad053f32e14ff082d857ef57339c38317e7ea10e09d1` | MSVC 19.51.36260.0 | 2026-11-14T01:54:21Z |

Exported symbols (linux-x64): cictna_ua_abi_version cictna_ua_client_cancel cictna_ua_client_connect cictna_ua_client_delete cictna_ua_client_disconnect cictna_ua_client_get_endpoints cictna_ua_client_iterate cictna_ua_client_new cictna_ua_client_poll cictna_ua_client_poll_log cictna_ua_client_send cictna_ua_client_state cictna_ua_codec_roundtrip cictna_ua_create_certificate cictna_ua_encryption_backend cictna_ua_features cictna_ua_free cictna_ua_selftest_loopback cictna_ua_server_add_namespace cictna_ua_server_add_node cictna_ua_server_delete cictna_ua_server_iterate cictna_ua_server_new cictna_ua_server_poll_log cictna_ua_server_read cictna_ua_server_start cictna_ua_server_stop cictna_ua_server_write cictna_ua_stack_version cictna_ua_status_name

Minimum glibc: GLIBC_2.34

## Platform verification

| Platform | Status |
|---|---|
| linux-x64 | verified here: native build, all test suites, managed and native smoke |
| linux-arm64 | cross-built, native smoke PASS under qemu; managed tests not run on ARM64 hardware |
| win-x64 | CI run [43543036264](https://github.com/talelksiksi/cictna-industrial/actions/runs/43543036264) on the self-hosted runner: native build success, managed build and tests success (105 passed, 0 failed, 5 skipped, 110 total, SMOKE OK), from `5541671` |
