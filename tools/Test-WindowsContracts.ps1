#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Runs Windows fake-driven contracts while the fake-agent gate executes.

.DESCRIPTION
  Starts the isolated fake-agent gate first, then runs independent fake-driven
  contracts in separate PowerShell hosts. The real .NET and native ETW gates
  must finish before invoking this script. Any child failure fails this gate.

.PARAMETER FakeTimeoutSeconds
  Maximum lifetime of the owned fake-agent child. Defaults to 600 seconds.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 1200)][int] $FakeTimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'The Windows contract overlap requires Windows PowerShell 5.1.'
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$windowsPowerShell = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop |
    Select-Object -First 1).Source
$fakeScript = Join-Path $PSScriptRoot 'Test-AgentEval.ps1'
$steps = @(
    [pscustomobject]@{ name = 'Capture helper contract (PowerShell 7)'; executable = $pwsh; path = 'tools\Test-CaptureBenchmarkTrace.ps1'; arguments = @() }
    [pscustomobject]@{ name = 'Capture helper contract (Windows PowerShell 5.1)'; executable = $windowsPowerShell; path = 'tools\Test-CaptureBenchmarkTrace.ps1'; arguments = @() }
    [pscustomobject]@{ name = 'Project capture contract'; executable = $pwsh; path = 'tools\Test-CaptureProjectTrace.ps1'; arguments = @() }
    [pscustomobject]@{ name = 'Command capture native argv contract'; executable = $pwsh; path = 'tools\Test-CaptureCommandTrace.ps1'; arguments = @('-WindowsNativeArgv', '-NativeArgvOnly') }
    [pscustomobject]@{ name = 'Local activation wrapper contract'; executable = $pwsh; path = 'tools\Test-UseLocalFiltrace.ps1'; arguments = @() }
    [pscustomobject]@{ name = 'Eval gate'; executable = $pwsh; path = 'eval\Invoke-Eval.ps1'; arguments = @('-Configuration', 'Release') }
)
$originalPolicy = $env:PSExecutionPolicyPreference
$fake = [System.Diagnostics.Process]::new()
[bool] $started = $false
$fakeWatch = [System.Diagnostics.Stopwatch]::new()
Push-Location $root
try {
    $env:PSExecutionPolicyPreference = 'Bypass'
    $info = [System.Diagnostics.ProcessStartInfo]::new($pwsh)
    foreach ($argument in @('-NoLogo', '-NoProfile', '-File', $fakeScript)) {
        [void]$info.ArgumentList.Add($argument)
    }
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $fake.StartInfo = $info
    if (-not $fake.Start()) { throw 'Could not launch the owned fake-agent gate.' }
    $started = $true
    $fakeWatch.Start()
    $stdoutTask = $fake.StandardOutput.ReadToEndAsync()
    $stderrTask = $fake.StandardError.ReadToEndAsync()
    Write-Host "Fake-agent contract running as PID $($fake.Id)."

    foreach ($step in $steps) {
        [string] $scriptPath = Join-Path $root $step.path
        [string[]] $arguments = if ($step.executable -eq $windowsPowerShell) {
            $quotedPath = $scriptPath.Replace("'", "''")
            # A narrow child console can wrap an expected diagnostic mid-phrase.
            $command = '$size = $Host.UI.RawUI.BufferSize; $size.Width = 200; ' +
                '$Host.UI.RawUI.BufferSize = $size; & ' + "'" + $quotedPath + "'"
            @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy',
                'Bypass', '-Command', $command)
        }
        else {
            @('-NoLogo', '-NoProfile', '-File', $scriptPath) + @($step.arguments)
        }
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        Write-Host "Starting $($step.name)..."
        & ([string]$step.executable) @arguments
        [int] $exitCode = $LASTEXITCODE
        $watch.Stop()
        if ($exitCode -ne 0) {
            throw "$($step.name) failed with exit code $exitCode."
        }
        Write-Host ("Passed {0} in {1:F2} seconds." -f $step.name,
            $watch.Elapsed.TotalSeconds)
    }

    [long] $remaining = [Math]::Max(0,
        ([long]$FakeTimeoutSeconds * 1000) - $fakeWatch.ElapsedMilliseconds)
    if (-not $fake.WaitForExit([int]$remaining)) {
        throw "Fake-agent contract exceeded its $FakeTimeoutSeconds-second deadline."
    }
    if (-not $stdoutTask.Wait(10000) -or -not $stderrTask.Wait(10000)) {
        throw 'Fake-agent child streams remained open after its process exited.'
    }
    [Console]::Out.Write($stdoutTask.GetAwaiter().GetResult())
    [Console]::Error.Write($stderrTask.GetAwaiter().GetResult())
    if (($fake.ExitTime - $fake.StartTime).TotalSeconds -gt $FakeTimeoutSeconds) {
        throw "Fake-agent contract exceeded its $FakeTimeoutSeconds-second deadline."
    }
    if ($fake.ExitCode -ne 0) {
        throw "Fake-agent contract failed with exit code $($fake.ExitCode)."
    }
    Write-Host ("All Windows contract gates passed in {0:F2} elapsed seconds." -f
        $fakeWatch.Elapsed.TotalSeconds)
}
finally {
    if ($started -and -not $fake.HasExited) {
        $fake.Kill($true)
        if (-not $fake.WaitForExit(10000)) {
            Write-Warning "Owned fake-agent process $($fake.Id) may still be running."
        }
    }
    $fake.Dispose()
    $env:PSExecutionPolicyPreference = $originalPolicy
    Pop-Location
}
