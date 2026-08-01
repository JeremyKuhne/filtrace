#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Proves that `--symbols` applies a local PDB to a native module, end to end.

.DESCRIPTION
  Builds the NativeLoop C++ fixture, captures a CPU trace of it, and checks that its
  frames resolve to method names only when the symbol directory is supplied.

  This runs as a gate rather than against a committed fixture because a filtrace
  capture records no PDB identity of its own - cross-machine symbol injection (the
  PerfView "merge" step) is a documented follow-up in EtwCollector. Until that exists,
  TraceEvent resolves a native module by reading the binary back from the absolute path
  recorded in the trace, so a committed capture only resolves on the machine that took
  it. Capturing and checking in the same job sidesteps that entirely.

  A plain C++ binary is the subject on purpose: it carries no CLR rundown, so its
  frames are unresolved until its PDB is loaded, and it builds in seconds - unlike a
  Native AOT publish, which needs the ILC toolchain and minutes of compilation.

.PARAMETER Configuration
  The build configuration whose filtrace binary is exercised. Defaults to Release.

.PARAMETER Iterations
  Workload size. The default runs for roughly two and a half seconds, which clears the
  200-sample threshold filtrace treats as the minimum for a directional result.

.PARAMETER Require
  Fail instead of skipping when the environment cannot capture. CI passes this so a
  runner that silently lost Administrator turns into a red check rather than a quiet
  pass.

.NOTES
  Windows-only and needs Administrator for ETW kernel tracing. GitHub-hosted Windows
  runners already run elevated, so this is a normal CI step there.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidateRange(1, [int]::MaxValue)][int]$Iterations = 500000,
    [switch]$Require
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot 'fixtures/NativeLoop'
$source = Join-Path $fixture 'NativeLoop.cpp'
$executable = Join-Path $fixture 'NativeLoop.exe'
$filtrace = Join-Path $repoRoot "src/Filtrace/bin/$Configuration/net10.0/filtrace.exe"

function Skip-Or-Fail([string]$reason) {
    if ($Require) {
        Write-Error "Native symbol gate cannot run: $reason" -ErrorAction Continue
        exit 1
    }

    Write-Host "Skipping the native symbol gate: $reason" -ForegroundColor Yellow
    exit 0
}

# Compare against $false so Windows PowerShell 5.1, where $IsWindows is undefined, is
# not mistaken for a non-Windows OS.
if ($IsWindows -eq $false) {
    Skip-Or-Fail 'ETW capture is Windows-only.'
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Skip-Or-Fail 'ETW kernel tracing needs Administrator.'
}

if (-not (Test-Path $filtrace)) {
    Skip-Or-Fail "filtrace has not been built at $filtrace."
}

# Locate the C++ toolchain. The runner images carry the VC tools component, but a
# developer box may not, so a missing toolchain is a skip rather than a failure.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path $vswhere)) {
    Skip-Or-Fail 'vswhere.exe not found; no Visual Studio installation to build the C++ fixture with.'
}

$visualStudio = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) {
    Skip-Or-Fail 'No Visual Studio installation with the C++ tools component.'
}

$toolset = Get-ChildItem (Join-Path $visualStudio 'VC/Tools/MSVC') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
if (-not (Test-Path $compiler)) {
    Skip-Or-Fail "cl.exe not found under $($toolset.FullName)."
}

$windowsKitRoot = 'C:/Program Files (x86)/Windows Kits/10'
if (-not (Test-Path $windowsKitRoot)) {
    Skip-Or-Fail "No Windows 10 SDK at $windowsKitRoot; the C++ fixture cannot be compiled."
}

# Stop-preference would turn a missing or differently-shaped SDK layout into an
# unhandled error, which loses the reason this gate could not run.
$windowsKitInclude = Get-ChildItem (Join-Path $windowsKitRoot 'Include') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $windowsKitInclude) {
    Skip-Or-Fail "No SDK version directory under $windowsKitRoot/Include."
}

$windowsKitLib = Join-Path $windowsKitRoot "Lib/$($windowsKitInclude.Name)"
if (-not (Test-Path $windowsKitLib)) {
    Skip-Or-Fail "No matching SDK libraries at $windowsKitLib."
}

Write-Host "Building the NativeLoop fixture with $($toolset.Name)..."
$env:INCLUDE = @(
    (Join-Path $toolset.FullName 'include'),
    (Join-Path $windowsKitInclude.FullName 'ucrt'),
    (Join-Path $windowsKitInclude.FullName 'shared'),
    (Join-Path $windowsKitInclude.FullName 'um')) -join ';'
$env:LIB = @(
    (Join-Path $toolset.FullName 'lib/x64'),
    "$windowsKitLib/ucrt/x64",
    "$windowsKitLib/um/x64") -join ';'

Push-Location $fixture
try {
    # Build from clean. Incremental link state left by an earlier run makes the PDB the
    # executable ends up referencing order-dependent, which is exactly the variable this
    # gate must not have.
    Remove-Item (Join-Path $fixture 'NativeLoop.exe'),
        (Join-Path $fixture 'NativeLoop.pdb'),
        (Join-Path $fixture 'NativeLoop.ilk'),
        (Join-Path $fixture 'NativeLoop.obj') -Force -ErrorAction SilentlyContinue

    # /Zi writes the separate PDB this gate exists to exercise, and the linker is what
    # names it - do not point /Fd at the same file, which would have the compiler write
    # its own object-level PDB over the linker's and leave the executable referencing a
    # PDB with no public symbols in it.
    & $compiler /nologo /O2 /Zi /EHsc "/Fe:$executable" $source /link /INCREMENTAL:NO "/PDB:$(Join-Path $fixture 'NativeLoop.pdb')" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "cl.exe failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path $executable)) {
    throw "No executable was produced at $executable."
}

$trace = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace-native-symbols-$([guid]::NewGuid().ToString('N')).etl"
try {
    Write-Host "Capturing a CPU trace of NativeLoop ($Iterations iterations)..."
    & $filtrace collect --launch $executable --output $trace --profile cpu --launch-args "--iterations $Iterations" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Capture failed with exit code $LASTEXITCODE."
    }

    # The capture is machine-wide, so scope the analysis rather than physically trimming:
    # process scoping is lossless and this trace is discarded at the end anyway.
    $withSymbols = (& $filtrace cpu $trace --symbols $fixture --process NativeLoop | Out-String)
    $withoutSymbols = (& $filtrace cpu $trace --process NativeLoop | Out-String)

    if ($withSymbols -notmatch 'ComputeChecksum') {
        Write-Host $withSymbols
        throw 'A local PDB was supplied but the native frames did not resolve to method names.'
    }

    # The negative control. Without it a build that resolved these names some other way -
    # a symbol server, a cached PDB - would look like a passing gate.
    if ($withoutSymbols -match 'ComputeChecksum') {
        Write-Host $withoutSymbols
        throw 'Native frames resolved without a symbol directory; the check proves nothing.'
    }

    Write-Host 'Native symbol resolution gate passed.' -ForegroundColor Green
}
finally {
    Remove-Item $trace -Force -ErrorAction SilentlyContinue
    Remove-Item ([System.IO.Path]::ChangeExtension($trace, '.etlx')) -Force -ErrorAction SilentlyContinue
}
