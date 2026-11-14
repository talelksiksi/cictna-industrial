<#
.SYNOPSIS
  Installs a GitHub Actions self-hosted runner for talelksiksi/cictna-industrial on this PC.

.DESCRIPTION
  One step, run once from an elevated PowerShell (Run as administrator):

    powershell -ExecutionPolicy Bypass -File scripts\setup-runner-windows.ps1

  What it does:
   1. Checks the build prerequisites: Git, CMake 3.20+, GitHub CLI (logged in),
      Visual Studio or Build Tools with the C++ workload. Install CMake and Python 3 for ALL
      users: the runner service runs as NETWORK SERVICE and cannot see per-user installs.
   2. Gets a one-time registration token for this repository through the GitHub CLI.
   3. Downloads the latest runner, checks its SHA-256, unpacks it into C:\ar\cictna.
      (A short path on purpose: the native build nests deep and Windows has a 260-character limit.)
   4. Registers the runner for this repository only and installs it as a Windows service
      that runs as NETWORK SERVICE, so it is up whenever the PC is on, with no user logged in.

  The runner only picks up jobs from this repository. It never runs code from forks.
  To remove it: C:\ar\cictna\config.cmd remove --token (gh api -X POST repos/talelksiksi/cictna-industrial/actions/runners/remove-token --jq .token)

.PARAMETER NoService
  Register the runner but do not install the service. Start it by hand with C:\ar\cictna\run.cmd.
#>
[CmdletBinding()]
param(
    [switch]$NoService,
    [string]$Repository = 'talelksiksi/cictna-industrial',
    [string]$InstallDir = 'C:\ar\cictna'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail([string]$Message) { Write-Host "ERROR: $Message" -ForegroundColor Red; exit 1 }
function Ok([string]$Message) { Write-Host "ok   $Message" -ForegroundColor Green }

# --- 1. prerequisites ----------------------------------------------------------
if (-not $NoService) {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Fail 'Installing the service needs an elevated PowerShell (Run as administrator), or pass -NoService.'
    }
}

foreach ($tool in @('git', 'cmake', 'gh')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Fail "$tool is not on PATH. Install it (git-scm.com, cmake.org, cli.github.com) and open a new PowerShell."
    }
}
Ok 'git, cmake and gh found'

$cmakeVersion = [version]((& cmake --version | Select-Object -First 1) -replace '[^0-9.]', '')
if ($cmakeVersion -lt [version]'3.20') { Fail "CMake $cmakeVersion is too old, 3.20 or newer is needed." }
Ok "CMake $cmakeVersion"

& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { Fail 'GitHub CLI is not logged in. Run: gh auth login' }
Ok 'GitHub CLI logged in'

# The PC may keep another account active in gh; this repository uses the talelksiksi account. Use
# its token for this script only (process environment), without switching the active account.
$personalToken = (& gh auth token --user talelksiksi 2>$null)
if ($LASTEXITCODE -eq 0 -and $personalToken) {
    $env:GH_TOKEN = $personalToken.Trim()
    Ok 'Using the talelksiksi account for this script'
} else {
    Fail 'The talelksiksi account is not stored in gh. Run: gh auth login (as talelksiksi), then gh auth switch --user <other account>. See docs/pc-steps.md.'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = if (Test-Path $vswhere) {
    & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property displayName
}
if (-not $vs) {
    Fail ('No Visual Studio with C++ tools found. In the Visual Studio Installer, add the ' +
          '"Desktop development with C++" workload (Build Tools are enough).')
}
Ok "C++ toolchain: $vs"

# --- 2. registration token ------------------------------------------------------
$token = (& gh api -X POST "repos/$Repository/actions/runners/registration-token" --jq .token)
if ($LASTEXITCODE -ne 0 -or -not $token) { Fail "Could not get a registration token for $Repository." }
Ok 'registration token received'

# --- 3. download and verify the runner -----------------------------------------
$release = & gh api repos/actions/runner/releases/latest | ConvertFrom-Json
$asset = $release.assets | Where-Object { $_.name -like 'actions-runner-win-x64-*.zip' } | Select-Object -First 1
if (-not $asset) { Fail 'Could not find the Windows x64 runner in the latest release.' }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if (Test-Path (Join-Path $InstallDir '.runner')) {
    Fail "A runner is already configured in $InstallDir. Remove it first (see the help of this script)."
}
$zip = Join-Path $env:TEMP $asset.name
Write-Host "downloading $($asset.name)"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing

$expected = $null
if ($asset.PSObject.Properties.Name -contains 'digest' -and $asset.digest -like 'sha256:*') {
    $expected = $asset.digest.Substring(7)
} elseif ($release.body -match "(?m)$([regex]::Escape($asset.name))[^0-9a-f]*([0-9a-f]{64})") {
    $expected = $Matches[1]
}
if (-not $expected) { Fail 'Could not find the published SHA-256 for the runner download.' }
$actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
if ($actual -ne $expected.ToLowerInvariant()) { Fail "SHA-256 mismatch for $($asset.name)." }
Ok "runner $($release.tag_name) downloaded, checksum verified"

Expand-Archive -Path $zip -DestinationPath $InstallDir -Force
Remove-Item $zip

# --- 4. register (and install the service) --------------------------------------
$configArgs = @(
    '--unattended',
    '--url', "https://github.com/$Repository",
    '--token', $token,
    '--name', "$env:COMPUTERNAME-cictna",
    '--work', '_work',
    '--replace'
)
if (-not $NoService) { $configArgs += '--runasservice' }

Push-Location $InstallDir
try {
    & .\config.cmd @configArgs
    $configExit = $LASTEXITCODE
} finally {
    Pop-Location
}
if (-not (Test-Path (Join-Path $InstallDir '.runner'))) {
    Fail 'Runner registration failed (see the output above).'
}

if (-not $NoService) {
    # On non-English Windows the runner configures its service with the localized account name
    # (for example "AUTORITE NT\SERVICE RÉSEAU"), and the service then fails to start with
    # error 1068. Re-assign the same account by its language-independent name and start it.
    # (sc.exe cannot be used for this from Windows PowerShell 5.1: it drops the empty password.)
    $service = Get-Service 'actions.runner.*' | Where-Object { $_.Name -like "*$($env:COMPUTERNAME)-cictna" } | Select-Object -First 1
    if (-not $service) { Fail 'The runner service was not created (see the output above).' }
    $result = Get-CimInstance Win32_Service -Filter "Name='$($service.Name)'" |
        Invoke-CimMethod -MethodName Change -Arguments @{ StartName = 'NT AUTHORITY\NetworkService'; StartPassword = '' }
    if ($result.ReturnValue -ne 0) { Fail "Could not set the service account (code $($result.ReturnValue))." }
    try {
        Start-Service $service.Name -ErrorAction Stop
    } catch {
        Fail "The runner is registered but its service does not start: $($_.Exception.Message). See C:\ar\cictna\_diag."
    }
    Ok "Service $($service.Name) running as NetworkService"
} elseif ($configExit -ne 0) {
    Fail 'Runner registration failed (see the output above).'
}

Write-Host ''
Ok "Runner registered for $Repository with labels: self-hosted, Windows, X64"
if ($NoService) {
    Write-Host "Start it when you want CI to run: $InstallDir\run.cmd"
} else {
    Write-Host 'Installed as a Windows service; it starts with the PC.'
}
Write-Host 'Trigger CI: GitHub > cictna-industrial > Actions > CI > Run workflow (or: gh workflow run CI -R talelksiksi/cictna-industrial)'
