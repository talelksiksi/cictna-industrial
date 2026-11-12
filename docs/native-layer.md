# Native layer

How the OPC UA protocol core (open62541, in C) is built, packaged and called from C#.
Read this before touching anything under `native/` or `src/Cictna.Industrial.OpcUa.Native/`.

## The pieces

```
open62541 (C, MPL-2.0)   ─┐
mbedTLS   (C, Apache-2.0) ├─ static libs ─► cictna_ua (shared lib, our C shim) ─► P/Invoke ─► C#
our shim  (C, proprietary)┘                 exports only cictna_ua_*
```

| Path | What it is |
|---|---|
| `native/versions.env` | Pinned tag + commit of open62541 and mbedTLS. The only place versions change. |
| `native/build.sh` | Linux build (x64 native, ARM64 cross). Every CMake option is explained in it. |
| `native/build.ps1` | Windows build (MSVC, static C runtime). Same options as build.sh. |
| `native/shim/cictna_ua.h` | The C boundary. The contract between C and C#. |
| `native/shim/cictna_ua.c` | Implementation. All open62541 struct handling happens here. |
| `native/shim/CMakeLists.txt` | Links everything into one library and hides all non-`cictna_ua_*` symbols. |
| `native/toolchains/` | CMake toolchain for the ARM64 cross-compiler. |
| `native/tests/native_smoke.c` | Loads the library with `dlopen` and runs the self test (ARM64 under qemu in CI). |
| `native/licenses/` | Licence texts shipped in the NuGet package. Regenerate with `scripts/collect-native-licenses.py`. |
| `native/.deps/`, `.build/`, `out/` | Fetched sources, build trees, outputs. Git-ignored. |

## Why a C shim instead of calling open62541 directly

1. Large parts of open62541's API are `static inline` functions and macros. They have no exported
   symbol, so P/Invoke cannot call them.
2. Its structs change between versions and with build options. Mirroring them in C# would break
   silently on a version bump. In the shim they are compiled against the exact headers.
3. A small plain-C surface (ints, pointers, UTF-8 strings, status codes) is easy to keep stable,
   version (`CICTNA_UA_ABI_VERSION`) and test.

## Why one static library per platform

open62541 and mbedTLS are linked statically into `cictna_ua`, and the linker hides their symbols.
Each platform therefore ships one file that depends only on the C library:

- client machines need no OpenSSL, mbedTLS or VC++ redistributable;
- our open62541 cannot clash with another copy loaded in the same process;
- the package version fully determines the protocol stack version.

MPL-2.0 allows static linking with proprietary code as long as open62541's own files stay
unmodified or their changes are published. We never edit them.

## Build options that matter

See the comment block in `native/build.sh` for every option. The important ones:

- **Encryption: mbedTLS.** Enables Basic256Sha256 and the other Sign / SignAndEncrypt policies.
  Tests fail if a build comes out without it.
- **UA_MULTITHREADING=100.** open62541 locks internally around its public API. The C# layer still
  serializes calls per client; the lock is a safety net for callbacks.
- **Namespace zero REDUCED.** Enough for the client and the simulator's server. FULL would need the
  ua-nodeset submodule and is a Phase 5 decision.
- **Log level 300 (info).** Trace and debug logging are compiled out.

## Loading at run time

`NativeLibraryResolver` (registered by a module initializer before the first P/Invoke) looks in:

1. the runtime's default probing, which for NuGet consumers uses `deps.json` and finds
   `runtimes/<rid>/native/` automatically;
2. `runtimes/<rid>/native/` under the application folder, for project references and plain copies.

`OpcUaNativeLibrary.EnsureCompatible()` then checks the ABI version. Call it once at startup.

## Changing the C boundary

In one commit: edit `cictna_ua.h` and `cictna_ua.c`, mirror the change in `NativeMethods.cs`, bump
`CICTNA_UA_ABI_VERSION` and `OpcUaNativeLibrary.ExpectedAbiVersion`, add tests.

## Bumping open62541 or mbedTLS

1. Find the new tag and its commit: `git ls-remote --tags <url> <tag>`.
2. Update tag and commit in `native/versions.env`.
3. `native/build.sh linux-x64 --clean` (and arm64), run the tests.
4. `python3 scripts/collect-native-licenses.py`, then review `THIRD-PARTY-NOTICES.md`
   (versions, commits, the list of compiled-in dependencies).
5. Read the upstream release notes for API changes that touch the shim.
6. Run CI on Windows, then write a build record (`scripts/build-and-record.sh`).

## Known limits

- Linux binaries need glibc 2.34 or newer (Ubuntu 22.04+, Debian 12+, recent Raspberry Pi OS),
  because they are built on Ubuntu 24.04. Older edge devices need a build in an older container.
- ARM64 is cross-compiled and tested natively under qemu. The managed tests have not run on real
  ARM64 hardware yet.
