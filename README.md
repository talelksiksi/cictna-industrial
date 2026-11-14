# cictna-industrial

Industrial connectivity stack for CICTNA RDC, written in C# on .NET 10:

- **OPC UA** client and server over [open62541](https://www.open62541.org) (C, MPL-2.0), with mbedTLS for encryption
- **Modbus TCP** client and server, pure C#
- **PLC simulator** serving the same tags over OPC UA and Modbus, with fault injection
- **Store-and-forward** buffer for collectors (SQLite, WAL mode)

Proprietary, all rights reserved (see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

## Status

| Phase | Content | State |
|---|---|---|
| 0 | Repo, native build, packaging, CI, smoke test | Done (14 Nov 2026): CI green on Windows and Linux |
| 1 | OPC UA client core | Code complete, green on Windows and Linux CI; waits for the Prosys interop run on the PC |
| 2 | Subscriptions and resilience | next |
| 3 | Security and certificates | |
| 4 | Modbus TCP | |
| 5 | Simulator | |
| 6 | Store-and-forward and 24-hour soak | |
| 7 | Packaging and docs | |

A phase closes only when its exit criteria pass on Windows and Linux. See [CHANGELOG.md](CHANGELOG.md).

## Documentation

- [docs/roadmap.md](docs/roadmap.md): goal, decisions, architecture, phases and exit criteria
- [docs/pc-steps.md](docs/pc-steps.md): what has to be done on the Windows PC, with status
- [docs/builds/](docs/builds/): one record per build (what, from which commit, verified where)
- [docs/opcua-client.md](docs/opcua-client.md): how to use the OPC UA client (and the test server)
- [docs/native-layer.md](docs/native-layer.md): how open62541 is built, wrapped and loaded, and the open62541 behaviours we found
- [docs/coding-standards.md](docs/coding-standards.md)
- [CLAUDE.md](CLAUDE.md): working rules for this repository

## Layout

```
native/        pinned open62541 + mbedTLS, build scripts, our C shim, licence texts
src/           managed libraries (Cictna.Industrial.*)
tests/         Unit/, Integration/, Interop/ (node-opcua, Prosys), Smoke/ (Chaos, Soak, Fuzz come with their phases)
scripts/       runner setup, licence collection
docs/          coding standards, native layer design, PC steps, build records (docs/builds/)
```

## Build

Prerequisites: .NET 10 SDK, Git, CMake 3.20+, Python 3 (open62541 generates code with it). Node.js 22.13+ for the node-opcua interop tests (skipped without it).
Linux also needs `gcc`, `ninja-build`; ARM64 cross-builds need `gcc-aarch64-linux-gnu` (and `qemu-user-static` to test).
Windows needs Visual Studio 2022+ or the Build Tools with "Desktop development with C++".

```bash
# Linux
native/build.sh linux-x64          # -> native/out/linux-x64/libcictna_ua.so
native/build.sh linux-arm64        # optional cross-build
dotnet test -c Release
dotnet run --project tests/Smoke -c Release
```

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File native\build.ps1   # -> native\out\win-x64\cictna_ua.dll
dotnet test -c Release
dotnet run --project tests\Smoke -c Release
```

The first native build takes about a minute; later builds are incremental.

## CI

CI runs on demand (Actions > CI > Run workflow) and on `v*` tags, never on every push.
Windows jobs run on the self-hosted runner on Talel's PC (set it up once with
`scripts/setup-runner-windows.ps1`). Linux jobs run on GitHub-hosted Ubuntu by default.
A `v*` tag also packs the NuGet packages with all three runtimes and publishes them to GitHub Packages.
