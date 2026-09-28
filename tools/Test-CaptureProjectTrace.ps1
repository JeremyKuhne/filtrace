#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Contract checks for the bundled executable-project capture helper.

.DESCRIPTION
  Exercises dotnet-trace profile negotiation and sidecar metadata without building
  or launching a target. A fake recorder covers current, legacy, malformed, and
  incompatible profile surfaces.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$captureScript = Join-Path $root '.agents/skills/filtrace/scripts/Capture-ProjectTrace.ps1'
$recorderScript = Join-Path $root '.agents/skills/filtrace/scripts/Get-DotnetTraceRecorder.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ftp-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-ThrowsLike([scriptblock]$Action, [string]$Expected) {
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message.IndexOf($Expected, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return
        }

        throw "Expected failure containing '$Expected', got: $($_.Exception.Message)"
    }

    throw "Expected failure containing '$Expected', but the action succeeded."
}

function Invoke-ProjectCapture([string]$HostPath, [string[]]$Arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $HostPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = (Get-Location).Path
    if ([System.IO.Path]::GetFileNameWithoutExtension($HostPath) -eq 'powershell') {
        $Arguments = @('-ExecutionPolicy', 'RemoteSigned') + $Arguments
    }
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        Assert-True ($process.Start()) "Could not start capture host '$HostPath'."
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw "Capture host '$HostPath' did not exit within the test's 60-second bound."
        }
        Assert-True ($stdout.Wait(5000) -and $stderr.Wait(5000)) 'Capture output did not drain after exit.'
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout.Result
            Stderr = $stderr.Result
        }
    }
    finally {
        $process.Dispose()
    }
}

