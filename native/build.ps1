<#
.SYNOPSIS
  Builds the native OPC UA library for Windows x64:
  mbedTLS (static) + open62541 (static) + our shim, linked into native\out\win-x64\cictna_ua.dll.

.DESCRIPTION
  Windows counterpart of native/build.sh; the two must use the same open62541 options.
  Design notes: docs/native-layer.md.

  Requires Git, CMake 3.20 or newer, and Visual Studio 2022 or newer (or the Build Tools)
  with the "Desktop development with C++" workload. Runs in Windows PowerShell 5.1 and PowerShell 7.

  Output in native\out\win-x64\:
    cictna_ua.dll    mbedTLS + open62541 + our shim, one self-contained DLL
    build-info.txt   versions, compiler, options and SHA-256 (read by the build records)

  Choices specific to Windows:
   - The Visual Studio generator (-A x64) is used rather than Ninja, so the script works from a
     plain PowerShell without a "Developer Command Prompt" environment.
   - The C runtime is linked statically (/MT, CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded), so client
     machines need no Visual C++ redistributable. CMP0091=NEW is forced because older
     cmake_minimum_required() calls in the dependencies would otherwise ignore that setting.
   - Sources are pinned by tag AND commit (versions.env); a mismatch stops the build.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File native\build.ps1
  powershell -ExecutionPolicy Bypass -File native\build.ps1 -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Rid = 'win-x64',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = $PSScriptRoot

# Read native\versions.env (KEY=VALUE lines, # comments).
$v = @{}
foreach ($line in Get-Content (Join-Path $here 'versions.env')) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $v[$Matches[1]] = $Matches[2].Trim() }
}

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    # Native tools write warnings to stderr; in Windows PowerShell 5.1 that can surface as a
    # terminating error under 'Stop'. Judge success by the exit code only.
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $File @Arguments } finally { $ErrorActionPreference = $saved }
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# CMake: use the one on PATH, else the standard install folders, else the copy that ships with
# Visual Studio's C++ workload. A Windows service (the CI runner) does not see PATH changes made
# after it started, so relying on PATH alone fails right after installing CMake.
function Find-CMake {
    $onPath = Get-Command cmake -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $candidates = @(
        (Join-Path $env:ProgramFiles 'CMake\bin\cmake.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'CMake\bin\cmake.exe')
    )
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -property installationPath
        if ($vs) { $candidates += Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' }
    }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    throw 'CMake not found. Install it from cmake.org (option: add CMake to the system PATH) or add the "C++ CMake tools for Windows" component in the Visual Studio Installer.'
}
$cmake = Find-CMake
Write-Host "== using CMake: $cmake"

# Python 3: open62541 generates its type and node code with it. Same idea as CMake: PATH first
# (skipping the Microsoft Store stub in WindowsApps), then the py launcher, then the all-users
# install folders. The CI runner service only sees all-users installs (docs/pc-steps.md).
function Find-Python {
    foreach ($name in @('python', 'python3')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd -and $cmd.Source -notlike '*\WindowsApps\*') { return $cmd.Source }
    }
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        $exe = & $py.Source -3 -c "import sys; print(sys.executable)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $exe -and (Test-Path $exe.Trim())) { return $exe.Trim() }
    }
    $installed = Get-ChildItem -Path (Join-Path $env:ProgramFiles 'Python3*\python.exe') -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($installed) { return $installed.FullName }
    throw 'Python 3 not found. Install it for all users: winget install --id Python.Python.3.12 -e --scope machine (admin PowerShell).'
}
$python = Find-Python
Write-Host "== using Python: $python"

$deps   = Join-Path $here '.deps'
$work   = Join-Path $here ".build\$Rid"
$prefix = Join-Path $work 'prefix'
$out    = Join-Path $here "out\$Rid"

