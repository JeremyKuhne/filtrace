#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script = Join-Path $root 'benchmarks/Invoke-TrackDInvestigation.ps1'
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Write-Json([string] $Path, [object] $Value) {
    [string] $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, "$json`n", $utf8)
}

[string] $temporaryRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "filtrace-trackd-contract-$([Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    [string] $corpusSource = Join-Path $temporaryRoot 'corpus-source'
    [string] $corpusInputs = Join-Path $corpusSource 'inputs'
    [string] $corpus = Join-Path $temporaryRoot 'corpus'
    [System.IO.Directory]::CreateDirectory($corpusInputs) | Out-Null
    [System.IO.Directory]::CreateDirectory($corpus) | Out-Null
    [string] $fakeTrace = Join-Path $corpusInputs 'cpu-10k-d20.nettrace'
    [System.IO.File]::WriteAllText(
        $fakeTrace,
        'fake trace bytes',
        $utf8)
    [string] $archive = Join-Path $corpus 'input-corpus.zip'
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $corpusSource,
        $archive,
        [System.IO.Compression.CompressionLevel]::Fastest,
        $false)
    [string] $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Write-Json (Join-Path $corpus 'input-corpus.manifest.json') ([ordered]@{
        schemaVersion = 1
        traces = @([ordered]@{
            name = 'cpu-10k-d20'
            archivePath = 'inputs/cpu-10k-d20.nettrace'
            bytes = (Get-Item -LiteralPath $fakeTrace).Length
            sha256 = (Get-FileHash -LiteralPath $fakeTrace -Algorithm SHA256).Hash
        })
        archive = [ordered]@{
            path = 'input-corpus.zip'
            sha256 = $archiveHash
            bytes = (Get-Item -LiteralPath $archive).Length
        }
    })

    [string] $adapter = Join-Path $root 'tools/fixtures/Fake-TrackDMeasurements.ps1'
    [string] $blockingProcessName = if ($IsWindows) {
        'Filtrace.BlockingProcess.exe'
    }
    else {
        'Filtrace.BlockingProcess'
    }
    [string] $blockingProcess = Join-Path `
        $root `
        "tools/fixtures/Filtrace.BlockingProcess/bin/Release/net10.0/$blockingProcessName"
    Assert-True `
        (Test-Path -LiteralPath $blockingProcess -PathType Leaf) `
        "Elapsed probe child was not built at '$blockingProcess'."

    [string] $probeTrace = Join-Path $temporaryRoot 'elapsed-probe.nettrace'
    Copy-Item `
        -LiteralPath (Join-Path $root 'tests/Filtrace.Core.Tests/Fixtures/threadpool.nettrace') `
        -Destination $probeTrace
    [string] $probeOutput = Join-Path $temporaryRoot 'elapsed-probe.json'
    [string] $readyPath = Join-Path $temporaryRoot 'elapsed-probe.ready'
    [string] $releasePath = Join-Path $temporaryRoot 'elapsed-probe.release'
    [System.Management.Automation.CommandInfo] $dotnetCommand = @(
        Get-Command dotnet -CommandType Application -ErrorAction Stop)[0]
    [System.Diagnostics.ProcessStartInfo] $probeStart = [System.Diagnostics.ProcessStartInfo]::new()
    $probeStart.FileName = $dotnetCommand.Source
    $probeStart.WorkingDirectory = $root
    $probeStart.UseShellExecute = $false
    $probeStart.RedirectStandardOutput = $true
    $probeStart.RedirectStandardError = $true
    foreach ($argument in @(
        'run', '-c', 'Release', '--no-build',
        '--project', 'benchmarks/Filtrace.Benchmarks', '--',
        '--cli-telemetry',
        '--scenario', 'info-warm',
        '--trace', $probeTrace,
        '--output', $probeOutput,
        '--iterations', '1',
        '--filtrace', $blockingProcess)) {
        $probeStart.ArgumentList.Add($argument)
    }
    $probeStart.Environment['FILTRACE_ELAPSED_READY_PATH'] = $readyPath
    $probeStart.Environment['FILTRACE_ELAPSED_RELEASE_PATH'] = $releasePath

    [System.Diagnostics.Process] $probeProcess = [System.Diagnostics.Process]::new()
    $probeProcess.StartInfo = $probeStart
    [System.Threading.Tasks.Task[string]] $probeStandardOutput = $null
    [System.Threading.Tasks.Task[string]] $probeStandardError = $null
    [System.Diagnostics.Stopwatch] $heldOpen = $null
    [bool] $probeStarted = $false
    [int] $probeCleanupTimeoutMilliseconds = 5000
    try {
        $probeStarted = $probeProcess.Start()
        Assert-True $probeStarted 'Elapsed probe process did not start.'
        $probeStandardOutput = $probeProcess.StandardOutput.ReadToEndAsync()
        $probeStandardError = $probeProcess.StandardError.ReadToEndAsync()

        [System.Diagnostics.Stopwatch] $readinessWait = [System.Diagnostics.Stopwatch]::StartNew()
        while (
            -not (Test-Path -LiteralPath $readyPath) -and
            -not $probeProcess.HasExited -and
            $readinessWait.Elapsed -lt [TimeSpan]::FromSeconds(30)
        ) {
            [System.Threading.Thread]::Sleep(10)
        }
        Assert-True `
            (Test-Path -LiteralPath $readyPath) `
            'Elapsed probe child did not publish readiness.'

        $heldOpen = [System.Diagnostics.Stopwatch]::StartNew()
        [System.Threading.Thread]::Sleep(500)
        [System.IO.File]::WriteAllText($releasePath, '')
        $heldOpen.Stop()

        if (-not $probeProcess.WaitForExit(30000)) {
            throw 'Elapsed probe process did not exit after release.'
        }

        [System.Threading.Tasks.Task] $probeOutputCompletion = [System.Threading.Tasks.Task]::WhenAll(
            [System.Threading.Tasks.Task[]]@($probeStandardOutput, $probeStandardError))
        $probeOutputCompletion.WaitAsync(
            [TimeSpan]::FromMilliseconds($probeCleanupTimeoutMilliseconds)).GetAwaiter().GetResult()
        [string] $probeText = $probeStandardOutput.GetAwaiter().GetResult()
        [string] $probeError = $probeStandardError.GetAwaiter().GetResult()
        Assert-True `
            ($probeProcess.ExitCode -eq 0) `
            "Elapsed probe failed with code $($probeProcess.ExitCode): $probeError"
        Assert-True `
            ([string]::IsNullOrEmpty($probeError)) `
            "Elapsed probe wrote unexpected stderr: $probeError"
        Assert-True `
            ($probeText.Trim() -eq $probeOutput) `
            'Elapsed probe did not report its telemetry output path.'
    }
    finally {
        if (-not (Test-Path -LiteralPath $releasePath)) {
            [System.IO.File]::WriteAllText($releasePath, '')
        }
        if ($probeStarted) {
            [bool] $probeExited = $false
            try {
                $probeExited = $probeProcess.HasExited
            }
            catch {
                Write-Warning "Unable to inspect the elapsed probe during cleanup: $($_.Exception.Message)"
            }

            if (-not $probeExited) {
                try {
                    $probeProcess.Kill($true)
                }
                catch {
                    Write-Warning "Unable to terminate the elapsed probe during cleanup: $($_.Exception.Message)"
                }

                try {
                    if (-not $probeProcess.WaitForExit($probeCleanupTimeoutMilliseconds)) {
                        Write-Warning `
                            "Elapsed probe remained active after the bounded cleanup wait."
                    }
                }
                catch {
                    Write-Warning "Unable to wait for the elapsed probe during cleanup: $($_.Exception.Message)"
                }
            }
        }
        $probeProcess.Dispose()
    }

    [object] $probeReport = Get-Content -LiteralPath $probeOutput -Raw | ConvertFrom-Json
    [object] $probeLaunch = @($probeReport.launches)[0]
    Assert-True ($probeReport.schemaVersion -eq 2) 'Elapsed probe did not write telemetry schema 2.'
    Assert-True `
        ($probeLaunch.launchToExitMilliseconds -ge $heldOpen.Elapsed.TotalMilliseconds) `
        'Launch-to-exit telemetry did not contain the synchronized child wait.'
    Assert-True `
        ($probeLaunch.launchToExitMilliseconds -gt $probeLaunch.totalProcessorMilliseconds) `
        'Launch-to-exit telemetry did not remain distinct from child CPU time.'

    [string] $firstCommandDirectory = Join-Path $temporaryRoot 'path-first'
    [string] $secondCommandDirectory = Join-Path $temporaryRoot 'path-second'
    [System.IO.Directory]::CreateDirectory($firstCommandDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($secondCommandDirectory) | Out-Null
    [string] $duplicateCommandName = "filtrace-duplicate-$([Guid]::NewGuid().ToString('N'))"
    [string] $duplicateCommandFileName = if ($IsWindows) {
        "$duplicateCommandName.cmd"
    }
    else {
        $duplicateCommandName
    }
    [object[]] $duplicateCommandDefinitions = @(
        [pscustomobject]@{ Directory = $firstCommandDirectory; Output = 'first-path-match' },
        [pscustomobject]@{ Directory = $secondCommandDirectory; Output = 'second-path-match' })
    foreach ($commandDefinition in $duplicateCommandDefinitions) {
        [string] $commandPath = Join-Path $commandDefinition.Directory $duplicateCommandFileName
        [string] $commandContents = if ($IsWindows) {
            "@echo off`r`necho $($commandDefinition.Output)`r`n"
        }
        else {
            "#!/bin/sh`nprintf '%s\n' '$($commandDefinition.Output)'`n"
        }
        [System.IO.File]::WriteAllText($commandPath, $commandContents, $utf8)
        if (-not $IsWindows) {
            [System.IO.File]::SetUnixFileMode(
                $commandPath,
                [System.IO.UnixFileMode]::UserRead -bor
                    [System.IO.UnixFileMode]::UserWrite -bor
                    [System.IO.UnixFileMode]::UserExecute)
        }
    }

    [System.Management.Automation.CommandInfo] $gitCommand = @(
        Get-Command git -CommandType Application -ErrorAction Stop)[0]
    [string] $previousPath = $env:PATH
    [string] $duplicatePathRun = Join-Path $temporaryRoot 'duplicate-path'
    try {
        $env:PATH = "$firstCommandDirectory$([System.IO.Path]::PathSeparator)" +
            "$secondCommandDirectory$([System.IO.Path]::PathSeparator)$previousPath"
        [System.Management.Automation.CommandInfo[]] $duplicateCommands = @(
            Get-Command $duplicateCommandName -CommandType Application -ErrorAction Stop)
        Assert-True ($duplicateCommands.Count -eq 2) 'Duplicate command setup did not produce two PATH matches.'
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $duplicatePathRun `
            -NoBuild `
            -DotnetPath $duplicateCommandName `
            -GitPath $gitCommand.Source `
            -TestAdapterPath $adapter
    }
    finally {
        $env:PATH = $previousPath
    }
    [object] $duplicatePathResult = Get-Content `
        -LiteralPath (Join-Path $duplicatePathRun 'run.json') `
        -Raw | ConvertFrom-Json
    Assert-True `
        ($duplicatePathResult.sdkVersion -eq 'first-path-match') `
        'Multiple PATH matches did not select the first executable.'

    [string] $success = Join-Path $temporaryRoot 'success'
    & $script `
        -InputCorpusDirectory $corpus `
        -BaselineCheckout $root `
        -CandidateCheckout $root `
        -AllowDirtyCheckouts `
        -OutputDirectory $success `
        -BenchmarkJob dry `
        -TelemetryIterations 2 `
        -NoBuild `
        -TestAdapterPath $adapter
    [object] $successStatus = Get-Content -LiteralPath (Join-Path $success 'run-status.json') -Raw | ConvertFrom-Json
    [object] $comparison = Get-Content -LiteralPath (Join-Path $success 'comparison.json') -Raw | ConvertFrom-Json
    Assert-True ($successStatus.status -eq 'completed') 'Fake no-op run did not complete.'
    Assert-True (@($comparison.benchmarkRows).Count -eq 2) 'Fake no-op did not compare two BDN rows.'
    Assert-True (@($comparison.benchmarkRows | Where-Object {
        $_.meanDeltaPercent -ne 0 -or $_.allocatedDeltaBytes -ne 0
    }).Count -eq 0) 'Fake no-op benchmark deltas were not neutral.'
    Assert-True `
        ($comparison.cliTelemetry.averageLaunchToExitDeltaMilliseconds -eq 0) `
        'Fake no-op launch-to-exit delta was not neutral.'
    Assert-True `
        ($comparison.cliTelemetry.averageCpuDeltaPercent -is [double] -and `
            $comparison.cliTelemetry.averageCpuDeltaPercent -eq 0.0) `
        'Fake no-op CLI CPU percentage was not a neutral JSON number.'
    Assert-True `
        ($comparison.cliTelemetry.averageCpuDeltaMilliseconds -is [double] -and `
            $comparison.cliTelemetry.averageCpuDeltaMilliseconds -eq 0.0) `
        'Fake no-op CLI CPU absolute delta was not a neutral JSON number.'
    Assert-True ($comparison.cliTelemetry.peakWorkingSetDeltaBytes -eq 0) 'Fake no-op working-set delta was not neutral.'
    Assert-True ($comparison.cliTelemetry.privateMemoryDeltaBytes -eq 0) 'Fake no-op private-memory delta was not neutral.'

    [System.Collections.Generic.List[string]] $reviewRegressionFailures = @()
    [object[]] $digestCases = @(
        [ordered]@{ Mode = 'alternating-case'; Expected = 1 },
        [ordered]@{ Mode = 'different-value'; Expected = 2 })
    [string] $previousFakeDigestMode = $env:FILTRACE_TRACKD_FAKE_DIGEST_MODE
    try {
        foreach ($digestCase in $digestCases) {
            [string] $digestRun = Join-Path $temporaryRoot "digest-$($digestCase.Mode)"
            $env:FILTRACE_TRACKD_FAKE_DIGEST_MODE = $digestCase.Mode
            & $script `
                -InputCorpusDirectory $corpus `
                -BaselineCheckout $root `
                -CandidateCheckout $root `
                -AllowDirtyCheckouts `
                -OutputDirectory $digestRun `
                -TelemetryIterations 2 `
                -NoBuild `
                -TestAdapterPath $adapter
            [object] $digestComparison = Get-Content `
                -LiteralPath (Join-Path $digestRun 'comparison.json') `
                -Raw | ConvertFrom-Json
            if (
                $digestComparison.cliTelemetry.baseline.distinctOutputDigests -ne $digestCase.Expected -or
                $digestComparison.cliTelemetry.candidate.distinctOutputDigests -ne $digestCase.Expected
            ) {
                $reviewRegressionFailures.Add(
                    "Digest mode '$($digestCase.Mode)' did not produce $($digestCase.Expected) distinct output digests.")
            }
        }
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_DIGEST_MODE = $previousFakeDigestMode
    }

    [string] $integralSchemaRun = Join-Path $temporaryRoot 'schema-integral-double'
    [string] $previousFakeTelemetry = $env:FILTRACE_TRACKD_FAKE_ELAPSED
    try {
        $env:FILTRACE_TRACKD_FAKE_ELAPSED = 'schema-integral-double'
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $integralSchemaRun `
            -TelemetryIterations 1 `
            -NoBuild `
            -TestAdapterPath $adapter
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_ELAPSED = $previousFakeTelemetry
    }
    [object] $integralSchemaStatus = Get-Content `
        -LiteralPath (Join-Path $integralSchemaRun 'run-status.json') `
        -Raw | ConvertFrom-Json
    Assert-True `
        ($integralSchemaStatus.status -eq 'completed') `
        'Integral floating-point schema version 2 with one launch was rejected.'

    [string] $zeroCountersRun = Join-Path $temporaryRoot 'zero-counters'
    [string] $previousFakeZeroCounters = $env:FILTRACE_TRACKD_FAKE_ZERO_COUNTERS
    try {
        $env:FILTRACE_TRACKD_FAKE_ZERO_COUNTERS = '1'
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $zeroCountersRun `
            -NoBuild `
            -TestAdapterPath $adapter
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_ZERO_COUNTERS = $previousFakeZeroCounters
    }
    [object] $zeroCountersStatus = Get-Content `
        -LiteralPath (Join-Path $zeroCountersRun 'run-status.json') `
        -Raw | ConvertFrom-Json
    [object] $zeroCountersComparison = Get-Content `
        -LiteralPath (Join-Path $zeroCountersRun 'comparison.json') `
        -Raw | ConvertFrom-Json
    Assert-True ($zeroCountersStatus.status -eq 'completed') 'Zero sampled counters were rejected.'
    Assert-True `
        ($zeroCountersComparison.cliTelemetry.baseline.averageCpuMilliseconds -eq 0) `
        'Zero sampled CPU was not retained.'
    Assert-True `
        ($zeroCountersComparison.cliTelemetry.baseline.maxPeakWorkingSetBytes -eq 0) `
        'Zero sampled working set was not retained.'
    Assert-True `
        ($zeroCountersComparison.cliTelemetry.baseline.maxPrivateMemoryBytes -eq 0) `
        'Zero sampled private memory was not retained.'
    Assert-True `
        ($zeroCountersComparison.cliTelemetry.averageCpuDeltaPercent -is [double] -and `
            $zeroCountersComparison.cliTelemetry.averageCpuDeltaPercent -eq 0.0) `
        'Zero sampled CPU did not produce a neutral JSON percentage.'
    Assert-True `
        ($zeroCountersComparison.cliTelemetry.averageCpuDeltaMilliseconds -is [double] -and `
            $zeroCountersComparison.cliTelemetry.averageCpuDeltaMilliseconds -eq 0.0) `
        'Zero sampled CPU did not produce a neutral JSON absolute delta.'

    [string] $baselineZeroRun = Join-Path $temporaryRoot 'baseline-zero-cpu'
    [string] $previousFakeZeroCpuArm = $env:FILTRACE_TRACKD_FAKE_ZERO_CPU_ARM
    try {
        $env:FILTRACE_TRACKD_FAKE_ZERO_CPU_ARM = 'baseline'
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $baselineZeroRun `
            -NoBuild `
            -TestAdapterPath $adapter
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_ZERO_CPU_ARM = $previousFakeZeroCpuArm
    }
    [object] $baselineZeroStatus = Get-Content `
        -LiteralPath (Join-Path $baselineZeroRun 'run-status.json') `
        -Raw | ConvertFrom-Json
    [object] $baselineZeroComparison = Get-Content `
        -LiteralPath (Join-Path $baselineZeroRun 'comparison.json') `
        -Raw | ConvertFrom-Json
    Assert-True ($baselineZeroStatus.status -eq 'completed') 'Baseline-zero CPU run did not complete.'
    Assert-True `
        ($baselineZeroComparison.cliTelemetry.baseline.averageCpuMilliseconds -eq 0.0) `
        'Baseline-zero CPU run did not retain the zero baseline.'
    Assert-True `
        ($baselineZeroComparison.cliTelemetry.candidate.averageCpuMilliseconds -eq 100.0) `
        'Baseline-zero CPU run did not retain the positive candidate.'
    [object] $baselineZeroPercent = `
        $baselineZeroComparison.cliTelemetry.PSObject.Properties['averageCpuDeltaPercent']
    Assert-True `
        ($null -ne $baselineZeroPercent -and $null -eq $baselineZeroPercent.Value) `
        'Baseline-zero CPU percentage was not serialized as JSON null.'
    [object] $baselineZeroAbsolute = `
        $baselineZeroComparison.cliTelemetry.PSObject.Properties['averageCpuDeltaMilliseconds']
    Assert-True `
        ($null -ne $baselineZeroAbsolute -and `
            $baselineZeroAbsolute.Value -is [double] -and `
            $baselineZeroAbsolute.Value -eq 100.0) `
        'Baseline-zero CPU absolute delta was not serialized as 100 milliseconds.'

    [string] $candidateZeroRun = Join-Path $temporaryRoot 'candidate-zero-cpu'
    try {
        $env:FILTRACE_TRACKD_FAKE_ZERO_CPU_ARM = 'candidate'
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $candidateZeroRun `
            -NoBuild `
            -TestAdapterPath $adapter
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_ZERO_CPU_ARM = $previousFakeZeroCpuArm
    }
    [object] $candidateZeroStatus = Get-Content `
        -LiteralPath (Join-Path $candidateZeroRun 'run-status.json') `
        -Raw | ConvertFrom-Json
    [object] $candidateZeroComparison = Get-Content `
        -LiteralPath (Join-Path $candidateZeroRun 'comparison.json') `
        -Raw | ConvertFrom-Json
    Assert-True ($candidateZeroStatus.status -eq 'completed') 'Candidate-zero CPU run did not complete.'
    Assert-True `
        ($candidateZeroComparison.cliTelemetry.baseline.averageCpuMilliseconds -eq 100.0) `
        'Candidate-zero CPU run did not retain the positive baseline.'
    Assert-True `
        ($candidateZeroComparison.cliTelemetry.candidate.averageCpuMilliseconds -eq 0.0) `
        'Candidate-zero CPU run did not retain the zero candidate.'
    Assert-True `
        ($candidateZeroComparison.cliTelemetry.averageCpuDeltaPercent -is [double] -and `
            $candidateZeroComparison.cliTelemetry.averageCpuDeltaPercent -eq -100.0) `
        'Candidate-zero CPU percentage was not serialized as -100 percent.'
    Assert-True `
        ($candidateZeroComparison.cliTelemetry.averageCpuDeltaMilliseconds -is [double] -and `
            $candidateZeroComparison.cliTelemetry.averageCpuDeltaMilliseconds -eq -100.0) `
        'Candidate-zero CPU absolute delta was not serialized as -100 milliseconds.'

    [object[]] $invalidTelemetryCases = @(
        [ordered]@{
            Mode = 'schema1'
            Field = $null
            Value = $null
            Message = 'does not use schema version 2'
        },
        [ordered]@{
            Mode = 'schema-missing'
            Field = $null
            Value = $null
            Message = 'missing schemaVersion'
        },
        [ordered]@{
            Mode = 'schema-null'
            Field = $null
            Value = $null
            Message = 'schemaVersion must be a JSON number'
        },
        [ordered]@{
            Mode = 'schema-boolean'
            Field = $null
            Value = $null
            Message = 'schemaVersion must be a JSON number'
        },
        [ordered]@{
            Mode = 'schema-string'
            Field = $null
            Value = $null
            Message = 'schemaVersion must be a JSON number'
        },
        [ordered]@{
            Mode = 'schema-fractional'
            Field = $null
            Value = $null
            Message = 'schemaVersion must be an integer from 0 through Int64.MaxValue'
        },
        [ordered]@{
            Mode = 'schema-nonfinite'
            Field = $null
            Value = $null
            Message = 'nonfinite schemaVersion'
        },
        [ordered]@{
            Mode = 'empty'
            Field = $null
            Value = $null
            Message = 'iterations must be a positive integer'
        },
        [ordered]@{
            Mode = 'iterations-fractional'
            Field = $null
            Value = $null
            Message = 'iterations must be a positive integer JSON number'
        },
        [ordered]@{
            Mode = 'iterations-null'
            Field = $null
            Value = $null
            Message = 'iterations must be a positive integer JSON number'
        },
        [ordered]@{
            Mode = 'iterations-missing'
            Field = $null
            Value = $null
            Message = 'iterations must be a positive integer JSON number'
        },
        [ordered]@{
            Mode = 'iterations-string'
            Field = $null
            Value = $null
            Message = 'iterations must be a positive integer JSON number'
        },
        [ordered]@{
            Mode = 'iterations-boolean'
            Field = $null
            Value = $null
            Message = 'iterations must be a positive integer JSON number'
        },
        [ordered]@{
            Mode = 'launches-missing'
            Field = $null
            Value = $null
            Message = 'missing launches'
        },
        [ordered]@{
            Mode = 'launches-null'
            Field = $null
            Value = $null
            Message = 'launches must be a JSON array'
        },
        [ordered]@{
            Mode = 'launches-empty'
            Field = $null
            Value = $null
            Message = 'incomplete launch set'
        },
        [ordered]@{
            Mode = 'launches-object'
            Field = $null
            Value = $null
            Message = 'launches must be a JSON array'
        },
        [ordered]@{
            Mode = 'launches-string'
            Field = $null
            Value = $null
            Message = 'launches must be a JSON array'
        },
        [ordered]@{
            Mode = 'missing'
            Field = $null
            Value = $null
            Message = 'missing launchToExitMilliseconds'
        },
        [ordered]@{
            Mode = 'malformed'
            Field = $null
            Value = $null
            Message = 'launchToExitMilliseconds must be a JSON number'
        },
        [ordered]@{
            Mode = 'nonfinite'
            Field = $null
            Value = $null
            Message = 'nonfinite launchToExitMilliseconds'
        },
        [ordered]@{
            Mode = 'negative'
            Field = $null
            Value = $null
            Message = 'negative launchToExitMilliseconds'
        },
        [ordered]@{
            Mode = $null
            Field = 'totalProcessorMilliseconds'
            Value = 'missing'
            Message = 'missing totalProcessorMilliseconds'
        },
        [ordered]@{
            Mode = $null
            Field = 'totalProcessorMilliseconds'
            Value = 'null'
            Message = 'totalProcessorMilliseconds must be a JSON number'
        },
        [ordered]@{
            Mode = $null
            Field = 'totalProcessorMilliseconds'
            Value = 'string'
            Message = 'totalProcessorMilliseconds must be a JSON number'
        },
        [ordered]@{
            Mode = $null
            Field = 'totalProcessorMilliseconds'
            Value = 'boolean'
            Message = 'totalProcessorMilliseconds must be a JSON number'
        },
        [ordered]@{
            Mode = $null
            Field = 'totalProcessorMilliseconds'
            Value = 'nonfinite'
            Message = 'nonfinite totalProcessorMilliseconds'
        },
        [ordered]@{
            Mode = $null
            Field = 'totalProcessorMilliseconds'
            Value = 'negative'
            Message = 'negative totalProcessorMilliseconds'
        },
        [ordered]@{
            Mode = $null
            Field = 'peakWorkingSetBytes'
            Value = 'missing'
            Message = 'missing peakWorkingSetBytes'
        },
        [ordered]@{
            Mode = $null
            Field = 'peakWorkingSetBytes'
            Value = 'negative'
            Message = 'peakWorkingSetBytes must be an integer from 0 through Int64.MaxValue'
        },
        [ordered]@{
            Mode = $null
            Field = 'maxPrivateMemoryBytes'
            Value = 'fractional'
            Message = 'maxPrivateMemoryBytes must be an integer from 0 through Int64.MaxValue'
        },
        [ordered]@{
            Mode = $null
            Field = 'maxPrivateMemoryBytes'
            Value = 'overflow'
            Message = 'maxPrivateMemoryBytes must be an integer from 0 through Int64.MaxValue'
        },
        [ordered]@{
            Mode = $null
            Field = 'exitCode'
            Value = 'missing'
            Message = 'missing exitCode'
        },
        [ordered]@{
            Mode = $null
            Field = 'exitCode'
            Value = 'one'
            Message = 'nonzero exitCode'
        },
        [ordered]@{
            Mode = $null
            Field = 'standardOutputLength'
            Value = 'missing'
            Message = 'missing standardOutputLength'
        },
        [ordered]@{
            Mode = $null
            Field = 'standardOutputLength'
            Value = 'negative'
            Message = 'standardOutputLength must be an integer from 0 through Int64.MaxValue'
        },
        [ordered]@{
            Mode = $null
            Field = 'standardOutputLength'
            Value = 'zero'
            Message = 'standardOutputLength must be positive'
        },
        [ordered]@{
            Mode = $null
            Field = 'standardErrorLength'
            Value = 'missing'
            Message = 'missing standardErrorLength'
        },
        [ordered]@{
            Mode = $null
            Field = 'standardErrorLength'
            Value = 'one'
            Message = 'nonzero standardErrorLength'
        },
        [ordered]@{
            Mode = $null
            Field = 'outputSha256'
            Value = 'missing'
            Message = 'missing outputSha256'
        },
        [ordered]@{
            Mode = $null
            Field = 'outputSha256'
            Value = 'bad-digest-length'
            Message = 'outputSha256 must be a 64-character hexadecimal string'
        },
        [ordered]@{
            Mode = $null
            Field = 'outputSha256'
            Value = 'bad-digest-hex'
            Message = 'outputSha256 must be a 64-character hexadecimal string'
        },
        [ordered]@{
            Mode = $null
            Field = 'iteration'
            Value = 'missing'
            Message = 'missing iteration'
        },
        [ordered]@{
            Mode = $null
            Field = 'iteration'
            Value = 'one'
            Message = 'launch iterations must be the unique ordinals'
        },
        [ordered]@{
            Mode = $null
            Field = 'arguments'
            Value = 'missing'
            Message = 'missing arguments'
        },
        [ordered]@{
            Mode = $null
            Field = 'arguments'
            Value = 'empty-array'
            Message = 'arguments must be a nonempty JSON array of strings'
        },
        [ordered]@{
            Mode = $null
            Field = 'arguments'
            Value = 'not-array'
            Message = 'arguments must be a nonempty JSON array of strings'
        },
        [ordered]@{
            Mode = $null
            Field = 'arguments'
            Value = 'nonstring-array'
            Message = 'arguments must be a nonempty JSON array of strings'
        })
    $previousFakeTelemetry = $env:FILTRACE_TRACKD_FAKE_ELAPSED
    [string] $previousFakeInvalidField = $env:FILTRACE_TRACKD_FAKE_INVALID_FIELD
    [string] $previousFakeInvalidValue = $env:FILTRACE_TRACKD_FAKE_INVALID_VALUE
    try {
        foreach ($invalidTelemetryCase in $invalidTelemetryCases) {
            [string] $caseName = if ($invalidTelemetryCase.Mode) {
                $invalidTelemetryCase.Mode
            }
            else {
                "$($invalidTelemetryCase.Field)-$($invalidTelemetryCase.Value)"
            }
            [string] $invalidTelemetryRun = Join-Path `
                $temporaryRoot `
                "telemetry-$caseName"
            $env:FILTRACE_TRACKD_FAKE_ELAPSED = $invalidTelemetryCase.Mode
            $env:FILTRACE_TRACKD_FAKE_INVALID_FIELD = $invalidTelemetryCase.Field
            $env:FILTRACE_TRACKD_FAKE_INVALID_VALUE = $invalidTelemetryCase.Value
            [bool] $invalidTelemetryFailed = $false
            [string] $invalidTelemetryError = ''
            try {
                & $script `
                    -InputCorpusDirectory $corpus `
                    -BaselineCheckout $root `
                    -CandidateCheckout $root `
                    -AllowDirtyCheckouts `
                    -OutputDirectory $invalidTelemetryRun `
                    -NoBuild `
                    -TestAdapterPath $adapter
            }
            catch {
                $invalidTelemetryError = $_.Exception.Message
                $invalidTelemetryFailed = $_.Exception.Message.Contains(
                    $invalidTelemetryCase.Message,
                    [StringComparison]::Ordinal)
            }

            [object] $invalidTelemetryStatus = Get-Content `
                -LiteralPath (Join-Path $invalidTelemetryRun 'run-status.json') `
                -Raw | ConvertFrom-Json
            if (-not $invalidTelemetryFailed) {
                $reviewRegressionFailures.Add(
                    "Invalid telemetry case '$caseName' was not rejected as expected; status was '$($invalidTelemetryStatus.status)', error was '$invalidTelemetryError'.")
            }
            else {
                Assert-True `
                    ($invalidTelemetryStatus.status -eq 'failed') `
                    "Invalid telemetry case '$caseName' did not record failed status."
                Assert-True `
                    (Test-Path -LiteralPath (Join-Path $invalidTelemetryRun 'failure.txt')) `
                    "Invalid telemetry case '$caseName' omitted failure.txt."
                Assert-True `
                    (Test-Path -LiteralPath (Join-Path $invalidTelemetryRun 'commands.txt')) `
                    "Invalid telemetry case '$caseName' omitted commands.txt."
                Assert-True `
                    (-not (Test-Path -LiteralPath (Join-Path $invalidTelemetryRun 'comparison.json'))) `
                    "Invalid telemetry case '$caseName' wrote comparison.json."
            }
        }
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_ELAPSED = $previousFakeTelemetry
        $env:FILTRACE_TRACKD_FAKE_INVALID_FIELD = $previousFakeInvalidField
        $env:FILTRACE_TRACKD_FAKE_INVALID_VALUE = $previousFakeInvalidValue
    }
    Assert-True `
        ($reviewRegressionFailures.Count -eq 0) `
        ($reviewRegressionFailures -join [Environment]::NewLine)

    [string] $failure = Join-Path $temporaryRoot 'adapter-failure'
    $previousFailureArm = $env:FILTRACE_TRACKD_FAKE_FAIL_ARM
    $env:FILTRACE_TRACKD_FAKE_FAIL_ARM = 'candidate'
    [bool] $failed = $false
    try {
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $failure `
            -NoBuild `
            -TestAdapterPath $adapter
    }
    catch {
        $failed = $true
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_FAIL_ARM = $previousFailureArm
    }

    [object] $failureStatus = Get-Content -LiteralPath (Join-Path $failure 'run-status.json') -Raw | ConvertFrom-Json
    Assert-True $failed 'Injected adapter failure unexpectedly succeeded.'
    Assert-True ($failureStatus.status -eq 'failed') 'Adapter failure did not record failed status.'
    Assert-True (Test-Path -LiteralPath (Join-Path $failure 'failure.txt')) 'Adapter failure omitted failure.txt.'
    Assert-True (Test-Path -LiteralPath (Join-Path $failure 'commands.txt')) 'Adapter failure omitted commands.txt.'

    [string] $invalidGate = Join-Path $temporaryRoot 'invalid-gate'
    [bool] $gateFailed = $false
    try {
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $invalidGate `
            -TestAdapterPath $adapter
    }
    catch {
        $gateFailed = $true
    }

    [object] $gateStatus = Get-Content -LiteralPath (Join-Path $invalidGate 'run-status.json') -Raw | ConvertFrom-Json
    Assert-True $gateFailed 'Ungated test adapter unexpectedly ran.'
    Assert-True ($gateStatus.status -eq 'failed') 'Ungated adapter did not record failed status.'

    [string] $timeout = Join-Path $temporaryRoot 'adapter-timeout'
    $previousSleepArm = $env:FILTRACE_TRACKD_FAKE_SLEEP_ARM
    $env:FILTRACE_TRACKD_FAKE_SLEEP_ARM = 'baseline'
    [bool] $timedOut = $false
    try {
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $timeout `
            -NoBuild `
            -NativeTimeoutSeconds 1 `
            -TestAdapterPath $adapter
    }
    catch {
        $timedOut = $_.Exception.Message.Contains(
            'did not finish within 1 seconds',
            [StringComparison]::Ordinal)
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_SLEEP_ARM = $previousSleepArm
    }

    [object] $timeoutStatus = Get-Content -LiteralPath (Join-Path $timeout 'run-status.json') -Raw | ConvertFrom-Json
    Assert-True $timedOut 'Injected adapter hang did not report the configured timeout.'
    Assert-True ($timeoutStatus.status -eq 'failed') 'Adapter timeout did not record failed status.'

    [string] $oversized = Join-Path $temporaryRoot 'adapter-output-limit'
    $previousOutputArm = $env:FILTRACE_TRACKD_FAKE_OUTPUT_ARM
    $env:FILTRACE_TRACKD_FAKE_OUTPUT_ARM = 'baseline'
    [bool] $outputFailed = $false
    try {
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $oversized `
            -NoBuild `
            -NativeTimeoutSeconds 30 `
            -TestAdapterPath $adapter
    }
    catch {
        $outputFailed = $_.Exception.Message.Contains(
            'output exceeded 10485760 bytes',
            [StringComparison]::Ordinal)
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_OUTPUT_ARM = $previousOutputArm
    }

    [object] $outputStatus = Get-Content -LiteralPath (Join-Path $oversized 'run-status.json') -Raw | ConvertFrom-Json
    $outputFailed = $outputFailed -or [string]$outputStatus.message -like '*output exceeded 10485760 bytes*'
    Assert-True `
        $outputFailed `
        "Oversized adapter output did not hit the live byte limit. Status: $($outputStatus.message)"
    Assert-True ($outputStatus.status -eq 'failed') 'Oversized adapter output did not record failed status.'

    [string] $unsafeCorpus = Join-Path $temporaryRoot 'unsafe-corpus'
    [System.IO.Directory]::CreateDirectory($unsafeCorpus) | Out-Null
    [string] $unsafeArchive = Join-Path $unsafeCorpus 'input-corpus.zip'
    [System.IO.FileStream] $unsafeStream = [System.IO.File]::Create($unsafeArchive)
    [System.IO.Compression.ZipArchive] $unsafeZip = [System.IO.Compression.ZipArchive]::new(
        $unsafeStream,
        [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [System.IO.Compression.ZipArchiveEntry] $unsafeEntry = $unsafeZip.CreateEntry('../escape.txt')
        [System.IO.StreamWriter] $unsafeWriter = [System.IO.StreamWriter]::new($unsafeEntry.Open())
        try {
            $unsafeWriter.Write('escape')
        }
        finally {
            $unsafeWriter.Dispose()
        }
    }
    finally {
        $unsafeZip.Dispose()
        $unsafeStream.Dispose()
    }

    Write-Json (Join-Path $unsafeCorpus 'input-corpus.manifest.json') ([ordered]@{
        schemaVersion = 1
        traces = @([ordered]@{
            name = 'escape'
            archivePath = '../escape.txt'
            bytes = 6
            sha256 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
        })
        archive = [ordered]@{
            path = 'input-corpus.zip'
            sha256 = (Get-FileHash -LiteralPath $unsafeArchive -Algorithm SHA256).Hash
            bytes = (Get-Item -LiteralPath $unsafeArchive).Length
        }
    })
    [string] $unsafeRun = Join-Path $temporaryRoot 'unsafe-run'
    [bool] $unsafeFailed = $false
    try {
        & $script `
            -InputCorpusDirectory $unsafeCorpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $unsafeRun `
            -NoBuild `
            -TestAdapterPath $adapter
    }
    catch {
        $unsafeFailed = $_.Exception.Message.Contains(
            'unsafe or duplicate destination',
            [StringComparison]::Ordinal)
    }

    [object] $unsafeStatus = Get-Content -LiteralPath (Join-Path $unsafeRun 'run-status.json') -Raw | ConvertFrom-Json
    Assert-True $unsafeFailed 'Unsafe corpus path was not rejected.'
    Assert-True ($unsafeStatus.status -eq 'failed') 'Unsafe corpus did not record failed status.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $unsafeRun 'baseline/escape.txt'))) 'Unsafe corpus wrote outside its extraction root.'

    Write-Host 'Track D investigation contract passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction Stop
    }
}