if (-not ('FakeBoundedCaptureProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;

public sealed class FakeBoundedCaptureProcess : IDisposable
{
    public FakeBoundedCaptureProcess(string failure) { Failure = failure; }
    public ProcessStartInfo StartInfo { get; set; }
    public string Failure { get; }
    public bool Started { get; private set; }
    public int WaitCalls { get; private set; }
    public int KillCalls { get; private set; }
    public bool TreeKill { get; private set; }
    public bool Disposed { get; private set; }

    public bool Start() { Started = true; return true; }
    public int Id
    {
        get
        {
            if (Failure == "id") { throw new InvalidOperationException("fake collector id unavailable"); }
            return 41234;
        }
    }
    public bool WaitForExit(int milliseconds)
    {
        WaitCalls++;
        if (Failure == "wait-twice" ||
            ((Failure == "wait" || Failure == "wait-kill") && WaitCalls == 1))
        {
            throw new InvalidOperationException("fake collector wait unavailable");
        }
        return true;
    }
    public int ExitCode
    {
        get
        {
            if (Failure == "exit") { throw new InvalidOperationException("fake collector exit unavailable"); }
            return 0;
        }
    }
    public void Kill(bool entireProcessTree)
    {
        KillCalls++;
        TreeKill = entireProcessTree;
        if (Failure == "wait-kill") { throw new InvalidOperationException("fake collector tree kill failed"); }
    }
    public void Dispose() { Disposed = true; }
}
'@
}

function New-FakeCollector([string]$Failure) {
    return [FakeBoundedCaptureProcess]::new($Failure)
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$oldMode = $env:FILTRACE_FAKE_RECORDER_MODE
$oldCollectMode = $env:FILTRACE_FAKE_COLLECT_MODE
$oldArgumentsFile = $env:FILTRACE_FAKE_ARGS_FILE
$oldPidFile = $env:FILTRACE_FAKE_PID_FILE
$oldChildPidFile = $env:FILTRACE_FAKE_CHILD_PID_FILE
$oldCaptureScript = $env:FILTRACE_CAPTURE_SCRIPT
$oldTestProject = $env:FILTRACE_TEST_PROJECT
$oldFakeRecorder = $env:FILTRACE_FAKE_RECORDER
$oldTestTrace = $env:FILTRACE_TEST_TRACE
try {
    $tokens = $null
    $parseErrors = $null
    $captureAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $captureScript,
        [ref]$tokens,
        [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Capture-ProjectTrace.ps1 did not parse.'

    $recorderTokens = $null
    $recorderParseErrors = $null
    $recorderAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $recorderScript,
        [ref]$recorderTokens,
        [ref]$recorderParseErrors)
    Assert-True ($recorderParseErrors.Count -eq 0) 'Get-DotnetTraceRecorder.ps1 did not parse.'

    $functionNames = @(
        'Stop-OwnedEventPipeRecorder',
        'Invoke-BoundedEventPipeCapture',
        'Write-EventPipeCaptureAttempt',
        'Write-CaptureMetadata')
    $definitions = @(
        $captureAst.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $functionNames },
            $true) |
            Sort-Object { $_.Extent.StartOffset } |
            ForEach-Object { $_.Extent.Text }
    )
    Assert-True ($definitions.Count -eq $functionNames.Count) 'Capture metadata function could not be isolated.'
    . ([scriptblock]::Create(($definitions -join [Environment]::NewLine)))
    . $recorderScript

    $source = Get-Content -LiteralPath $captureScript -Raw
    $preflightOffset = $source.IndexOf(
        '$dotnetTraceRecorder = Get-DotnetTraceRecorder',
        [StringComparison]::Ordinal)
    $buildOffset = $source.IndexOf(
        'dotnet build $projFile.FullName',
        [StringComparison]::Ordinal)
    Assert-True ($preflightOffset -ge 0) 'Recorder preflight invocation was not found.'
    Assert-True ($buildOffset -ge 0) 'Project build invocation was not found.'
    Assert-True ($preflightOffset -lt $buildOffset) 'Recorder preflight must run before the project build.'

    $fakeRecorder = Join-Path $temporaryRoot 'fake-dotnet-trace.ps1'
    $fakeSource = @'
$command = $args[0]
if ($command -eq '--version') {
    if ($env:FILTRACE_FAKE_RECORDER_MODE -eq 'version-fail') {
        Write-Error 'version unavailable' -ErrorAction Continue
        exit 6
    }
    if ($env:FILTRACE_FAKE_RECORDER_MODE -eq 'version-malformed') {
        Write-Output 'unknown'
        exit 0
    }
    Write-Output '9.0.661903+fake'
    exit 0
}

if ($command -eq 'collect') {
    if ($env:FILTRACE_FAKE_ARGS_FILE) {
        [System.IO.File]::WriteAllText(
            $env:FILTRACE_FAKE_ARGS_FILE,
            (ConvertTo-Json -InputObject @($args) -Compress),
            (New-Object System.Text.UTF8Encoding($false)))
    }
    $outputIndex = [Array]::IndexOf([object[]]$args, '--output')
    if ($outputIndex -lt 0 -or $outputIndex + 1 -ge $args.Count) {
        Write-Error 'fake collector received no output path' -ErrorAction Continue
        exit 9
    }
    $trace = $args[$outputIndex + 1]
    switch ($env:FILTRACE_FAKE_COLLECT_MODE) {
        'success' {
            [System.IO.File]::WriteAllText($trace, 'complete fake trace')
            Write-Output 'fake collector completed'
            exit 0
        }
        'nonzero' {
            [System.IO.File]::WriteAllText($trace, 'partial fake trace')
            Write-Error 'fake collector failed' -ErrorAction Continue
            exit 17
        }
        'empty' {
            [System.IO.File]::WriteAllBytes($trace, [byte[]]@())
            exit 0
        }
        'missing' { exit 0 }
        'timeout' {
            [System.IO.File]::WriteAllText($trace, 'partial fake trace')
            [System.IO.File]::WriteAllText($env:FILTRACE_FAKE_PID_FILE, [string]$PID)
            $childStart = [System.Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
            $childStart.UseShellExecute = $false
            $childStart.RedirectStandardOutput = $true
            $childStart.RedirectStandardError = $true
            foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30')) {
                $childStart.ArgumentList.Add($argument)
            }
            $child = [System.Diagnostics.Process]::Start($childStart)
            [System.IO.File]::WriteAllText($env:FILTRACE_FAKE_CHILD_PID_FILE, [string]$child.Id)
            $child.Dispose()
            Start-Sleep -Seconds 30
            exit 0
        }
        'timeout-missing' {
            [System.IO.File]::WriteAllText($env:FILTRACE_FAKE_PID_FILE, [string]$PID)
            Start-Sleep -Seconds 30
            exit 0
        }
        default {
            Write-Error 'fake collector mode was not selected' -ErrorAction Continue
            exit 9
        }
    }
}

if ($command -ne 'list-profiles') {
    Write-Error "unexpected fake recorder command: $command" -ErrorAction Continue
    exit 9
}

switch ($env:FILTRACE_FAKE_RECORDER_MODE) {
    'current' {
        Write-Output 'dotnet-trace profiles:'
        Write-Output '  dotnet-common                        - Runtime diagnostics'
        Write-Output '  dotnet-sampled-thread-time (collect) - Managed CPU samples'
        Write-Output '  gc-verbose                           - Allocation samples'
        Write-Output '  cpu-sampling (collect-linux)         - Kernel samples'
        exit 0
    }
    'legacy' {
        Write-Output 'dotnet-trace profiles:'
        Write-Output '  cpu-sampling - Managed CPU samples'
        Write-Output '  gc-verbose  - Allocation samples'
        exit 0
    }
    'collect-linux-only' {
        Write-Output 'dotnet-trace profiles:'
        Write-Output '  cpu-sampling (collect-linux) - Kernel samples'
        Write-Output '  gc-verbose                   - Allocation samples'
        exit 0
    }
    'none' {
        Write-Output 'dotnet-trace profiles:'
        Write-Output '  database - Database events'
        exit 0
    }
    'malformed' {
        Write-Output 'no parseable rows'
        exit 0
    }
    'list-fail' {
        Write-Error 'profile inventory unavailable' -ErrorAction Continue
        exit 7
    }
    default {
        Write-Error 'fake mode was not selected' -ErrorAction Continue
        exit 8
    }
}
'@
    [System.IO.File]::WriteAllText(
        $fakeRecorder,
        $fakeSource,
        (New-Object System.Text.UTF8Encoding($false)))

    $env:FILTRACE_FAKE_RECORDER_MODE = 'current'
    $current = Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    Assert-True (
        $current.ProfileArgument -ceq 'dotnet-common,dotnet-sampled-thread-time') `
        'Current recorder profiles did not select the proven CPU pair.'
    Assert-True ($current.Version -ceq '9.0.661903+fake') 'Recorder version was not retained.'
    Assert-True (
        ($current.Metadata.profiles -join ',') -ceq 'dotnet-common,dotnet-sampled-thread-time') `
        'Effective current profiles were not retained in metadata.'

    $allocation = Get-DotnetTraceRecorder $fakeRecorder 'alloc'
    Assert-True ($allocation.ProfileArgument -ceq 'gc-verbose') 'Allocation capture did not select gc-verbose.'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'legacy'
    $legacy = Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    Assert-True ($legacy.ProfileArgument -ceq 'cpu-sampling') 'Advertised legacy CPU profile was not selected.'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'collect-linux-only'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'supported CPU collect profile'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'none'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'Available: database'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'malformed'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'no profiles that apply to collect'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'list-fail'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'list-profiles failed (exit 7)'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'version-fail'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } '--version failed'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'version-malformed'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'no semantic version'

        $projectDirectory = Join-Path $temporaryRoot 'project with spaces'
        New-Item -ItemType Directory -Path $projectDirectory | Out-Null
        $buildMarker = Join-Path $projectDirectory 'build-started.txt'
        $projectPath = Join-Path $projectDirectory 'Probe.csproj'
        $escapedMarker = [System.Security.SecurityElement]::Escape($buildMarker)
        $projectSource = @"
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
    <Target Name="MarkBuildStart" BeforeTargets="Build">
        <WriteLinesToFile File="$escapedMarker" Lines="build started" Overwrite="true" />
    </Target>