if ($Clean) {
    foreach ($d in @($work, $out)) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
}
foreach ($d in @($deps, $work, $out)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

# Clone a pinned tag and refuse to continue if the commit is not the expected one.
function Get-Pinned {
    param([string]$Name, [string]$Url, [string]$Tag, [string]$Sha)
    $dir = Join-Path $deps $Name
    if (Test-Path (Join-Path $dir '.git')) {
        $head = (& git -C $dir rev-parse HEAD).Trim()
        if ($head -eq $Sha) { return }
        Remove-Item -Recurse -Force $dir
    }
    Write-Host "== fetching $Name $Tag"
    Invoke-Checked git @('-c', 'advice.detachedHead=false', 'clone', '--quiet', '--depth', '1', '--branch', $Tag, $Url, $dir)
    $got = (& git -C $dir rev-parse HEAD).Trim()
    if ($got -ne $Sha) { throw "$Name $Tag is $got, expected $Sha" }
}

Get-Pinned 'open62541' $v.OPEN62541_URL $v.OPEN62541_TAG $v.OPEN62541_SHA
Get-Pinned 'mbedtls'   $v.MBEDTLS_URL   $v.MBEDTLS_TAG   $v.MBEDTLS_SHA
Invoke-Checked git @('-C', (Join-Path $deps 'mbedtls'), 'submodule', 'update', '--quiet', '--init', '--depth', '1', 'framework')
$fw = (& git -C (Join-Path $deps 'mbedtls\framework') rev-parse HEAD).Trim()
if ($fw -ne $v.MBEDTLS_FRAMEWORK_SHA) { throw "mbedtls framework is $fw, expected $($v.MBEDTLS_FRAMEWORK_SHA)" }

# Forward slashes keep CMake happy with Windows paths.
$prefixCm = $prefix -replace '\\', '/'
$common = @(
    '-A', 'x64',
    "-DCMAKE_INSTALL_PREFIX=$prefixCm",
    '-DCMAKE_INSTALL_LIBDIR=lib',
    '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW',
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded'
)

Write-Host "== building mbedTLS $($v.MBEDTLS_TAG) for $Rid"
Invoke-Checked $cmake (@('-S', (Join-Path $deps 'mbedtls'), '-B', (Join-Path $work 'mbedtls')) + $common + @(
    '-DENABLE_TESTING=OFF',
    '-DENABLE_PROGRAMS=OFF',
    '-DUSE_STATIC_MBEDTLS_LIBRARY=ON',
    '-DUSE_SHARED_MBEDTLS_LIBRARY=OFF',
    '-DMBEDTLS_FATAL_WARNINGS=OFF'))
Invoke-Checked $cmake @('--build', (Join-Path $work 'mbedtls'), '--config', 'Release', '--parallel')
Invoke-Checked $cmake @('--install', (Join-Path $work 'mbedtls'), '--config', 'Release')

Write-Host "== building open62541 $($v.OPEN62541_TAG) for $Rid"
# Options are explained one by one in native/build.sh; keep both lists identical.
Invoke-Checked $cmake (@('-S', (Join-Path $deps 'open62541'), '-B', (Join-Path $work 'open62541')) + $common + @(
    '-DBUILD_SHARED_LIBS=OFF',
    '-DUA_ENABLE_ENCRYPTION=MBEDTLS',
    "-DMBEDTLS_FOLDER_INCLUDE=$prefixCm/include",
    "-DMBEDTLS_FOLDER_LIBRARY=$prefixCm/lib",
    '-DUA_MULTITHREADING=100',
    '-DUA_NAMESPACE_ZERO=REDUCED',
    '-DUA_ENABLE_PUBSUB=OFF',
    '-DUA_ENABLE_DISCOVERY_MULTICAST_MDNSD=OFF',
    '-DUA_ENABLE_MQTT=OFF',
    '-DUA_ENABLE_NODESETLOADER=OFF',
    '-DUA_LOGLEVEL=300',
    '-DUA_BUILD_EXAMPLES=OFF',
    '-DUA_BUILD_UNIT_TESTS=OFF',
    '-DUA_FORCE_WERROR=OFF',
    "-DPython3_EXECUTABLE=$($python -replace '\\', '/')"))
Invoke-Checked $cmake @('--build', (Join-Path $work 'open62541'), '--config', 'Release', '--parallel')
Invoke-Checked $cmake @('--install', (Join-Path $work 'open62541'), '--config', 'Release')

Write-Host "== building shim for $Rid"
$outCm = $out -replace '\\', '/'
Invoke-Checked $cmake (@('-S', (Join-Path $here 'shim'), '-B', (Join-Path $work 'shim')) + $common + @(
    "-DCMAKE_PREFIX_PATH=$prefixCm",
    "-DCICTNA_UA_OUT_DIR=$outCm"))
Invoke-Checked $cmake @('--build', (Join-Path $work 'shim'), '--config', 'Release', '--parallel')

$dll = Join-Path $out 'cictna_ua.dll'
if (-not (Test-Path $dll)) { throw "build finished but $dll is missing" }

# ---- build-info.txt -------------------------------------------------------------
# Same "key: value" format as build.sh. The compiler is read from the file CMake writes
# when it detects the compiler, so it records what was really used.
$ccFile = Get-ChildItem (Join-Path $work 'shim\CMakeFiles') -Recurse -Filter 'CMakeCCompiler.cmake' | Select-Object -First 1
$ccId = ''; $ccVersion = ''
foreach ($line in Get-Content $ccFile.FullName) {
    if ($line -match '^set\(CMAKE_C_COMPILER_ID "(.*)"\)$') { $ccId = $Matches[1] }
    if ($line -match '^set\(CMAKE_C_COMPILER_VERSION "(.*)"\)$') { $ccVersion = $Matches[1] }
}
$repoRoot = Split-Path $here -Parent
$saved = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$repoCommit = (& git -C $repoRoot rev-parse --short HEAD 2>$null)
$repoDirty = if (& git -C $repoRoot status --porcelain 2>$null) { 'yes' } else { 'no' }
$ErrorActionPreference = $saved
if (-not $repoCommit) { $repoCommit = 'unknown' }
# Read all lines, then take the first: piping into Select-Object -First 1 stops the pipeline
# early, which can kill cmake mid-write and leave a non-zero $LASTEXITCODE that fails the CI step
# after a successful build (seen once in 20 runs).
$cmakeVersion = (@(& $cmake --version)[0] -split ' ')[2]
$item = Get-Item $dll
$info = @(
    "rid: $Rid",
    "built_utc: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))",
    "repo_commit: $repoCommit",
    "repo_dirty: $repoDirty",
    "open62541: $($v.OPEN62541_TAG) ($($v.OPEN62541_SHA))",
    "mbedtls: $($v.MBEDTLS_TAG) ($($v.MBEDTLS_SHA))",
    "compiler: $ccId $ccVersion",
    "cmake: $cmakeVersion",
    'options: encryption=mbedtls multithreading=100 ns0=reduced pubsub=off loglevel=300 crt=static',
    "file: $($item.Name)",
    "size_bytes: $($item.Length)",
    "sha256: $((Get-FileHash -Algorithm SHA256 $dll).Hash.ToLowerInvariant())"
)
# ASCII without BOM so the bash record script reads it the same way as the Linux file.
[System.IO.File]::WriteAllLines((Join-Path $out 'build-info.txt'), $info, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "== done: $dll"

# Every native call above was checked; do not let a stray $LASTEXITCODE decide the step's result.
exit 0
