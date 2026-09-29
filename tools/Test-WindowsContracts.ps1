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

function Stop-OwnedProcess([System.Diagnostics.Process] $Process) {
    try {
        if (-not $Process.HasExited) { $Process.Kill($true) }
    }
    catch [System.InvalidOperationException] {
        if (-not $Process.HasExited) { throw }
    }
    if (-not $Process.WaitForExit(10000)) {
        throw "Owned process $($Process.Id) did not exit within the cleanup bound."
    }
}

function Assert-FakeWithinDeadline(
    [System.Diagnostics.Process] $Process,
    [System.Diagnostics.Stopwatch] $Watch,
    [int] $TimeoutSeconds) {
    if ($Watch.ElapsedMilliseconds -lt ([long]$TimeoutSeconds * 1000)) { return }
    if (-not $Process.HasExited) {
        Stop-OwnedProcess $Process
        throw "Fake-agent contract exceeded its $TimeoutSeconds-second deadline."
    }
    if (($Process.ExitTime - $Process.StartTime).TotalSeconds -gt $TimeoutSeconds) {
        throw "Fake-agent contract exceeded its $TimeoutSeconds-second deadline."
    }
}

function Invoke-ContractStep(
    $Step,
    [string] $Root,
    [string] $WindowsPowerShell,
    [System.Diagnostics.Process] $Fake,
    [System.Diagnostics.Stopwatch] $FakeWatch,
    [int] $FakeTimeoutSeconds) {
    [string] $scriptPath = Join-Path $Root $Step.path
    [string[]] $arguments = if ($Step.executable -eq $WindowsPowerShell) {
        $quotedPath = $scriptPath.Replace("'", "''")
        # A narrow child console can wrap an expected diagnostic mid-phrase.
        $command = '$size = $Host.UI.RawUI.BufferSize; $size.Width = 200; ' +
            '$Host.UI.RawUI.BufferSize = $size; & ' + "'" + $quotedPath + "'"
        @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy',
            'Bypass', '-Command', $command)
    }
    else {
        @('-NoLogo', '-NoProfile', '-File', $scriptPath) + @($Step.arguments)
    }

    $info = [System.Diagnostics.ProcessStartInfo]::new([string]$Step.executable)
    foreach ($argument in $arguments) { [void]$info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = $Root
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    [bool] $started = $false
    $process.StartInfo = $info
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "Starting $($Step.name)..."
    try {
        if (-not $process.Start()) { throw "Could not start $($Step.name)." }
        $started = $true
        Write-Host "$($Step.name) running as PID $($process.Id)."
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(250)) {
            Assert-FakeWithinDeadline $Fake $FakeWatch $FakeTimeoutSeconds
        }
        Assert-FakeWithinDeadline $Fake $FakeWatch $FakeTimeoutSeconds
        $drain = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not ($stdoutTask.IsCompleted -and $stderrTask.IsCompleted)) {
            Assert-FakeWithinDeadline $Fake $FakeWatch $FakeTimeoutSeconds
            if ($drain.ElapsedMilliseconds -ge 10000) {
                throw "$($Step.name) child streams remained open after exit."
            }
            [System.Threading.Thread]::Sleep(100)
        }
        Assert-FakeWithinDeadline $Fake $FakeWatch $FakeTimeoutSeconds
        [Console]::Out.Write($stdoutTask.GetAwaiter().GetResult())
        [Console]::Error.Write($stderrTask.GetAwaiter().GetResult())
        [int] $exitCode = $process.ExitCode
        if ($exitCode -ne 0) { throw "$($Step.name) failed with exit code $exitCode." }
        $watch.Stop()
        Write-Host ("Passed {0} in {1:F2} seconds." -f $Step.name,
            $watch.Elapsed.TotalSeconds)
    }
    finally {
        try {
            if ($started) { Stop-OwnedProcess $process }
        }
        finally { $process.Dispose() }
    }
}

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
        Invoke-ContractStep $step $root $windowsPowerShell $fake $fakeWatch $FakeTimeoutSeconds
    }

    Assert-FakeWithinDeadline $fake $fakeWatch $FakeTimeoutSeconds
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
    try {
        if ($started) { Stop-OwnedProcess $fake }
    }
    finally {
        $fake.Dispose()
        $env:PSExecutionPolicyPreference = $originalPolicy
        Pop-Location
    }
}
