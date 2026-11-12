# Build 0001: Phase 0 foundation

<!-- commit: 7baf1d58c1b82d9d19829674f6dbb35d23c2b21a -->

| | |
|---|---|
| Result | **PASS** |
| Date (UTC) | 2026-11-12 11:22 |
| Commit | `7baf1d5` Add PC steps checklist, build records and in-code documentation |
| Uncommitted changes | no |
| Built on | Linux x86_64, Ubuntu 24.04.5 LTS |
| .NET SDK | 10.0.115 |
| CMake | 3.28.3 |
| open62541 | v1.5.10 (`93c3bdfc9ea4`) |
| mbedTLS | v3.6.7 (`068ff080b369`) |

## Notes

Phase 0 (foundation) build: first complete build of the native layer and its .NET binding.

- open62541 v1.5.10 and mbedTLS v3.6.7, pinned by commit, statically linked with the cictna_ua
  shim into one library per platform. Encryption, internal locking, reduced namespace 0, no PubSub.
- `Cictna.Industrial.OpcUa.Native` 0.1.0: bindings, loader, ABI and feature checks, loopback self test.
- Decisions recorded in this build: .NET 10 LTS; proprietary licence; Windows x64, Linux x64 and
  Linux ARM64; Linux CI on GitHub-hosted Ubuntu, Windows CI on the self-hosted runner.

Phase 0 is not closed yet: the Windows build has not run. It needs the self-hosted runner
(docs/pc-steps.md, steps 1 to 4).

## Changes (full history, first record)

- `7baf1d5` Add PC steps checklist, build records and in-code documentation
- `725c5db` Phase 0: native build, bindings, packaging, CI
- `886270d` Initial commit

## Steps

| Step | Result | Seconds |
|---|---|---|
| native linux-x64 | PASS | 32 |
| native linux-arm64 | PASS | 57 |
| dotnet build | PASS | 3 |
| unit tests | PASS | 4 |
| managed smoke | PASS | 1 |
| native smoke x64 | PASS | 0 |
| native smoke arm64 (qemu) | PASS | 1 |
| pack | PASS | 2 |

Unit tests: 11 passed, 0 failed, 11 total.

Managed smoke test output:

```
Runtime: linux-x64, .NET 10.0.15
[PASS] native library loads: open62541 v1.5.10
[PASS] ABI version: 1 (expected 1)
[PASS] encryption backend: mbedtls
[PASS] required features: Encryption, Multithreading, Subscriptions, MethodCalls, Historizing
[PASS] loopback server + client read: port 36647, Good, server time 2026-11-12T11:22:16.7171688+00:00
[PASS] server time is plausible: skew 503 ms
SMOKE OK
```

## Native binaries

| RID | File | Bytes | SHA-256 | Compiler | Built (UTC) |
|---|---|---|---|---|---|
| linux-x64 | `libcictna_ua.so` | 2515800 | `38422d85aea2aa43f18c5a6655635572fc8a572bdc7eeb7cda6f4d410e9bf0f8` | cc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-12T11:21:12Z |
| linux-arm64 | `libcictna_ua.so` | 2240840 | `bbd9791ee268f723d5a84a39bbc37d3662f778dcda514d3616095dd941897b87` | aarch64-linux-gnu-gcc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0 | 2026-11-12T11:22:09Z |
| win-x64 | not built here | | | | |

Exported symbols (linux-x64): cictna_ua_abi_version cictna_ua_encryption_backend cictna_ua_features cictna_ua_selftest_loopback cictna_ua_stack_version cictna_ua_status_name

Minimum glibc: GLIBC_2.34

## Platform verification

| Platform | Status |
|---|---|
| linux-x64 | verified here: native build, unit tests, managed and native smoke |
| linux-arm64 | cross-built, native smoke PASS under qemu; managed tests not run on ARM64 hardware |
| win-x64 | **pending**: needs the self-hosted runner (docs/pc-steps.md) |
