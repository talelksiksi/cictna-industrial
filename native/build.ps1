<#
.SYNOPSIS
  Builds the native OPC UA library for Windows x64:
  mbedTLS (static) + open62541 (static) + our shim, linked into native\out\win-x64\cictna_ua.dll.

.DESCRIPTION
  Requires Git, CMake 3.20 or newer, and Visual Studio 2022 or newer (or the Build Tools)
  with the "Desktop development with C++" workload. Runs in Windows PowerShell 5.1 and PowerShell 7.
  The DLL links the C runtime statically (/MT), so client machines need no VC++ redistributable.

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
Invoke-Checked cmake (@('-S', (Join-Path $deps 'mbedtls'), '-B', (Join-Path $work 'mbedtls')) + $common + @(
    '-DENABLE_TESTING=OFF',
    '-DENABLE_PROGRAMS=OFF',
    '-DUSE_STATIC_MBEDTLS_LIBRARY=ON',
    '-DUSE_SHARED_MBEDTLS_LIBRARY=OFF',
    '-DMBEDTLS_FATAL_WARNINGS=OFF'))
Invoke-Checked cmake @('--build', (Join-Path $work 'mbedtls'), '--config', 'Release', '--parallel')
Invoke-Checked cmake @('--install', (Join-Path $work 'mbedtls'), '--config', 'Release')

Write-Host "== building open62541 $($v.OPEN62541_TAG) for $Rid"
Invoke-Checked cmake (@('-S', (Join-Path $deps 'open62541'), '-B', (Join-Path $work 'open62541')) + $common + @(
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
    '-DUA_FORCE_WERROR=OFF'))
Invoke-Checked cmake @('--build', (Join-Path $work 'open62541'), '--config', 'Release', '--parallel')
Invoke-Checked cmake @('--install', (Join-Path $work 'open62541'), '--config', 'Release')

Write-Host "== building shim for $Rid"
$outCm = $out -replace '\\', '/'
Invoke-Checked cmake (@('-S', (Join-Path $here 'shim'), '-B', (Join-Path $work 'shim')) + $common + @(
    "-DCMAKE_PREFIX_PATH=$prefixCm",
    "-DCICTNA_UA_OUT_DIR=$outCm"))
Invoke-Checked cmake @('--build', (Join-Path $work 'shim'), '--config', 'Release', '--parallel')

$dll = Join-Path $out 'cictna_ua.dll'
if (-not (Test-Path $dll)) { throw "build finished but $dll is missing" }
Write-Host "== done: $dll"
