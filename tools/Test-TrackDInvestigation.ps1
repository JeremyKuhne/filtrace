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
    $scriptTokens = $null
    $scriptParseErrors = $null
    [System.Management.Automation.Language.ScriptBlockAst] $scriptAst =
        [System.Management.Automation.Language.Parser]::ParseFile(
            $script,
            [ref]$scriptTokens,
            [ref]$scriptParseErrors)
    Assert-True ($scriptParseErrors.Count -eq 0) 'Invoke-TrackDInvestigation.ps1 did not parse.'
    [string[]] $boundaryFunctionNames = @(
        'Format-Command',
        'Invoke-NativeText',
        'Start-NativeProcess',
        'Wait-NativeProcess',
        'Stop-NativeProcess',
        'Resolve-LocalFile',
        'Get-BoundedAnalyzerFileIdentity',
        'Get-AnalyzerIdentity',
        'Test-FiniteJsonNumber',
        'Get-ValidatedProfileWarnings',
        'Get-ValidatedProfileResult',
        'Get-AnalysisEvidence')
    [object[]] $boundaryDefinitions = @(
        $scriptAst.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -in $boundaryFunctionNames
            },
            $true) |
            Sort-Object { $_.Extent.StartOffset } |
            ForEach-Object { $_.Extent.Text })
    Assert-True `
        ($boundaryDefinitions.Count -eq $boundaryFunctionNames.Count) `
        'Track D boundary functions could not be isolated.'
    . ([scriptblock]::Create(($boundaryDefinitions -join [Environment]::NewLine)))
    $commandLog = [System.Collections.Generic.List[string]]::new()
    $maximumCapturedBytes = 10 * 1024 * 1024
    $nativeCleanupTimeoutMilliseconds = 10000
    $NativeTimeoutSeconds = 30
    $maximumAnalyzerEntries = 512
    $maximumAnalyzerFiles = 256
    $maximumAnalyzerFileBytes = 128MB
    $maximumAnalyzerDirectoryBytes = 512MB
    $profileQualityWarningCodes = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)

    [string] $analysisEvidenceDirectory = Join-Path $temporaryRoot 'analysis-evidence-schema'
    [System.IO.Directory]::CreateDirectory($analysisEvidenceDirectory) | Out-Null
    Write-Json (Join-Path $analysisEvidenceDirectory 'run.json') ([ordered]@{
        status = 'completed'
        queries = @(
            [ordered]@{
                id = 'orientation'
                operation = 'info'
                status = 'completed'
                stdout = 'info.json'
            },
            [ordered]@{
                id = 'hot-methods'
                operation = 'rank'
                status = 'completed'
                stdout = 'rank.json'
            })
    })
    Write-Json (Join-Path $analysisEvidenceDirectory 'rank.json') ([ordered]@{
        schemaVersion = 16
        warnings = @()
        context = [ordered]@{ operation = 'rank'; metric = 'cpu'; unit = 'ms' }
        result = [ordered]@{
            scopeWeight = 128
            contributingRecordCount = 128
            rows = @([ordered]@{ frame = 'Fake.Work'; weight = 128; percentOfScope = 100 })
        }
    })
    [string] $realInfoJson = @'
{"schemaVersion":16,"warnings":[],"hints":[],"context":{"operation":"info"},"result":{"path":"capture.nettrace","format":"NetTrace","totalWeight":128,"sampleCount":128,"symbolResolutionRate":1,"threads":[{"thread":"4860","sampleCount":128}],"availableAnalyses":["cpu","alloc","gcstats"],"etlxCacheState":"converted","analyses":{"cpu":{"captureStatus":"enabled","eventCount":128},"gcstats":{"captureStatus":"enabled","eventCount":1}},"sourceResolution":{"searchedDirectories":[],"sampledManagedFrameCount":128,"mappedManagedFrameCount":0,"matchingPdbModules":[],"highestUnmappedModules":[],"highestUnmappedMethods":[]}}}
'@
    [System.IO.File]::WriteAllText(
        (Join-Path $analysisEvidenceDirectory 'info.json'),
        $realInfoJson,
        $utf8)
    [System.Collections.IDictionary] $realShapeEvidence = Get-AnalysisEvidence `
        $analysisEvidenceDirectory `
        'cpu' `
        $true
    Assert-True `
        ($realShapeEvidence.status -ceq 'observed' -and $realShapeEvidence.eventCount -eq 128) `
        'Schema 16 result analyses did not produce observed CPU evidence.'
    Assert-True `
        ($realShapeEvidence.summaries[0].contributingRecordCount -eq 128 -and
            $realShapeEvidence.summaries[0].contributingRecordCountStatus -ceq 'available') `
        'CPU evidence did not retain its required contributing record count.'

    [string] $realAllocationRankJson = @'
{"schemaVersion":16,"warnings":[],"context":{"operation":"rank","metric":"alloc","measure":"self","unit":"bytes"},"result":{"scopeWeight":34054816,"rootFrame":"","rows":[{"frame":"Filtrace.Tracing.Readers.TraceLogReader.ReadCore","weight":26442304,"percentOfScope":77.65}]}}
'@
    [System.IO.File]::WriteAllText(
        (Join-Path $analysisEvidenceDirectory 'rank.json'),
        $realAllocationRankJson,
        $utf8)
    [string] $allocationInfoJson = @'
{"schemaVersion":16,"warnings":[{"code":"warning","severity":"warning","message":"No sampled-profile (CPU) events were found in the trace."}],"hints":[],"context":{"operation":"info"},"result":{"path":"capture.nettrace","format":"NetTrace","totalWeight":34054816,"sampleCount":32,"symbolResolutionRate":1,"threads":[],"availableAnalyses":["alloc","gcstats"],"etlxCacheState":"converted","analyses":{"alloc":{"captureStatus":"enabled","eventCount":32},"gcstats":{"captureStatus":"enabled","eventCount":1}},"sourceResolution":{"searchedDirectories":[],"sampledManagedFrameCount":0,"mappedManagedFrameCount":0,"matchingPdbModules":[],"highestUnmappedModules":[],"highestUnmappedMethods":[]}}}
'@
    [System.IO.File]::WriteAllText(
        (Join-Path $analysisEvidenceDirectory 'info.json'),
        $allocationInfoJson,
        $utf8)
    [System.Collections.IDictionary] $allocationEvidence = Get-AnalysisEvidence `
        $analysisEvidenceDirectory `
        'alloc' `
        $true
    Assert-True `
        ($allocationEvidence.status -ceq 'observed' -and $allocationEvidence.eventCount -eq 32) `
        'A real-shaped allocation rank without a record count was not observed.'
    Assert-True `
        ($null -eq $allocationEvidence.summaries[0].contributingRecordCount -and
            $allocationEvidence.summaries[0].contributingRecordCountStatus -ceq 'unavailable') `
        'Allocation evidence did not explicitly retain its unavailable record count.'

    Write-Json (Join-Path $analysisEvidenceDirectory 'rank.json') ([ordered]@{
        schemaVersion = 16
        warnings = @()
        context = [ordered]@{ operation = 'rank'; metric = 'alloc'; measure = 'self'; unit = 'bytes' }
        result = [ordered]@{
            scopeWeight = 128
            contributingRecordCount = $null
            rows = @([ordered]@{ frame = 'Fake.Work'; weight = 128; percentOfScope = 100 })
        }
    })
    [System.Collections.IDictionary] $nullCountAllocationEvidence = Get-AnalysisEvidence `
        $analysisEvidenceDirectory `
        'alloc' `
        $true
    Assert-True `
        ($null -eq $nullCountAllocationEvidence.summaries[0].contributingRecordCount -and
            $nullCountAllocationEvidence.summaries[0].contributingRecordCountStatus -ceq 'unavailable') `
        'Allocation evidence rejected an explicitly null record count.'

    Write-Json (Join-Path $analysisEvidenceDirectory 'rank.json') ([ordered]@{
        schemaVersion = 16
        warnings = @()
        context = [ordered]@{ operation = 'rank'; metric = 'cpu'; measure = 'self'; unit = 'ms' }
        result = [ordered]@{
            scopeWeight = 128
            rows = @([ordered]@{ frame = 'Fake.Work'; weight = 128; percentOfScope = 100 })
        }
    })
    [bool] $missingCpuRecordCountFailed = $false
    try {
        $null = Get-ValidatedProfileResult `
            $analysisEvidenceDirectory `
            ([pscustomobject]@{ id = 'hot-methods'; status = 'completed'; stdout = 'rank.json' }) `
            'cpu' `
            'rank'
    }
    catch {
        $missingCpuRecordCountFailed = $_.Exception.Message.Contains(
            'invalid rank scope totals',
            [StringComparison]::Ordinal)
    }
    Assert-True `
        $missingCpuRecordCountFailed `
        'CPU rank evidence accepted a missing contributing record count.'

    [string] $topLevelOnlyInfoJson = @'
{"schemaVersion":16,"warnings":[],"hints":[],"context":{"operation":"info"},"analyses":{"cpu":{"captureStatus":"enabled","eventCount":128}}}
'@
    [System.IO.File]::WriteAllText(
        (Join-Path $analysisEvidenceDirectory 'info.json'),
        $topLevelOnlyInfoJson,
        $utf8)
    [bool] $topLevelOnlyFailed = $false
    try {
        $null = Get-AnalysisEvidence $analysisEvidenceDirectory 'cpu' $true
    }
    catch {
        $topLevelOnlyFailed = $_.Exception.Message.Contains(
            'omitted its result',
            [StringComparison]::Ordinal)
    }
    Assert-True `
        $topLevelOnlyFailed `
        'Profile evidence accepted legacy top-level analyses without a schema 16 result.'

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

    [string] $unsupportedProfileRun = Join-Path $temporaryRoot 'unsupported-profile-scenario'
    [bool] $unsupportedProfileFailed = $false
    try {
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $unsupportedProfileRun `
            -CliScenario 'batch-8' `
            -NoBuild `
            -TestAdapterPath $adapter `
            -CaptureProfiles `
            -AnalyzerPath $blockingProcess `
            -DotnetTracePath $dotnetCommand.Source
    }
    catch {
        $unsupportedProfileFailed = $_.Exception.Message.Contains(
            'CaptureProfiles supports only persistent single-trace warm scenarios',
            [StringComparison]::Ordinal)
    }
    Assert-True `
        $unsupportedProfileFailed `
        'Profile capture did not reject an unsupported scenario before measurement.'
    Assert-True `
        (-not (Test-Path -LiteralPath $unsupportedProfileRun)) `
        'Unsupported profile capture created its output directory before validation.'

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
    [bool] $duplicatePathRejected = $false
    try {
        $env:PATH = "$firstCommandDirectory$([System.IO.Path]::PathSeparator)" +
            "$secondCommandDirectory$([System.IO.Path]::PathSeparator)$previousPath"
        [System.Management.Automation.CommandInfo[]] $duplicateCommands = @(
            Get-Command $duplicateCommandName -CommandType Application -ErrorAction Stop)
        Assert-True ($duplicateCommands.Count -eq 2) 'Duplicate command setup did not produce two PATH matches.'
        try {
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
        catch {
            $duplicatePathRejected = $_.Exception.Message.Contains(
                'must resolve to a native .exe on Windows',
                [StringComparison]::Ordinal)
        }
    }
    finally {
        $env:PATH = $previousPath
    }
    if ($IsWindows) {
        Assert-True $duplicatePathRejected 'Windows batch executable resolution was not rejected clearly.'
    }
    else {
        [object] $duplicatePathResult = Get-Content `
            -LiteralPath (Join-Path $duplicatePathRun 'run.json') `
            -Raw | ConvertFrom-Json
        Assert-True `
            ($duplicatePathResult.sdkVersion -eq 'first-path-match') `
            'Multiple PATH matches did not select the first executable.'
    }

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

    [string] $fakeProfileToolName = if ($IsWindows) {
        'Filtrace.FakeProfileTool.exe'
    }
    else {
        'Filtrace.FakeProfileTool'
    }
    [string] $fakeProfileToolDirectory = Join-Path `
        $root `
        'tools/fixtures/Filtrace.FakeProfileTool/bin/Release/net10.0'
    [string] $fakeProfileTool = Join-Path $fakeProfileToolDirectory $fakeProfileToolName
    Assert-True `
        (Test-Path -LiteralPath $fakeProfileTool -PathType Leaf) `
        "Fake profile tool was not built at '$fakeProfileTool'."

    [string] $pathRecorderDirectory = Join-Path $temporaryRoot 'path-recorder'
    Copy-Item -LiteralPath $fakeProfileToolDirectory -Destination $pathRecorderDirectory -Recurse
    [string] $pathRecorderName = if ($IsWindows) { 'dotnet-trace.exe' } else { 'dotnet-trace' }
    [string] $pathRecorder = Join-Path $pathRecorderDirectory $pathRecorderName
    Rename-Item `
        -LiteralPath (Join-Path $pathRecorderDirectory $fakeProfileToolName) `
        -NewName $pathRecorderName

    [string] $profileInvocationLog = Join-Path $temporaryRoot 'profile-invocations.jsonl'
    [string] $measurementMarkers = Join-Path $temporaryRoot 'profile-measurements.txt'
    [string] $previousProfileMode = $env:FILTRACE_TRACKD_FAKE_PROFILE_MODE
    [string] $previousProfileInvocations = $env:FILTRACE_TRACKD_FAKE_PROFILE_INVOCATIONS
    [string] $previousMeasurementMarkers = $env:FILTRACE_TRACKD_FAKE_MEASUREMENT_MARKERS
    [string] $previousMutationPath = $env:FILTRACE_TRACKD_MUTATE_ANALYZER_DLL
    try {
        $env:FILTRACE_TRACKD_FAKE_PROFILE_INVOCATIONS = $profileInvocationLog
        $env:FILTRACE_TRACKD_FAKE_MEASUREMENT_MARKERS = $measurementMarkers

        [string] $prelaunchOutputDirectory = Join-Path $temporaryRoot 'prelaunch-output-directory'
        [System.IO.Directory]::CreateDirectory($prelaunchOutputDirectory) | Out-Null
        [string] $prelaunchError = Join-Path $temporaryRoot 'prelaunch-error.txt'
        Remove-Item -LiteralPath $profileInvocationLog -Force -ErrorAction SilentlyContinue
        [bool] $directoryOutputFailed = $false
        try {
            $null = Invoke-NativeText `
                $fakeProfileTool `
                @('--version') `
                $root `
                'directory output prelaunch' `
                $prelaunchOutputDirectory `
                $prelaunchError
        }
        catch {
            $directoryOutputFailed = $true
        }
        Assert-True $directoryOutputFailed 'Directory stdout path unexpectedly launched.'
        Assert-True `
            (-not (Test-Path -LiteralPath $profileInvocationLog)) `
            'Directory stdout path executed the child before stream acquisition failed.'

        [string] $readOnlyOutput = Join-Path $temporaryRoot 'prelaunch-readonly.txt'
        [System.IO.File]::WriteAllText($readOnlyOutput, 'read only', $utf8)
        if ($IsWindows) {
            [System.IO.File]::SetAttributes($readOnlyOutput, [System.IO.FileAttributes]::ReadOnly)
        }
        else {
            [System.IO.File]::SetUnixFileMode(
                $readOnlyOutput,
                [System.IO.UnixFileMode]::UserRead)
        }
        Remove-Item -LiteralPath $profileInvocationLog -Force -ErrorAction SilentlyContinue
        [bool] $readOnlyOutputFailed = $false
        try {
            $null = Invoke-NativeText `
                $fakeProfileTool `
                @('--version') `
                $root `
                'read-only output prelaunch' `
                $readOnlyOutput `
                $prelaunchError
        }
        catch {
            $readOnlyOutputFailed = $true
        }
        finally {
            if ($IsWindows) {
                [System.IO.File]::SetAttributes($readOnlyOutput, [System.IO.FileAttributes]::Normal)
            }
            else {
                [System.IO.File]::SetUnixFileMode(
                    $readOnlyOutput,
                    [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
            }
        }
        Assert-True $readOnlyOutputFailed 'Read-only stdout path unexpectedly launched.'
        Assert-True `
            (-not (Test-Path -LiteralPath $profileInvocationLog)) `
            'Read-only stdout path executed the child before stream acquisition failed.'

        Remove-Item -LiteralPath $profileInvocationLog -Force -ErrorAction SilentlyContinue
        [bool] $invalidArtifactLimitFailed = $false
        try {
            $null = Invoke-NativeText `
                $fakeProfileTool `
                @('--version') `
                $root `
                'invalid artifact limit prelaunch' `
                '' `
                '' `
                (Join-Path $temporaryRoot 'bounded-artifact.bin') `
                0
        }
        catch {
            $invalidArtifactLimitFailed = $_.Exception.Message.Contains(
                'requires a positive byte limit',
                [StringComparison]::Ordinal)
        }
        Assert-True $invalidArtifactLimitFailed 'Invalid artifact limit was not rejected before launch.'
        Assert-True `
            (-not (Test-Path -LiteralPath $profileInvocationLog)) `
            'Invalid artifact limit executed the child.'

        [string] $disabledProfiles = Join-Path $temporaryRoot 'profiles-disabled'
        Remove-Item -LiteralPath $profileInvocationLog,$measurementMarkers -Force -ErrorAction SilentlyContinue
        & $script `
            -InputCorpusDirectory $corpus `
            -BaselineCheckout $root `
            -CandidateCheckout $root `
            -AllowDirtyCheckouts `
            -OutputDirectory $disabledProfiles `
            -NoBuild `
            -TestAdapterPath $adapter
        Assert-True `
            (-not (Test-Path -LiteralPath $profileInvocationLog)) `
            'Disabled profile capture invoked the recorder or analyzer.'
        Assert-True `
            (-not (Test-Path -LiteralPath (Join-Path $disabledProfiles 'profiles.json'))) `
            'Disabled profile capture wrote profile artifacts.'

        [string] $fixedAnalyzerDirectory = Join-Path $temporaryRoot 'fixed-analyzer'
        Copy-Item `
            -LiteralPath $fakeProfileToolDirectory `
            -Destination $fixedAnalyzerDirectory `
            -Recurse
        [string] $fixedAnalyzer = Join-Path $fixedAnalyzerDirectory $fakeProfileToolName
        [string] $fixedManagedDll = Join-Path $fixedAnalyzerDirectory 'Filtrace.FakeProfileTool.dll'

        [string] $boundedIdentityFile = Join-Path $temporaryRoot 'bounded-identity.bin'
        [System.IO.File]::WriteAllBytes($boundedIdentityFile, [byte[]](1, 2, 3, 4, 5))
        [long] $previousMaximumAnalyzerFileBytes = $maximumAnalyzerFileBytes
        [long] $previousMaximumAnalyzerDirectoryBytes = $maximumAnalyzerDirectoryBytes
        [bool] $boundedIdentityFailed = $false
        try {
            $maximumAnalyzerFileBytes = 4
            $maximumAnalyzerDirectoryBytes = 8
            $null = Get-BoundedAnalyzerFileIdentity `
                ([System.IO.FileInfo]::new($boundedIdentityFile)) `
                8
        }
        catch {
            $boundedIdentityFailed = $_.Exception.Message.Contains(
                'exceeds 4 bytes',
                [StringComparison]::Ordinal)
        }
        finally {
            $maximumAnalyzerFileBytes = $previousMaximumAnalyzerFileBytes
            $maximumAnalyzerDirectoryBytes = $previousMaximumAnalyzerDirectoryBytes
        }
        Assert-True `
            $boundedIdentityFailed `
            'Analyzer identity did not enforce its streamed per-file byte limit.'

        [string] $tooManyFilesDirectory = Join-Path $temporaryRoot 'analyzer-too-many-files'
        Copy-Item `
            -LiteralPath $fakeProfileToolDirectory `
            -Destination $tooManyFilesDirectory `
            -Recurse
        [int] $existingAnalyzerFiles = @(
            Get-ChildItem -LiteralPath $tooManyFilesDirectory -File -Recurse).Count
        for ([int] $index = $existingAnalyzerFiles; $index -lt 257; $index++) {
            [System.IO.File]::WriteAllBytes(
                (Join-Path $tooManyFilesDirectory "extra-$index.bin"),
                [byte[]]::new(0))
        }
        [bool] $tooManyFilesFailed = $false
        try {
            $null = Get-AnalyzerIdentity (Join-Path $tooManyFilesDirectory $fakeProfileToolName)
        }
        catch {
            $tooManyFilesFailed = $_.Exception.Message.Contains(
                'exceeds 256 files',
                [StringComparison]::Ordinal)
        }
        Assert-True $tooManyFilesFailed 'Analyzer identity materialized more than 256 files.'

        [string] $tooManyEntriesDirectory = Join-Path $temporaryRoot 'analyzer-too-many-entries'
        Copy-Item `
            -LiteralPath $fakeProfileToolDirectory `
            -Destination $tooManyEntriesDirectory `
            -Recurse
        for ([int] $index = 0; $index -le $maximumAnalyzerEntries; $index++) {
            [System.IO.Directory]::CreateDirectory(
                (Join-Path $tooManyEntriesDirectory "empty-$index")) | Out-Null
        }
        [bool] $tooManyEntriesFailed = $false
        try {
            $null = Get-AnalyzerIdentity (Join-Path $tooManyEntriesDirectory $fakeProfileToolName)
        }
        catch {
            $tooManyEntriesFailed = $_.Exception.Message.Contains(
                'exceeds 512 entries including directories',
                [StringComparison]::Ordinal)
        }
        Assert-True $tooManyEntriesFailed 'Analyzer identity did not count empty directories.'

        [string] $linkTarget = Join-Path $temporaryRoot 'analyzer-link-target'
        [System.IO.Directory]::CreateDirectory($linkTarget) | Out-Null
        [string] $linkSentinel = Join-Path $linkTarget 'sentinel.txt'
        [System.IO.File]::WriteAllText($linkSentinel, 'owned sentinel', $utf8)
        [string] $linkedAnalyzerDirectory = Join-Path $temporaryRoot 'analyzer-with-link'
        Copy-Item `
            -LiteralPath $fakeProfileToolDirectory `
            -Destination $linkedAnalyzerDirectory `
            -Recurse
        [string] $childLink = Join-Path $linkedAnalyzerDirectory 'linked-directory'
        if ($IsWindows) {
            New-Item -ItemType Junction -Path $childLink -Target $linkTarget | Out-Null
        }
        else {
            $null = [System.IO.Directory]::CreateSymbolicLink($childLink, $linkTarget)
        }
        [bool] $childLinkFailed = $false
        try {
            $null = Get-AnalyzerIdentity (Join-Path $linkedAnalyzerDirectory $fakeProfileToolName)
        }
        catch {
            $childLinkFailed = $_.Exception.Message.Contains(
                'contains reparse point',
                [StringComparison]::Ordinal)
        }
        finally {
            [System.IO.Directory]::Delete($childLink)
        }
        Assert-True $childLinkFailed 'Analyzer identity traversed a child directory link.'
        Assert-True `
            (Test-Path -LiteralPath $linkSentinel -PathType Leaf) `
            'Child-link rejection or cleanup touched the link target sentinel.'

        [string] $rootLink = Join-Path $temporaryRoot 'linked-analyzer-root'
        if ($IsWindows) {
            New-Item -ItemType Junction -Path $rootLink -Target $fixedAnalyzerDirectory | Out-Null
        }
        else {
            $null = [System.IO.Directory]::CreateSymbolicLink($rootLink, $fixedAnalyzerDirectory)
        }
        [bool] $rootLinkFailed = $false
        try {
            $null = Get-AnalyzerIdentity (Join-Path $rootLink $fakeProfileToolName)
        }
        catch {
            $rootLinkFailed = $_.Exception.Message.Contains(
                'Analyzer directory is a reparse point',
                [StringComparison]::Ordinal)
        }
        finally {
            [System.IO.Directory]::Delete($rootLink)
        }
        Assert-True $rootLinkFailed 'Analyzer identity accepted a reparse-point root.'
        Assert-True `
            (Test-Path -LiteralPath $fixedAnalyzer -PathType Leaf) `
            'Root-link cleanup touched the analyzer target.'

        [string] $profileSuccess = Join-Path $temporaryRoot 'profiles-success'
        Remove-Item -LiteralPath $profileInvocationLog,$measurementMarkers -Force -ErrorAction SilentlyContinue
        $env:FILTRACE_TRACKD_FAKE_PROFILE_MODE = 'gc-valid-empty'
        $env:PATH = "$pathRecorderDirectory$([System.IO.Path]::PathSeparator)$previousPath"
        try {
            & $script `
                -InputCorpusDirectory $corpus `
                -BaselineCheckout $root `
                -CandidateCheckout $root `
                -AllowDirtyCheckouts `
                -OutputDirectory $profileSuccess `
                -NoBuild `
                -TestAdapterPath $adapter `
                -CaptureProfiles `
                -AnalyzerPath $fixedAnalyzer
        }
        finally {
            $env:PATH = $previousPath
        }

        [object] $profileRun = Get-Content -LiteralPath (Join-Path $profileSuccess 'run.json') -Raw | ConvertFrom-Json -Depth 32
        [object] $profiles = Get-Content -LiteralPath (Join-Path $profileSuccess 'profiles.json') -Raw | ConvertFrom-Json -Depth 32
        Assert-True ($profiles.status -ceq 'completed') 'Successful profile workflow did not complete.'
        Assert-True `
            ([string]::Equals(
                [string]$profiles.tools.recorder.path,
                (Resolve-Path -LiteralPath $pathRecorder).Path,
                $(if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }))) `
            'Omitted recorder path did not retain the canonical first PATH match.'
        Assert-True `
            ($profiles.tools.recorder.sha256 -ceq (Get-FileHash -LiteralPath $pathRecorder -Algorithm SHA256).Hash) `
            'Recorder identity did not retain the resolved native executable hash.'
        Assert-True ($profiles.tools.recorder.version -ceq '1.2.3+fake') 'Recorder version was not retained.'
        Assert-True (@($profiles.arms).Count -eq 2) 'Profile workflow did not retain both measured arms.'
        Assert-True `
            (@($profiles.arms.captures | Where-Object { $_.status -cne 'completed' }).Count -eq 0) `
            'Successful profile workflow retained an incomplete capture.'
        Assert-True `
            (@($profiles.arms.captures | Where-Object { $_.metric -ceq 'allocation' }).Count -eq 2) `
            'Profile workflow did not retain one allocation capture per arm.'
        Assert-True `
            ($profiles.metricSemantics.allocation -ceq 'sampled-allocation-ticks') `
            'Allocation profile was not labeled as sampled allocation ticks.'
        [object[]] $allocationProfileEvidence = @(
            $profiles.arms.captures.analyses |
                Where-Object { $_.name -ceq 'alloc' } |
                ForEach-Object { $_.evidence })
        Assert-True `
            ($allocationProfileEvidence.Count -eq 2) `
            'Profile workflow did not retain allocation evidence for both arms.'
        Assert-True `
            (@($allocationProfileEvidence.summaries | Where-Object {
                $null -ne $_.contributingRecordCount -or
                    $_.contributingRecordCountStatus -cne 'unavailable'
            }).Count -eq 0) `
            'Profile workflow fabricated an allocation contributing record count.'
        Assert-True `
            (@($profiles.arms.captures.analyses | Where-Object {
                $_.name -ceq 'gcstats' -and $_.evidence.status -ceq 'empty'
            }).Count -eq 2) `
            'Enabled-zero GC evidence was not retained as empty for both arms.'
        Assert-True `
            ($profileRun.profiles.sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $profileSuccess 'profiles.json') -Algorithm SHA256).Hash) `
            'Run record did not link the exact profile artifact identity.'
        Assert-True `
            ((Get-Content -LiteralPath $measurementMarkers).Count -eq 2) `
            'Profile success did not run both measured arms exactly once.'

        [object[]] $profileInvocations = @(
            Get-Content -LiteralPath $profileInvocationLog |
                ForEach-Object { $_ | ConvertFrom-Json -Depth 16 })
        [object[]] $collectInvocations = @(
            $profileInvocations | Where-Object { $_.arguments[0] -ceq 'collect' })
        Assert-True ($collectInvocations.Count -eq 4) 'Profile workflow did not issue four recorder captures.'
        foreach ($collectInvocation in $collectInvocations) {
            [int] $separator = [Array]::IndexOf([object[]]$collectInvocation.arguments, '--')
            Assert-True ($separator -ge 0) 'Recorder invocation omitted the child argv separator.'
            Assert-True `
                ($collectInvocation.arguments[$separator + 2] -in @('info', 'rank')) `
                'Recorder invocation did not replay telemetry child arguments.'
        }

        [object[]] $preflightFailures = @(
            [pscustomobject]@{ Name = 'absent'; Mode = 'success'; Recorder = Join-Path $temporaryRoot 'missing-dotnet-trace'; Message = 'was not found' },
            [pscustomobject]@{ Name = 'nonzero'; Mode = 'profiles-nonzero'; Recorder = $fakeProfileTool; Message = 'list-profiles' },
            [pscustomobject]@{ Name = 'malformed'; Mode = 'profiles-malformed'; Recorder = $fakeProfileTool; Message = 'no profiles' })
        [string] $invalidRecorder = Join-Path $temporaryRoot 'invalid-recorder.exe'
        [System.IO.File]::WriteAllText($invalidRecorder, 'not an executable', $utf8)
        $preflightFailures += [pscustomobject]@{
            Name = 'exception'
            Mode = 'success'
            Recorder = $invalidRecorder
            Message = 'could not start'
        }

        foreach ($case in $preflightFailures) {
            [string] $failureRun = Join-Path $temporaryRoot "profiles-preflight-$($case.Name)"
            Remove-Item -LiteralPath $measurementMarkers -Force -ErrorAction SilentlyContinue
            $env:FILTRACE_TRACKD_FAKE_PROFILE_MODE = $case.Mode
            [bool] $caseFailed = $false
            [string] $caseFailureMessage = ''
            try {
                & $script `
                    -InputCorpusDirectory $corpus `
                    -BaselineCheckout $root `
                    -CandidateCheckout $root `
                    -AllowDirtyCheckouts `
                    -OutputDirectory $failureRun `
                    -NoBuild `
                    -TestAdapterPath $adapter `
                    -CaptureProfiles `
                    -AnalyzerPath $fixedAnalyzer `
                    -DotnetTracePath $case.Recorder
            }
            catch {
                $caseFailed = $_.Exception.Message.Contains(
                    $case.Message,
                    [StringComparison]::OrdinalIgnoreCase)
            }
            Assert-True $caseFailed "Profile preflight '$($case.Name)' was not rejected as expected."
            Assert-True `
                (-not (Test-Path -LiteralPath $measurementMarkers)) `
                "Profile preflight '$($case.Name)' reached measured work."
        }

        [object[]] $postMeasurementFailures = @(
            [pscustomobject]@{ Name = 'capture-missing'; Mode = 'capture-missing'; Message = 'did not create' },
            [pscustomobject]@{ Name = 'capture-empty'; Mode = 'capture-empty'; Message = 'empty trace' },
            [pscustomobject]@{ Name = 'capture-nonzero'; Mode = 'capture-nonzero'; Message = 'exited with code 8' },
            [pscustomobject]@{ Name = 'analysis-empty-rank'; Mode = 'analysis-empty-rank'; Message = 'contained no cpu rank rows' },
            [pscustomobject]@{ Name = 'analysis-bad-rank-shape'; Mode = 'analysis-bad-rank-shape'; Message = 'malformed rank row' },
            [pscustomobject]@{ Name = 'analysis-invalid-record-count'; Mode = 'analysis-invalid-record-count'; Message = 'invalid rank scope totals' },
            [pscustomobject]@{ Name = 'analysis-empty'; Mode = 'analysis-valid-empty'; Message = 'contained no cpu events' },
            [pscustomobject]@{ Name = 'analysis-missing'; Mode = 'analysis-missing'; Message = 'omitted analysis' },
            [pscustomobject]@{ Name = 'analysis-wrong-top-level'; Mode = 'analysis-wrong-top-level'; Message = 'omitted analysis' },
            [pscustomobject]@{ Name = 'analysis-nonzero'; Mode = 'analysis-nonzero'; Message = 'exited with code 9' },
            [pscustomobject]@{ Name = 'analysis-malformed'; Mode = 'analysis-malformed'; Message = 'did not complete' },
            [pscustomobject]@{ Name = 'gc-absent'; Mode = 'gc-absent'; Message = 'omitted context or result' },
            [pscustomobject]@{ Name = 'gc-malformed'; Mode = 'gc-malformed'; Message = 'did not return schema 16' })
        foreach ($case in $postMeasurementFailures) {
            [string] $failureRun = Join-Path $temporaryRoot "profiles-$($case.Name)"
            Remove-Item -LiteralPath $measurementMarkers -Force -ErrorAction SilentlyContinue
            $env:FILTRACE_TRACKD_FAKE_PROFILE_MODE = $case.Mode
            [bool] $caseFailed = $false
            try {
                & $script `
                    -InputCorpusDirectory $corpus `
                    -BaselineCheckout $root `
                    -CandidateCheckout $root `
                    -AllowDirtyCheckouts `
                    -OutputDirectory $failureRun `
                    -NoBuild `
                    -TestAdapterPath $adapter `
                    -CaptureProfiles `
                    -AnalyzerPath $fixedAnalyzer `
                    -DotnetTracePath $fakeProfileTool
            }
            catch {
                $caseFailureMessage = $_.Exception.Message
                $caseFailed = $_.Exception.Message.Contains(
                    $case.Message,
                    [StringComparison]::OrdinalIgnoreCase)
            }
            Assert-True `
                $caseFailed `
                "Profile outcome '$($case.Name)' was not rejected as expected. Actual: $caseFailureMessage"
            Assert-True `
                ((Get-Content -LiteralPath $measurementMarkers).Count -eq 2) `
                "Profile outcome '$($case.Name)' did not occur after both measured arms."
            Assert-True `
                (Test-Path -LiteralPath (Join-Path $failureRun 'comparison.json')) `
                "Profile outcome '$($case.Name)' discarded the base comparison."
            [object] $failedProfiles = Get-Content -LiteralPath (Join-Path $failureRun 'profiles.json') -Raw | ConvertFrom-Json -Depth 32
            Assert-True `
                ($failedProfiles.status -ceq 'failed') `
                "Profile outcome '$($case.Name)' did not retain failed status."
        }

        [object[]] $qualityCases = @(
            [pscustomobject]@{
                Name = 'low-frame-resolution'
                Mode = 'analysis-low-quality'
                ExpectedStatus = 'insufficientQuality'
                WarningCode = 'low_frame_resolution'
            },
            [pscustomobject]@{
                Name = 'benign-process-scope'
                Mode = 'analysis-benign-warning'
                ExpectedStatus = 'observed'
                WarningCode = 'scope_applied'
            })
        foreach ($qualityCase in $qualityCases) {
            [string] $qualityRun = Join-Path $temporaryRoot "profiles-quality-$($qualityCase.Name)"
            Remove-Item -LiteralPath $measurementMarkers -Force -ErrorAction SilentlyContinue
            $env:FILTRACE_TRACKD_FAKE_PROFILE_MODE = $qualityCase.Mode
            & $script `
                -InputCorpusDirectory $corpus `
                -BaselineCheckout $root `
                -CandidateCheckout $root `
                -AllowDirtyCheckouts `
                -OutputDirectory $qualityRun `
                -NoBuild `
                -TestAdapterPath $adapter `
                -CaptureProfiles `
                -AnalyzerPath $fixedAnalyzer `
                -DotnetTracePath $fakeProfileTool

            [object] $qualityProfiles = Get-Content `
                -LiteralPath (Join-Path $qualityRun 'profiles.json') `
                -Raw | ConvertFrom-Json -Depth 32
            [object[]] $cpuEvidence = @(
                $qualityProfiles.arms.captures.analyses |
                    Where-Object { $_.name -ceq 'cpu' } |
                    ForEach-Object { $_.evidence })
            Assert-True `
                ($cpuEvidence.Count -eq 2) `
                "Quality case '$($qualityCase.Name)' did not retain CPU evidence for both arms."
            Assert-True `
                (@($cpuEvidence | Where-Object { $_.status -cne $qualityCase.ExpectedStatus }).Count -eq 0) `
                "Quality case '$($qualityCase.Name)' recorded the wrong attribution status."
            [object[]] $infoWarnings = @($cpuEvidence.warnings)
            Assert-True `
                (@($infoWarnings | Where-Object { $_.code -ceq $qualityCase.WarningCode }).Count -eq 2) `
                "Quality case '$($qualityCase.Name)' did not preserve info warnings."
            [object[]] $qualityWarnings = @($cpuEvidence.summaries.warnings)
            Assert-True `
                (@($qualityWarnings | Where-Object { $_.code -ceq $qualityCase.WarningCode }).Count -ge 2) `
                "Quality case '$($qualityCase.Name)' did not preserve analyzer warnings."
        }

        [string] $identityRun = Join-Path $temporaryRoot 'profiles-identity-changed'
        Remove-Item -LiteralPath $measurementMarkers -Force -ErrorAction SilentlyContinue
        $env:FILTRACE_TRACKD_FAKE_PROFILE_MODE = 'success'
        $env:FILTRACE_TRACKD_MUTATE_ANALYZER_DLL = $fixedManagedDll
        [bool] $identityFailed = $false
        try {
            & $script `
                -InputCorpusDirectory $corpus `
                -BaselineCheckout $root `
                -CandidateCheckout $root `
                -AllowDirtyCheckouts `
                -OutputDirectory $identityRun `
                -NoBuild `
                -TestAdapterPath $adapter `
                -CaptureProfiles `
                -AnalyzerPath $fixedAnalyzer `
                -DotnetTracePath $fakeProfileTool
        }
        catch {
            $identityFailed = $_.Exception.Message.Contains(
                'Analyzer identity changed',
                [StringComparison]::Ordinal)
        }
        Assert-True $identityFailed 'Profile workflow accepted a changed adjacent managed analyzer DLL.'
    }
    finally {
        $env:FILTRACE_TRACKD_FAKE_PROFILE_MODE = $previousProfileMode
        $env:FILTRACE_TRACKD_FAKE_PROFILE_INVOCATIONS = $previousProfileInvocations
        $env:FILTRACE_TRACKD_FAKE_MEASUREMENT_MARKERS = $previousMeasurementMarkers
        $env:FILTRACE_TRACKD_MUTATE_ANALYZER_DLL = $previousMutationPath
    }

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
    [string] $previousFakeTelemetry = $env:FILTRACE_TRACKD_FAKE_ELAPSED
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
                $invalidTelemetryFailed = $_.Exception.Message.Contains(
                    $invalidTelemetryCase.Message,
                    [StringComparison]::Ordinal)
            }

            [object] $invalidTelemetryStatus = Get-Content `
                -LiteralPath (Join-Path $invalidTelemetryRun 'run-status.json') `
                -Raw | ConvertFrom-Json
            Assert-True `
                $invalidTelemetryFailed `
                "Invalid telemetry case '$caseName' was not rejected as expected."
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
    finally {
        $env:FILTRACE_TRACKD_FAKE_ELAPSED = $previousFakeTelemetry
        $env:FILTRACE_TRACKD_FAKE_INVALID_FIELD = $previousFakeInvalidField
        $env:FILTRACE_TRACKD_FAKE_INVALID_VALUE = $previousFakeInvalidValue
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
