# Steps on Talel's PC

Everything that has to happen on the Windows PC (`talel-pc-new`) and cannot be done from the
cloud workspace. Tick items as they are done and commit the change, so this file is always
the current state. Newest open items first. Done items stay below with their date.

Legend: `[ ]` to do, `[x]` done.

---

## Open

Nothing required. Phase 2 needed only the Windows CI runner, which ran on its own.

### 6. (Optional) Prosys subscription check
Phase 2 added a sixth Prosys test that subscribes to the simulation signals. Not required to close
the phase (its exit criteria are met without it), but it checks subscriptions against a third,
Java-based stack. Whenever Prosys is running anyway:
- [ ] In PowerShell, in the repo folder (both lines in the same window):
  ```powershell
  git pull
  powershell -ExecutionPolicy Bypass -File native\build.ps1
  $env:CICTNA_PROSYS_URL = "opc.tcp://TALEL-PC-NEW:53530/OPCUA/SimulationServer"
  dotnet test --project tests\Interop\Cictna.Industrial.OpcUa.InteropTests -c Release
  ```
  Expected: 6 Prosys tests passed (the certificate is already trusted), node-opcua tests skipped
  (no Node.js on the PC). Paste the summary to Claude.

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

- [x] 2026-11-19: Phase 2 verified on the self-hosted runner (CI run 44548900001): 176 tests on Windows,
  171 passed, 5 Prosys skipped, 0 failed, including the chaos tests. Nothing to do by hand.
- [x] 2026-11-19: Prosys interop passed (closes Phase 1). Prosys OPC UA Simulation Server on the
  PC at `opc.tcp://TALEL-PC-NEW:53530/OPCUA/SimulationServer`: 5 passed, 0 failed (connect, browse
  and read of the simulation signals, signals change over time, server info and limits,
  GetEndpoints, Basic256Sha256 secure channel after trusting "Cictna Interop Client" in Prosys).
  The 9 node-opcua tests skipped locally (no Node.js on the PC); CI runs them. Lesson: `$env:` variables
  only live in the PowerShell window that set them; a new window or tab needs them set again. (Talel.)
- [x] 2026-11-14: First green CI run on Windows and Linux (run 43542102950): native win-x64 built
  with MSVC on the self-hosted runner, 110 tests on Windows (105 passed, 5 Prosys skipped, 0
  failed), smoke test OK on both. Closes Phase 0.
- [x] 2026-11-14: CI runner installed as a Windows service (`C:\ar\cictna`), running as
  NetworkService. Lessons, now handled by the scripts: the French service account name blocked
  the service start (re-assigned by its language-independent name); the service cannot see
  per-user installs, so build.ps1 finds CMake and Python itself and Python 3.13 is installed
  for all users (`C:\Program Files\Python313`; the per-user 3.12 could not be duplicated).
- [x] 2026-11-13: CMake 4.4.4 installed (winget); Visual Studio Community 2026 with the C++
  workload present. (Talel.)
- [x] 2026-11-13: Two GitHub accounts side by side: `talel-CICT` stays active in gh,
  `talelksiksi` stored next to it. `.gitconfig-personal` uses the talelksiksi token for every
  repo under `source\personal`; `get-latest.cmd` sets GH_TOKEN for its own window. For gh
  commands on personal repos: `$env:GH_TOKEN = gh auth token --user talelksiksi; <command>;
  Remove-Item Env:GH_TOKEN`. (Talel, steps from Claude.)

- [x] 2026-11-12: Git identity split. `C:\Users\talel\.gitconfig` now includes
  `~/.gitconfig-personal` for every repo under `source/personal/`, so commits there use the
  noreply identity. Backup of the previous file: `.gitconfig.backup-2026-11-12`. (Done by Claude.)
- [x] 2026-11-12: `get-latest.cmd` updated to also clone and update `cictna-industrial`. (Done by Claude.)
- [x] 2026-11-12: Private repository `talelksiksi/cictna-industrial` created. (Talel.)
- [x] 2026-11-12: `api.nuget.org` allowed in claude.ai capabilities, so the cloud workspace can
  restore NuGet packages. (Talel.)
