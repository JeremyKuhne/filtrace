#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Runs the test suite elevated so the tests that need Administrator - the ETW capture
  scenarios - actually execute instead of reporting inconclusive.

.DESCRIPTION
  Some tests only run on Windows with Administrator and mark themselves inconclusive
  otherwise (notably the `collect` verb's real ETW capture,
  CollectExecutorTests.Run_WhenElevated_ProducesEtl). This helper builds in the
  normal-user context - so build artifacts stay user-owned - then relaunches itself
  elevated to run `dotnet test`, teeing the elevated window's output to a log the calling
  console surfaces on completion. It is a developer convenience for validating elevated
  scenarios locally, not part of CI.

  Run it from the repository root (the folder holding filtrace.slnx). A single UAC prompt
  appears; the elevated run's exit code becomes this script's exit code.

.PARAMETER Configuration
  The build configuration to build and test. Defaults to Release.

.PARAMETER Project
  The test project (or filtrace.slnx) to run. Defaults to the CLI test project, which
  holds the elevated ETW capture test.

.PARAMETER Filter
  An optional test filter passed through to `dotnet test --filter`; omit to run them all.

.PARAMETER SkipBuild
  Skip the build and test the existing binaries. Set automatically for the elevated
  relaunch (which reuses the build produced in the normal-user context).

.PARAMETER LogFile
  Where the elevated run tees its output so the calling console can surface it. Set
  automatically by the relaunch; you do not normally pass this.

.EXAMPLE
  ./tools/Invoke-ElevatedTests.ps1

.EXAMPLE
  ./tools/Invoke-ElevatedTests.ps1 -Filter Run_WhenElevated_ProducesEtl

.EXAMPLE
  ./tools/Invoke-ElevatedTests.ps1 -Project filtrace.slnx
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Project = 'tests/Filtrace.Cli.Tests/Filtrace.Cli.Tests.csproj',
    [string]$Filter = '',
    [switch]$SkipBuild,
    [string]$LogFile = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# ETW kernel tracing (what the elevated tests exercise) is Windows-only. Compare against
# $false so Windows PowerShell 5.1 (where $IsWindows is undefined) is not mistaken for a
# non-Windows OS.
if ($IsWindows -eq $false) {
    Write-Error 'Elevated ETW tests are Windows-only; nothing to run on this OS.' -ErrorAction Continue
    exit 1
}

# Resolve the project (or solution) to an absolute path so it survives the elevated
# relaunch regardless of the working directory.
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $repoRoot $Project }
if (-not (Test-Path -LiteralPath $projectPath)) {
    Write-Error "Test project not found: $projectPath" -ErrorAction Continue
    exit 1
}
$projectPath = (Resolve-Path -LiteralPath $projectPath).Path

function Test-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Not elevated yet: build here (keeping artifacts user-owned), then relaunch elevated to
# run only the tests. -WorkingDirectory anchors the child at the repo root.
if (-not (Test-Elevated)) {
    if (-not $SkipBuild) {
        Write-Host "Building $projectPath ($Configuration)..." -ForegroundColor Cyan
        dotnet build $projectPath -c $Configuration | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Error "Build failed (exit $LASTEXITCODE)." -ErrorAction Continue ; exit $LASTEXITCODE }
    }

    $log = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace-elevated-tests-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
    Write-Host 'Elevating to run the tests (a UAC prompt will appear)...' -ForegroundColor Yellow

    # The elevated child reuses this build (-SkipBuild) and tees its output to the log so
    # this console can surface it after the separate elevated window closes. Quote the
    # path/value args so a repo or temp path containing spaces survives Start-Process
    # joining the array into a single command line.
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
        '-Configuration', "`"$Configuration`"", '-Project', "`"$projectPath`"", '-SkipBuild', '-LogFile', "`"$log`"")
    if ($Filter) { $argList += @('-Filter', "`"$Filter`"") }

    $proc = Start-Process pwsh -Verb RunAs -PassThru -Wait -WorkingDirectory $repoRoot -ArgumentList $argList

    if (Test-Path $log) {
        Write-Host "`n--- elevated test output ($log) ---" -ForegroundColor Cyan
        Get-Content $log
    }

    if ($proc.ExitCode -ne 0) {
        Write-Error "Elevated tests failed (exit $($proc.ExitCode)). See $log." -ErrorAction Continue
        exit $proc.ExitCode
    }

    Write-Host "`nElevated tests passed." -ForegroundColor Green
    exit 0
}

# Elevated from here: build unless the caller already did, then run the tests without
# rebuilding. Tee to the log (when relaunched) so the elevated window shows live progress
# and the parent console can print the same output.
if (-not $SkipBuild) {
    Write-Host "Building $projectPath ($Configuration)..." -ForegroundColor Cyan
    dotnet build $projectPath -c $Configuration | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$testArgs = @('test', $projectPath, '-c', $Configuration, '--no-build', '--no-restore')
if ($Filter) { $testArgs += @('--filter', $Filter) }

Write-Host "Running elevated: dotnet $($testArgs -join ' ')" -ForegroundColor Cyan
if ($LogFile) {
    dotnet @testArgs 2>&1 | Tee-Object -FilePath $LogFile
}
else {
    dotnet @testArgs
}
exit $LASTEXITCODE