</Project>
"@
        [System.IO.File]::WriteAllText(
                $projectPath,
                $projectSource,
                (New-Object System.Text.UTF8Encoding($false)))
        $rejectedTrace = Join-Path $projectDirectory 'should-not-exist.nettrace'
        $env:FILTRACE_FAKE_RECORDER_MODE = 'collect-linux-only'
        $fullOutput = & (Get-Process -Id $PID).Path -NoProfile -File $captureScript `
                -Project $projectPath `
                -Profiler EP `
                -Metric cpu `
                -DotnetTracePath $fakeRecorder `
                -Output $rejectedTrace 2>&1 | Out-String
        $fullExitCode = $LASTEXITCODE
        Assert-True ($fullExitCode -ne 0) 'Incompatible recorder unexpectedly passed the full helper preflight.'
        Assert-True (
            $fullOutput.IndexOf('capture preflight failed', [StringComparison]::OrdinalIgnoreCase) -ge 0) `
                'Full helper did not report the recorder preflight failure.'
        Assert-True (-not (Test-Path -LiteralPath $buildMarker)) 'Project build started before recorder compatibility was established.'
        Assert-True (-not (Test-Path -LiteralPath $rejectedTrace)) 'Target trace was created after recorder preflight failed.'

        $missingRecorder = Join-Path $temporaryRoot 'absent-dotnet-trace.exe'
        $missingOutput = & (Get-Process -Id $PID).Path -NoProfile -File $captureScript `
                -Project $projectPath `
                -Profiler EP `
                -DotnetTracePath $missingRecorder `
                -EventPipeTimeoutSeconds 1 `
                -Output $rejectedTrace 2>&1 | Out-String
        $missingExitCode = $LASTEXITCODE
        Assert-True ($missingExitCode -ne 0 -and
            $missingOutput.Contains('override could not be resolved')) `
            'Missing explicit recorder did not fail before the project build.'
        Assert-True (-not (Test-Path -LiteralPath $buildMarker)) `
            'Missing explicit recorder unexpectedly started the project build.'

    $sidecarPath = Join-Path $temporaryRoot 'probe.nettrace'
    $emitted = @(Write-CaptureMetadata $sidecarPath ([ordered]@{ cpu = 'enabled' }) $current.Metadata)
    Assert-True ($emitted.Count -eq 0) 'Sidecar writer polluted the success stream.'

    $sidecarFile = "$sidecarPath.filtrace.json"
    $sidecar = Get-Content -LiteralPath $sidecarFile -Raw | ConvertFrom-Json
    Assert-True ($sidecar.schemaVersion -eq 1) 'Sidecar schema version changed.'
    Assert-True ($sidecar.analyses.cpu -ceq 'enabled') 'Sidecar analysis state was not retained.'
    Assert-True ($sidecar.recorder.name -ceq 'dotnet-trace') 'Sidecar recorder name was not retained.'
    Assert-True ($sidecar.recorder.version -ceq '9.0.661903+fake') 'Sidecar recorder version was not retained.'
    Assert-True (
        ($sidecar.recorder.profiles -join ',') -ceq 'dotnet-common,dotnet-sampled-thread-time') `
        'Sidecar effective profiles were not retained.'

    $bytes = [System.IO.File]::ReadAllBytes($sidecarFile)
    Assert-True (
        $bytes.Length -lt 3 -or -not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) `
        'Sidecar must be UTF-8 without a BOM.'

    foreach ($failure in @('id', 'wait', 'wait-twice', 'wait-kill', 'exit')) {
        $fakeProcess = New-FakeCollector $failure
        $result = Invoke-BoundedEventPipeCapture 'fake-not-launched' @('collect') 1 $fakeProcess
        Assert-True ($fakeProcess.Started -and $fakeProcess.KillCalls -eq 1 -and
            $fakeProcess.TreeKill -and $fakeProcess.Disposed) `
            "A started collector survived '$failure' without an owned tree-kill request (started=$($fakeProcess.Started), kills=$($fakeProcess.KillCalls), tree=$($fakeProcess.TreeKill), disposed=$($fakeProcess.Disposed), outcome=$($result.Error), cleanup=$($result.Cleanup))."
        Assert-True ($result.Error -like '*fake collector*unavailable*' -and
            $result.ExitCode -eq $null -and -not $result.TimedOut) `
            "A '$failure' process-state error was not reported as an error."
        Assert-True (($null -eq $result.ProcessId) -eq ($failure -eq 'id')) `
            "A '$failure' error lost or invented the recorder PID."
        if ($failure -eq 'wait-twice') {
            Assert-True ($result.Cleanup -ceq 'termination-unverified' -and
                -not $result.CollectorStopped -and $result.CleanupError -like '*wait unavailable*') `
                'A repeated wait error was reported as verified cleanup.'
        }
        elseif ($failure -eq 'wait-kill') {
            Assert-True ($result.Cleanup -ceq 'termination-failed' -and
                $result.CleanupError -like '*tree kill failed*') `
                'A failed process-tree kill was hidden by the error record.'
        }
        else {
            Assert-True ($result.Cleanup -ceq 'tree-termination-requested' -and
                $result.CollectorStopped) "$failure cleanup was not bounded and observed."
        }

        $tracePath = Join-Path $temporaryRoot "post-start-$failure.nettrace"
        $attemptPath = Write-EventPipeCaptureAttempt $tracePath 'recorder-error' $result.Error $current $result 1
        $attempt = Get-Content -LiteralPath $attemptPath -Raw | ConvertFrom-Json
        Assert-True ($attempt.status -ceq 'recorder-error' -and
            $attempt.cleanup.descendantsMayRemain -eq $true -and
            $attempt.recorder.processId -eq $result.ProcessId -and
            $null -eq $attempt.traceBytes -and
            -not (Test-Path -LiteralPath "$tracePath.filtrace.json")) `
            "$failure attempt record invented successful recorder or subject evidence."
    }

    [System.IO.File]::WriteAllText(
        (Join-Path $projectDirectory 'Program.cs'),
        'System.Console.WriteLine("capture probe");',
        (New-Object System.Text.UTF8Encoding($false)))
    $env:FILTRACE_FAKE_RECORDER_MODE = 'current'
    $runtimeOutput = Join-Path $temporaryRoot 'capture output with spaces'
    New-Item -ItemType Directory -Path $runtimeOutput | Out-Null
    $hosts = @((Get-Process -Id $PID).Path)
    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
        $hosts += (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
    }
    $argumentWrapper = Join-Path $temporaryRoot 'invoke-array-args.ps1'
    $argumentSource = @'
& $env:FILTRACE_CAPTURE_SCRIPT -Project $env:FILTRACE_TEST_PROJECT -Profiler EP -Metric alloc `
    -Tfm net10.0 -DotnetTracePath $env:FILTRACE_FAKE_RECORDER -Output $env:FILTRACE_TEST_TRACE `
    -EventPipeTimeoutSeconds 10 -AppArgs @('space in one arg', 'quote"inside', 'C:\with spaces\', '')
exit $LASTEXITCODE
'@
    [System.IO.File]::WriteAllText(
        $argumentWrapper,
        $argumentSource,
        (New-Object System.Text.UTF8Encoding($false)))
    $env:FILTRACE_CAPTURE_SCRIPT = $captureScript
    $env:FILTRACE_TEST_PROJECT = $projectPath
    $env:FILTRACE_FAKE_RECORDER = $fakeRecorder

    Push-Location $projectDirectory
    try {
        foreach ($hostPath in ($hosts | Select-Object -Unique)) {
            $hostName = [System.IO.Path]::GetFileNameWithoutExtension($hostPath)
            $common = @('-NoProfile', '-NonInteractive', '-File', $captureScript,
                '-Project', $projectPath, '-Profiler', 'EP', '-Metric', 'alloc',
                '-Tfm', 'net10.0', '-DotnetTracePath', $fakeRecorder)
            $defaultTrace = Join-Path $runtimeOutput "$hostName-default.nettrace"
            $env:FILTRACE_FAKE_COLLECT_MODE = 'success'
            $default = Invoke-ProjectCapture $hostPath ($common + @('-Output', $defaultTrace))
            Assert-True ($default.ExitCode -eq 0) "$hostName default capture failed: $($default.Stderr)"
            Assert-True (Test-Path -LiteralPath "$defaultTrace.filtrace.json") "$hostName default capture omitted metadata."
            Assert-True (-not (Test-Path -LiteralPath "$defaultTrace.capture-attempt.json")) "$hostName default capture wrote a failure record."

            if ($hostName -eq 'powershell') {
                Remove-Item -LiteralPath $buildMarker
                $unsupportedTrace = Join-Path $runtimeOutput "$hostName-unsupported.nettrace"
                $unsupported = Invoke-ProjectCapture $hostPath ($common + @(
                        '-Output', $unsupportedTrace, '-EventPipeTimeoutSeconds', '1'))
                Assert-True ($unsupported.ExitCode -ne 0 -and
                    $unsupported.Stderr -match 'requires\s+PowerShell\s+7') `
                    'Windows PowerShell 5.1 did not reject the opted-in deadline.'
                Assert-True (-not (Test-Path -LiteralPath $buildMarker) -and
                    -not (Test-Path -LiteralPath $unsupportedTrace) -and
                    -not (Test-Path -LiteralPath "$unsupportedTrace.capture-attempt.json")) `
                    'Windows PowerShell 5.1 built or launched after rejecting the opted-in deadline.'
                continue
            }

            $autoArgs = $common + @('-EventPipeTimeoutSeconds', '10')
            $autoFirst = Invoke-ProjectCapture $hostPath $autoArgs
            $autoSecond = Invoke-ProjectCapture $hostPath $autoArgs
            $autoTraces = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'perf-traces') -Filter 'Probe-*.nettrace' -File)
            Assert-True ($autoFirst.ExitCode -eq 0 -and $autoSecond.ExitCode -eq 0 -and $autoTraces.Count -eq 2) `
                "$hostName did not give two opted-in default captures independent paths."
            foreach ($autoTrace in $autoTraces) {
                Assert-True (Test-Path -LiteralPath "$($autoTrace.FullName).filtrace.json") `
                    "$hostName default opted-in capture omitted metadata."
            }
            foreach ($autoTrace in $autoTraces) {
                [System.IO.File]::Delete($autoTrace.FullName)
                [System.IO.File]::Delete("$($autoTrace.FullName).filtrace.json")
            }

            $successTrace = Join-Path $runtimeOutput "$hostName-success.nettrace"
            $env:FILTRACE_FAKE_ARGS_FILE = "$successTrace.args.json"
            $env:FILTRACE_TEST_TRACE = $successTrace
            $appArguments = @('space in one arg', 'quote"inside', 'C:\with spaces\', '')
            $successArgs = @('-NoProfile', '-NonInteractive', '-File', $argumentWrapper)
            $success = Invoke-ProjectCapture $hostPath $successArgs
            Assert-True ($success.ExitCode -eq 0) "$hostName opted-in capture failed: $($success.Stderr)"
            Assert-True ($success.Stdout.Contains('Next-step filtrace commands:')) "$hostName success omitted analysis commands."
            Assert-True (Test-Path -LiteralPath "$successTrace.filtrace.json") "$hostName opted-in capture omitted metadata."
            $recorded = @(Get-Content -LiteralPath $env:FILTRACE_FAKE_ARGS_FILE -Raw | ConvertFrom-Json)
            $outputIndex = [Array]::IndexOf([object[]]$recorded, '--output')
            $profileIndex = [Array]::IndexOf([object[]]$recorded, '--profile')
            Assert-True ($recorded[$outputIndex + 1] -ceq $successTrace) "$hostName output path lost its argument boundary."
            Assert-True ($recorded[$profileIndex + 1] -ceq 'gc-verbose') "$hostName allocation profile changed."
            Assert-True (
                ((@($recorded | Select-Object -Last $appArguments.Count)) -join '|') -ceq ($appArguments -join '|')) `
                "$hostName app arguments lost spaces, quotes, empty values, or trailing backslashes."

            Remove-Item -LiteralPath $env:FILTRACE_FAKE_ARGS_FILE
            $reused = Invoke-ProjectCapture $hostPath $successArgs
            Assert-True ($reused.ExitCode -ne 0 -and $reused.Stderr -match 'already\s+exists') `
                "$hostName reused output unexpectedly (exit $($reused.ExitCode), stdout: $($reused.Stdout), stderr: $($reused.Stderr))."
            Assert-True (-not (Test-Path -LiteralPath $env:FILTRACE_FAKE_ARGS_FILE)) `
                "$hostName launched a collector after rejecting a reused output."

            $orphanTrace = Join-Path $runtimeOutput "$hostName-orphan.nettrace"
            $orphanRecord = "$orphanTrace.capture-attempt.json"
            [System.IO.File]::WriteAllText($orphanRecord, 'prior attempt evidence')
            $orphan = Invoke-ProjectCapture $hostPath ($common + @(
                    '-Output', $orphanTrace, '-EventPipeTimeoutSeconds', '1'))
            Assert-True ($orphan.ExitCode -ne 0 -and $orphan.Stderr -match 'already\s+exists') `
                "$hostName overwrote an orphaned capture attempt."
            Assert-True ([System.IO.File]::ReadAllText($orphanRecord) -ceq 'prior attempt evidence') `
                "$hostName changed prior attempt evidence."

            $invalidDeadline = Invoke-ProjectCapture $hostPath ($common + @(
                    '-Output', (Join-Path $runtimeOutput "$hostName-invalid.nettrace"),
                    '-EventPipeTimeoutSeconds', '0'))
            Assert-True ($invalidDeadline.ExitCode -ne 0) "$hostName accepted a zero EventPipe deadline."
            $maximumTrace = Join-Path $runtimeOutput "$hostName-maximum.nettrace"
            $maximum = Invoke-ProjectCapture $hostPath ($common + @(
                    '-Output', $maximumTrace, '-EventPipeTimeoutSeconds', '86400'))
            Assert-True ($maximum.ExitCode -eq 0 -and (Test-Path -LiteralPath "$maximumTrace.filtrace.json")) `
                "$hostName rejected the maximum EventPipe deadline."
            $overMaximum = Invoke-ProjectCapture $hostPath ($common + @(
                    '-Output', (Join-Path $runtimeOutput "$hostName-too-long.nettrace"),
                    '-EventPipeTimeoutSeconds', '86401'))
            Assert-True ($overMaximum.ExitCode -ne 0) "$hostName accepted a deadline beyond the maximum."

            foreach ($mode in @('nonzero', 'empty', 'missing', 'timeout', 'timeout-missing')) {
                $env:FILTRACE_FAKE_COLLECT_MODE = $mode
                $trace = Join-Path $runtimeOutput "$hostName-$mode.nettrace"
                $env:FILTRACE_FAKE_ARGS_FILE = "$trace.args.json"
                $env:FILTRACE_FAKE_PID_FILE = "$trace.pid"
                $env:FILTRACE_FAKE_CHILD_PID_FILE = "$trace.child.pid"
                $watch = [System.Diagnostics.Stopwatch]::StartNew()
                $caseStartedUtc = [DateTime]::UtcNow
                $survivors = [System.Collections.Generic.List[int]]::new()
                try {
                    $failure = Invoke-ProjectCapture $hostPath ($common + @(
                            '-Output', $trace, '-EventPipeTimeoutSeconds', '1'))
                }
                finally {
                    foreach ($pidPath in @($env:FILTRACE_FAKE_PID_FILE, $env:FILTRACE_FAKE_CHILD_PID_FILE)) {
                        if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) { continue }
                        $ownedId = [int][System.IO.File]::ReadAllText($pidPath)
                        $owned = Get-Process -Id $ownedId -ErrorAction SilentlyContinue
                        if ($null -eq $owned) { continue }
                        try {
                            $survivors.Add($ownedId)
                            $comparison = if ([System.OperatingSystem]::IsWindows()) {
                                [StringComparison]::OrdinalIgnoreCase
                            }
                            else { [StringComparison]::Ordinal }
                            if (-not [string]::Equals($owned.Path, $hostPath, $comparison) -or
                                $owned.StartTime.ToUniversalTime() -lt $caseStartedUtc.AddSeconds(-5)) {
                                throw "Fake process PID $ownedId could not be identity-verified for cleanup."
                            }
                            Stop-Process -Id $ownedId -Force
                        }
                        finally { $owned.Dispose() }
                    }
                }
                $watch.Stop()
                $attemptPath = "$trace.capture-attempt.json"
                Assert-True (Test-Path -LiteralPath $attemptPath) "$hostName $mode wrote no failure record."
                $attempt = Get-Content -LiteralPath $attemptPath -Raw | ConvertFrom-Json
                Assert-True (-not (Test-Path -LiteralPath "$trace.filtrace.json")) `
                    "$hostName $mode published success metadata."
                Assert-True (-not $failure.Stdout.Contains('Next-step filtrace commands:')) `
                    "$hostName $mode printed success-shaped analysis commands."
                Assert-True ($attempt.subjectCompletion -ceq 'unverified' -and
                    $attempt.cleanup.descendantsMayRemain -eq $true) `
                    "$hostName $mode claimed descendant completion without evidence."
                $attemptBytes = [System.IO.File]::ReadAllBytes($attemptPath)
                Assert-True (
                    $attemptBytes.Length -lt 3 -or
                    -not ($attemptBytes[0] -eq 0xEF -and $attemptBytes[1] -eq 0xBB -and $attemptBytes[2] -eq 0xBF)) `
                    "$hostName $mode attempt record has a UTF-8 BOM."
                if ($mode -eq 'nonzero') {
                    Assert-True ($failure.ExitCode -eq 17 -and $attempt.status -ceq 'recorder-failed') `
                        "$hostName lost the recorder's nonzero exit."
                    Assert-True ($attempt.traceBytes -gt 0) "$hostName failed recorder discarded the partial trace."
                }
                elseif ($mode -like 'timeout*') {
                    Assert-True ($failure.ExitCode -ne 0 -and $attempt.status -ceq 'timeout') `
                        "$hostName did not report the bounded timeout."
                    Assert-True ($watch.Elapsed.TotalSeconds -lt 20) "$hostName timeout exceeded the 20-second test bound."
                    if ($mode -eq 'timeout') {
                        Assert-True ($attempt.traceBytes -gt 0) "$hostName timeout did not retain its partial trace."
                    }
                    else {
                        Assert-True ($null -eq $attempt.traceBytes) `
                            "$hostName invented trace bytes for a pre-output timeout."
                    }
                    $fakePid = [int][System.IO.File]::ReadAllText($env:FILTRACE_FAKE_PID_FILE)
                    Assert-True ($fakePid -gt 0 -and $survivors.Count -eq 0) `
                        "$hostName left an owned fake process alive after timeout."
                    if ($mode -eq 'timeout') {
                        Assert-True (Test-Path -LiteralPath $env:FILTRACE_FAKE_CHILD_PID_FILE) `
                            "$hostName fake timeout did not start a child to exercise tree termination."
                    }
                }
                else {
                    Assert-True ($failure.ExitCode -ne 0 -and $attempt.status -ceq 'empty-trace') `
                        "$hostName accepted missing or empty recorder output."
                }
                Assert-True ($attempt.recorder.profiles[0] -ceq 'gc-verbose') `
                    "$hostName $mode lost effective recorder provenance."
            }

            $wrongProfiler = Invoke-ProjectCapture $hostPath (@('-NoProfile', '-NonInteractive',
                    '-File', $captureScript, '-Project', $projectPath, '-Profiler', 'ETW',
                    '-EventPipeTimeoutSeconds', '1'))
            Assert-True ($wrongProfiler.ExitCode -ne 0 -and
                $wrongProfiler.Stderr -match 'valid\s+only\s+with\s+-Profiler\s+EP') `
                "$hostName silently accepted an EventPipe-only deadline for ETW (exit $($wrongProfiler.ExitCode), stderr: $($wrongProfiler.Stderr))."
        }
    }
    finally {
        Pop-Location
    }

    $global:LASTEXITCODE = 0
    Write-Host 'Project capture contract passed.' -ForegroundColor Green
}
finally {
    $env:FILTRACE_FAKE_RECORDER_MODE = $oldMode
    $env:FILTRACE_FAKE_COLLECT_MODE = $oldCollectMode
    $env:FILTRACE_FAKE_ARGS_FILE = $oldArgumentsFile
    $env:FILTRACE_FAKE_PID_FILE = $oldPidFile
    $env:FILTRACE_FAKE_CHILD_PID_FILE = $oldChildPidFile
    $env:FILTRACE_CAPTURE_SCRIPT = $oldCaptureScript
    $env:FILTRACE_TEST_PROJECT = $oldTestProject
    $env:FILTRACE_FAKE_RECORDER = $oldFakeRecorder
    $env:FILTRACE_TEST_TRACE = $oldTestTrace
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}