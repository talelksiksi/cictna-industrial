# Steps on Talel's PC

Everything that has to happen on the Windows PC (`talel-pc-new`) and cannot be done from the
cloud workspace. Tick items as they are done and commit the change, so this file is always
the current state. Newest open items first. Done items stay below with their date.

Legend: `[ ]` to do, `[x]` done.

---

## Open

### 5. Prosys interop (closes Phase 1)
Phase 1's exit criteria include the Prosys OPC UA Simulation Server, which is installed on the PC.
- [ ] Get the latest code and build the native library locally (once, a few minutes), in a normal
  PowerShell:
  ```powershell
  cd C:\Users\talel\source\personal\cictna-industrial
  git pull
  powershell -ExecutionPolicy Bypass -File native\build.ps1
  ```
  It ends with `== done: ...\native\out\win-x64\cictna_ua.dll`.
- [ ] Start the Prosys OPC UA Simulation Server. Note its endpoint URL from the Status tab, usually
  `opc.tcp://<pc-name>:53530/OPCUA/SimulationServer`.
- [ ] In PowerShell, in the repo folder:
  ```powershell
  $env:CICTNA_PROSYS_URL = "opc.tcp://localhost:53530/OPCUA/SimulationServer"
  dotnet test --project tests\Interop\Cictna.Industrial.OpcUa.InteropTests -c Release
  ```
- [ ] The secure-channel test fails on the first run: Prosys has not trusted our client certificate
  yet. In Prosys, open the Certificates tab, find "Cictna Interop Client", choose Trust. Run the
  command again: all 5 Prosys tests should pass (the certificate is kept in
  `%TEMP%\cictna-interop`, so trusting once is enough).
- [ ] Tell Claude the result (or paste the summary). Claude then writes the Phase 1 build record
  with the Windows and Prosys results and closes the phase.

---

## Optional

- [ ] Self-hosted Linux runner instead of GitHub-hosted Ubuntu: only if CI minutes ever become a
  concern. Set the repository variable `LINUX_RUNS_ON` to `["self-hosted","linux","x64"]` after
  installing a Linux runner (WSL or a spare machine).

---

## Done

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
