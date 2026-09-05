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

function Assert-TelemetryCaseRejected(
    [string] $Case,
    [string] $ExpectedMessage,
    [string] $TemporaryRoot,
    [string] $Corpus,
    [string] $Root,
    [string] $Adapter) {
    [string] $output = Join-Path $TemporaryRoot "telemetry-$Case"
    [string] $previousCase = $env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE
    [bool] $rejected = $false
    try {
        $env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE = $Case
        & $script `
            -InputCorpusDirectory $Corpus `
            -BaselineCheckout $Root `
            -CandidateCheckout $Root `
            -AllowDirtyCheckouts `
            -OutputDirectory $output `
            -BenchmarkJob dry `
            -TelemetryIterations 2 `
            -NoBuild `
            -TestAdapterPath $Adapter
    }
    catch {
        $rejected = $_.Exception.Message.Contains($ExpectedMessage, [StringComparison]::Ordinal)
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE = $previousCase
    }

    [object] $status = Get-Content -LiteralPath (Join-Path $output 'run-status.json') -Raw | ConvertFrom-Json
    Assert-True $rejected "Telemetry case '$Case' was not rejected with '$ExpectedMessage'."
    Assert-True ($status.status -eq 'failed') "Telemetry case '$Case' did not record failed status."
    Assert-True `
        (Test-Path -LiteralPath (Join-Path $output 'candidate/cli-benchmark/cli-process.json')) `
        "Telemetry case '$Case' did not retain its raw candidate report."
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

    [string] $dependencyMismatch = Join-Path $temporaryRoot 'dependency-mismatch'
    [bool] $dependencyMismatchFailed = $false
    try {
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $dependencyMismatch `
            -NoBuild `
            -CandidateDependencyRoot $root `
            -CandidateDependencyCommit '0000000000000000000000000000000000000000' `
            -TestAdapterPath $adapter
    }
    catch {
        $dependencyMismatchFailed = $_.Exception.Message.Contains(
            'resolve candidate dependency commit exited with code',
            [StringComparison]::Ordinal)
    }

    Assert-True $dependencyMismatchFailed 'A mismatched candidate dependency commit unexpectedly ran.'
    $global:LASTEXITCODE = 0

    [string] $success = Join-Path $temporaryRoot 'success'
    & $script `
        -InputCorpusDirectory $corpus `
        -BaselineCheckout $root `
        -CandidateCheckout $root `
        -AllowDirtyCheckouts `
        -OutputDirectory $success `
        -BenchmarkJob dry `
        -TelemetryIterations 25 `
        -NoBuild `
        -TestAdapterPath $adapter
    [object] $successStatus = Get-Content -LiteralPath (Join-Path $success 'run-status.json') -Raw | ConvertFrom-Json
    [object] $comparison = Get-Content -LiteralPath (Join-Path $success 'comparison.json') -Raw | ConvertFrom-Json
    [object] $rawTelemetry = Get-Content `
        -LiteralPath (Join-Path $success 'baseline/cli-benchmark/cli-process.json') `
        -Raw | ConvertFrom-Json
    Assert-True ($successStatus.status -eq 'completed') 'Fake no-op run did not complete.'
    Assert-True ($comparison.schemaVersion -eq 2) 'Fake comparison did not retain telemetry schema metadata.'
    Assert-True (@($comparison.benchmarkRows).Count -eq 2) 'Fake no-op did not compare two BDN rows.'
    Assert-True (@($comparison.benchmarkRows | Where-Object {
        $_.meanDeltaPercent -ne 0 -or $_.allocatedDeltaPercent -ne 0
    }).Count -eq 0) 'Fake no-op benchmark deltas were not neutral.'
    Assert-True ($comparison.cliTelemetry.averageCpuDeltaPercent -eq 0) 'Fake no-op CLI CPU delta was not neutral.'
    Assert-True ($comparison.cliTelemetry.childWallP50DeltaPercent -eq 0) 'Fake no-op CLI wall p50 delta was not neutral.'
    Assert-True ($comparison.cliTelemetry.childWallP95DeltaPercent -eq 0) 'Fake no-op CLI wall p95 delta was not neutral.'
    Assert-True ($comparison.cliTelemetry.baseline.childWallP50Milliseconds -eq 13) 'Fake p50 did not use nearest rank.'
    Assert-True ($comparison.cliTelemetry.baseline.childWallP95Milliseconds -eq 24) 'Fake p95 did not use nearest rank.'
    Assert-True ($comparison.cliTelemetry.baseline.complete) 'Fake comparison lost the complete flag.'
    Assert-True ($comparison.cliTelemetry.baseline.launchCount -eq 25) 'Fake comparison lost the launch count.'
    Assert-True `
        ($comparison.cliTelemetry.baseline.sourceReport -eq 'baseline/cli-benchmark/cli-process.json') `
        'Fake comparison lost stable source metadata.'
    Assert-True (@($rawTelemetry.launches).Count -eq 25) 'Fake raw telemetry did not retain all launches.'
    Assert-True `
        ($rawTelemetry.launches[0].elapsedMilliseconds -eq 1000 -and
            $rawTelemetry.launches[1].elapsedMilliseconds -eq 1) `
        'Fake raw telemetry did not preserve shuffled iteration order.'
    Assert-True ($comparison.cliTelemetry.peakWorkingSetDeltaBytes -eq 0) 'Fake no-op working-set delta was not neutral.'
    Assert-True ($comparison.cliTelemetry.peakWorkingSetDeltaPercent -eq 0) 'Fake no-op working-set percent delta was not neutral.'
    Assert-True ($comparison.cliTelemetry.privateMemoryDeltaBytes -eq 0) 'Fake no-op private-memory delta was not neutral.'
    Assert-True ($comparison.cliTelemetry.privateMemoryDeltaPercent -eq 0) 'Fake no-op private-memory percent delta was not neutral.'
    Assert-True `
        ($comparison.benchmarkAllocationSource -like '*host/wrapper*not child managed allocation*') `
        'Fake no-op comparison did not label the BenchmarkDotNet allocation source.'

    [object[]] $telemetryCases = @(
        [pscustomobject]@{ Case = 'old-artifact'; Message = 'expected 2 with child wall telemetry' },
        [pscustomobject]@{ Case = 'failure'; Message = 'explicitly incomplete' },
        [pscustomobject]@{ Case = 'incomplete'; Message = 'incomplete launch set' },
        [pscustomobject]@{ Case = 'empty-launches'; Message = 'incomplete launch set' },
        [pscustomobject]@{ Case = 'malformed-elapsed'; Message = "property 'elapsedMilliseconds' is not a number" },
        [pscustomobject]@{ Case = 'null-report'; Message = 'is null' },
        [pscustomobject]@{ Case = 'null-launches'; Message = 'missing a launch set' },
        [pscustomobject]@{ Case = 'null-launch'; Message = 'launch 1 is null' },
        [pscustomobject]@{ Case = 'null-arguments'; Message = "missing required property 'arguments'" },
        [pscustomobject]@{ Case = 'reordered-iterations'; Message = 'launch 1 has iteration 2' },
        [pscustomobject]@{ Case = 'duplicate-iterations'; Message = 'launch 2 has iteration 1' },
        [pscustomobject]@{ Case = 'missing-complete'; Message = "missing required property 'complete'" },
        [pscustomobject]@{ Case = 'missing-p50'; Message = "missing required property 'childWallP50Milliseconds'" },
        [pscustomobject]@{ Case = 'corrupt-p95'; Message = 'percentiles do not match' },
        [pscustomobject]@{ Case = 'failure-on-complete'; Message = 'retains a failure diagnostic' },
        [pscustomobject]@{ Case = 'nonzero'; Message = 'not a successful complete observation' },
        [pscustomobject]@{ Case = 'empty-output'; Message = 'not a successful complete observation' },
        [pscustomobject]@{ Case = 'stderr'; Message = 'not a successful complete observation' },
        [pscustomobject]@{ Case = 'inconsistent-digest'; Message = 'inconsistent output digests' },
        [pscustomobject]@{ Case = 'fractional-iteration'; Message = "property 'iteration' is not a 64-bit integer" },
        [pscustomobject]@{ Case = 'string-exit-code'; Message = "property 'exitCode' is not a 64-bit integer" },
        [pscustomobject]@{ Case = 'string-elapsed'; Message = "property 'elapsedMilliseconds' is not a number" },
        [pscustomobject]@{ Case = 'boolean-cpu'; Message = "property 'totalProcessorMilliseconds' is not a number" },
        [pscustomobject]@{ Case = 'malformed-json'; Message = 'Conversion from JSON failed' })
    foreach ($telemetryCase in $telemetryCases) {
        Assert-TelemetryCaseRejected `
            $telemetryCase.Case `
            $telemetryCase.Message `
            $temporaryRoot `
            $corpus `
            $root `
            $adapter
    }

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
