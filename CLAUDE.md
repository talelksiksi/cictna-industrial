# cictna-industrial: working rules

Personal / CICTNA RDC project. Not employer work.

## Identity and confidentiality
- Commit only as `Talel Ksiksi <6784031+talelksiksi@users.noreply.github.com>`. Never a work identity.
  In a cloud workspace, set the repo-local identity first and check `git log --format='%an <%ae> | %cn <%ce>'` before pushing.
- No code, product names, protocols, telegram formats or client names from Talel's employer or its clients,
  anywhere: code, tests, sample plants, commit messages, UI text. Sample data is invented.
- Push only to private repos under `talelksiksi`.

## Quality bar
- A phase closes only when its exit criteria pass on Windows and Linux (see README).
- Test first. Every network call: timeout, CancellationToken, structured log, metric.
- Native boundary: SafeHandle for every open62541 object, no raw pointers in public API,
  all native calls for one client serialized, callbacks marshalled safely. The C shim is the only
  native surface; keep `native/shim/cictna_ua.h` and `NativeMethods.cs` in step and bump
  `CICTNA_UA_ABI_VERSION` with any signature change.
- Do not modify files under `native/.deps/` (open62541, mbedTLS). Behaviour changes go in the shim or C#.
- Warnings are errors. Public API has XML docs.

## UI
- Any UI uses the cictnardc.net palette: sea #3d6d78, sea-deep #2c5560, pine #2f5a46, dawn #e7a982,
  sun #f1c09b, ink #1f2e2f, stone #5b6b6a, rule #d4dddb, fog #eef2f1, mist #f8faf9.
  Fonts: Archivo (text), JetBrains Mono (values, code).
- Client-facing text: natural, no em dashes, no exclamation marks, no buzzwords.

## Commands
```bash
native/build.sh linux-x64        # native library (also linux-arm64)
dotnet test -c Release           # unit, integration and interop tests (Microsoft Testing Platform)
(cd tests/Interop/node-opcua-server && npm ci)   # once, for the node-opcua interop server
scripts/build-and-record.sh "<title>" --notes notes.md   # full pipeline + build record
dotnet run --project tests/Smoke -c Release
```
