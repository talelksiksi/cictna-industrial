# Changelog

All notable changes are listed here. Versions follow semantic versioning; tags are `vX.Y.Z`.

## [Unreleased]

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
