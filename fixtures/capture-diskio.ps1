<#
.SYNOPSIS
    Captures the disk I/O ETW (.etl) fixture for the disk-I/O provider.

.DESCRIPTION
    Captures a disk I/O ETW trace in two steps and writes the result into the core
    test fixtures as `diskio.etl`:

      1. HotLoopBench's `capture-disk` command opens a tight kernel ETW session
         (DiskIO kernel keywords plus the context-switch keywords the ThreadTime
         view needs) around a small inline file-I/O workload.
      2. HotLoopBench's `trim` command relogs that raw capture down to just the
         HotLoopBench process tree - including its own disk I/O, correlated back to
         the issuing thread by IRP - which drops the machine-wide disk traffic and,
         above all, the system-wide file-name rundown that the DiskFileIO keyword
         triggers (a rundown of every open file object, hundreds of thousands of
         events, that dominates the raw trace regardless of the capture window).

    This is separate from capture-etw.ps1 (which captures the CPU / thread-time
    `etw.etl`) so the shared thread-time fixture stays untouched.

.NOTES
    Run from an administrator terminal on a Windows machine with the .NET 10
    SDK. ETW kernel tracing requires elevation.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$fixturesRoot = $PSScriptRoot
$benchProject = Join-Path $fixturesRoot 'HotLoopBench'
$coreFixtures = Join-Path $fixturesRoot '../tests/Filtrace.Core.Tests/Fixtures'

# ETW kernel tracing requires administrator rights; fail fast with a clear
# message rather than letting the capture session fail to start.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator))
{
    throw 'Not elevated: ETW capture needs an administrator terminal. Re-run this script elevated.'
}

$fixtureEtl = Join-Path $coreFixtures 'diskio.etl'
$rawEtl = Join-Path ([System.IO.Path]::GetTempPath()) 'filtrace-diskio-raw.etl'
$apphost = Join-Path $benchProject 'bin/Release/net10.0/HotLoopBench.exe'

# Prefer the Program Files runtime so a stray DOTNET_ROOT / PATH order does not break
# the apphost's runtime resolution in the elevated session.
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:Path = 'C:\Program Files\dotnet;' + $env:Path

Write-Host 'Building HotLoopBench (net10.0)...'
Push-Location $benchProject
try
{
    dotnet build -c Release -f net10.0 | Out-Host
    if ($LASTEXITCODE -ne 0)
    {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}
finally
{
    Pop-Location
}

if (-not (Test-Path $apphost))
{
    throw "Apphost not found: $apphost"
}

# 1. Capture: the apphost opens a tight kernel session around an inline file-I/O
#    workload. The session is open only for the sub-second workload, but ETW kernel
#    tracing is machine-wide, so the DiskFileIO file-name rundown still makes the raw
#    trace large - hence the trim below. Running the apphost (rather than `dotnet run`)
#    gives the capturing process a deterministic name to trim to.
Write-Host 'Capturing the raw disk I/O ETW trace (elevated)...'
& $apphost capture-disk $rawEtl | Out-Host
if ($LASTEXITCODE -ne 0)
{
    throw "Disk I/O capture failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path $rawEtl))
{
    throw "No raw disk I/O .etl was produced at $rawEtl."
}
Write-Host "Raw capture: $([math]::Round((Get-Item $rawEtl).Length / 1KB)) KB"

# 2. Trim to just the HotLoopBench process tree's events (including its disk I/O,
#    correlated back to the issuing thread by IRP), dropping the machine-wide disk
#    traffic and the system-wide file-name rundown that dominate the raw capture.
Write-Host 'Trimming to the HotLoopBench process tree...'
& $apphost trim $rawEtl $fixtureEtl HotLoopBench | Out-Host
if ($LASTEXITCODE -ne 0)
{
    throw "Trim failed with exit code $LASTEXITCODE."
}

Remove-Item $rawEtl -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $fixtureEtl))
{
    throw "No trimmed disk I/O .etl was produced at $fixtureEtl."
}
Write-Host "Disk I/O fixture -> $fixtureEtl ($([math]::Round((Get-Item $fixtureEtl).Length / 1KB)) KB)"

Write-Host 'Done.'
