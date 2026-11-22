# Steps on the Windows PC

Everything that has to happen on the Windows PC that hosts the self-hosted CI runner and
cannot be done from CI. Tick items as they are done and commit the change, so this file is always
the current state. Newest open items first. Done items stay below with their date.

Legend: `[ ]` to do, `[x]` done.

---

## Open

Nothing required. Phases 2 and 3 needed only the Windows CI runner, which ran on its own.

### 6. (Optional) Prosys subscription and certificate store checks
Phase 2 added a Prosys test that subscribes to the simulation signals, and Phase 3 one that
trusts Prosys's certificate through our certificate store instead of the development flag. Not
required to close either phase (their exit criteria are met without them), but they check both
against a third, Java-based stack. Whenever Prosys is running anyway:
- [ ] In PowerShell, in the repo folder (both lines in the same window):
  ```powershell
  git pull
  powershell -ExecutionPolicy Bypass -File native\build.ps1
  $env:CICTNA_PROSYS_URL = "opc.tcp://<pc-name>:53530/OPCUA/SimulationServer"
  dotnet test --project tests\Interop\Cictna.Industrial.OpcUa.InteropTests -c Release
  ```
  Expected: 7 Prosys tests passed (our client certificate is already trusted in Prosys), node-opcua tests skipped
  (no Node.js on the PC).

---

## Optional

- [ ] Delete `C:\WINDOWS\ServiceProfiles\NetworkService\.dotnet` (an admin PowerShell:
  `Remove-Item -Recurse -Force C:\WINDOWS\ServiceProfiles\NetworkService\.dotnet`). Early CI runs
  installed the .NET SDK there; since 14 Nov CI installs it into the runner's tool cache instead,
  so nothing uses that folder.
- [ ] Self-hosted Linux runner instead of GitHub-hosted Ubuntu: only if CI minutes ever become a
  concern. Set the repository variable `LINUX_RUNS_ON` to `["self-hosted","linux","x64"]` after
  installing a Linux runner (WSL or a spare machine).

---

## Done

- [x] 2026-11-20: Phase 4 verified on the self-hosted runner: 359 tests on Windows, 352 passed,
  0 failed, 7 Prosys skipped, including the Modbus interop tests with modbus-serial and jsmodbus
  and the fuzz suite (CI run 44714362878). Nothing to do by hand.
- [x] 2026-11-20: Phase 3 verified on the self-hosted runner: 200 tests on Windows, 194 passed,
  6 Prosys skipped, 0 failed, including the trust tests. Nothing to do by hand.
- [x] 2026-11-19: Phase 2 verified on the self-hosted runner (CI run 44548900001): 176 tests on Windows,
  171 passed, 5 Prosys skipped, 0 failed, including the chaos tests. Nothing to do by hand.
- [x] 2026-11-19: Prosys interop passed (closes Phase 1). Prosys OPC UA Simulation Server on the
  PC at `opc.tcp://<pc-name>:53530/OPCUA/SimulationServer`: 5 passed, 0 failed (connect, browse
  and read of the simulation signals, signals change over time, server info and limits,
  GetEndpoints, Basic256Sha256 secure channel after trusting "Cictna Interop Client" in Prosys).
  The 9 node-opcua tests skipped locally (no Node.js on the PC); CI runs them. Lesson: `$env:` variables
  only live in the PowerShell window that set them; a new window or tab needs them set again.
- [x] 2026-11-14: First green CI run on Windows and Linux (run 43542102950): native win-x64 built
  with MSVC on the self-hosted runner, 110 tests on Windows (105 passed, 5 Prosys skipped, 0
  failed), smoke test OK on both. Closes Phase 0.
- [x] 2026-11-14: CI runner installed as a Windows service (`C:\ar\cictna`), running as
  NetworkService. Lessons, now handled by the scripts: the French service account name blocked
  the service start (re-assigned by its language-independent name); the service cannot see
  per-user installs, so build.ps1 finds CMake and Python itself and Python 3.13 is installed
  for all users (`C:\Program Files\Python313`; the per-user 3.12 could not be duplicated).
- [x] 2026-11-13: CMake 4.4.4 installed (winget); Visual Studio Community 2026 with the C++
  workload present.
