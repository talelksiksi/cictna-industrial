# Steps on Talel's PC

Everything that has to happen on the Windows PC (`talel-pc-new`) and cannot be done from the
cloud workspace. Tick items as they are done and commit the change, so this file is always
the current state. Newest open items first. Done items stay below with their date.

Legend: `[ ]` to do, `[x]` done.

---

## Open

### 1. Get the repository
- [ ] Run `C:\Users\talel\source\personal\get-latest.cmd`.
  It now includes `cictna-industrial` and clones it to `C:\Users\talel\source\personal\cictna-industrial`.
- [ ] Inside that folder, run `git config user.email`.
  Expected: `6784031+talelksiksi@users.noreply.github.com`.
  If it shows the work address, stop and tell Claude before committing anything.

### 2. Check the build tools
Open a new PowerShell and run each line. The expected result is on the right.

| Command | Expected |
|---|---|
| `dotnet --list-sdks` | a `10.0.x` line (10.0.103 and 10.0.201 were seen on 12 Nov) |
| `git --version` | any recent version |
| `cmake --version` | 3.20 or newer. If missing: install from cmake.org, tick "Add CMake to the system PATH" |
| `gh auth status` | "Logged in to github.com account talelksiksi" |

- [ ] Visual Studio Installer: Modify your Visual Studio (or Build Tools) and make sure the
  workload **Desktop development with C++** is ticked. The native library is built with MSVC.

### 3. Install the CI runner (once)
- [ ] Open PowerShell **as administrator**, then:
  ```powershell
  cd C:\Users\talel\source\personal\cictna-industrial
  powershell -ExecutionPolicy Bypass -File scripts\setup-runner-windows.ps1
  ```
  The script checks step 2 again, verifies the runner download's checksum, registers the
  runner for this repository only and installs it as a Windows service (it starts with the PC).
  It installs to `C:\ar\cictna` (short path on purpose, Windows has a 260-character path limit).
- [ ] Check on GitHub: repository > Settings > Actions > Runners shows `TALEL-PC-NEW-cictna` as Idle.

### 4. First Windows build (closes Phase 0 and the Windows half of Phase 1)
- [ ] Optional but recommended: install Node.js 22 LTS (nodejs.org), so the node-opcua interop
  tests also run locally. Without it they are skipped locally; CI installs Node.js itself.
- [ ] Locally, in the repo folder:
  ```powershell
  powershell -ExecutionPolicy Bypass -File native\build.ps1
  dotnet test -c Release
  dotnet run --project tests\Smoke -c Release
  ```
  Expected: `native\out\win-x64\cictna_ua.dll`, then `failed: 0` (105 passed with Node.js,
  5 Prosys tests skipped until step 5), then `SMOKE OK`.
- [ ] On GitHub: Actions > CI > Run workflow (or `gh workflow run CI -R talelksiksi/cictna-industrial`).
  All jobs green closes Phase 0. If something fails, tell Claude; it reads the log and fixes it.

### 5. Prosys interop (closes Phase 1)
Phase 1's exit criteria include the Prosys OPC UA Simulation Server, which is installed on the PC.
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

- [x] 2026-11-12: Git identity split. `C:\Users\talel\.gitconfig` now includes
  `~/.gitconfig-personal` for every repo under `source/personal/`, so commits there use the
  noreply identity. Backup of the previous file: `.gitconfig.backup-2026-11-12`. (Done by Claude.)
- [x] 2026-11-12: `get-latest.cmd` updated to also clone and update `cictna-industrial`. (Done by Claude.)
- [x] 2026-11-12: Private repository `talelksiksi/cictna-industrial` created. (Talel.)
- [x] 2026-11-12: `api.nuget.org` allowed in claude.ai capabilities, so the cloud workspace can
  restore NuGet packages. (Talel.)
