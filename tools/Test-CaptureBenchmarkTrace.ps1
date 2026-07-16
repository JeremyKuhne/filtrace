#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Contract checks for the bundled BenchmarkDotNet capture helper.

.DESCRIPTION
    Runs Capture-BenchmarkTrace.ps1 against fake dotnet and filtrace hosts. The fake
    writes two EventPipe trace pairs and one speedscope-only case into the exact
    --artifacts directory it receives. Stale captures are seeded outside that run to
    prove they cannot be selected.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$captureScript = Join-Path $root '.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1'
# Keep the root short so the long case identities below exercise JSON sizing rather
# than the legacy Windows path-length boundary.
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ftc-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-StringContains(
    [string]$Text,
    [string]$Value,
    [StringComparison]$Comparison = [StringComparison]::OrdinalIgnoreCase) {
    return $null -ne $Text -and $Text.IndexOf($Value, $Comparison) -ge 0
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $help = Get-Help $captureScript -Full | Out-String -Width 4096
    Assert-True (Test-StringContains $help '[-ElevatedChild]') 'ElevatedChild was not discoverable in Get-Help syntax.'
    $captureSource = Get-Content -LiteralPath $captureScript -Raw
    Assert-True (Test-StringContains $captureSource '.PARAMETER ElevatedChild' ([StringComparison]::Ordinal)) 'ElevatedChild has no comment-based help entry.'
    Assert-True (Test-StringContains $captureSource 'Internal switch reserved for the self-elevated ETW child process') 'ElevatedChild help did not identify the switch as internal.'
    Assert-True (Test-StringContains $captureSource 'reports the capture.log path') 'Elevated timeout help did not describe reporting capture.log.'
    foreach ($staleElevationPhrase in @('surfaces the log tail', 'live progress', 'references for progress', 'log still surfaces', 'hang/no-tail')) {
        Assert-True (-not (Test-StringContains $captureSource $staleElevationPhrase)) "Stale ETW elevation wording remained: '$staleElevationPhrase'."
    }

    # Execute the trusted command-builder functions directly so ETW syntax is covered
    # without elevation or a capture side effect.
    $tokens = $null
    $parseErrors = $null
    $captureAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $captureScript,
        [ref]$tokens,
        [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Capture helper could not be parsed for command-contract tests.'
    $commandFunctionNames = @(
        'ConvertTo-RuntimeSummary',
        'ConvertTo-PowerShellArgument',
        'Get-RuntimeSummaries',
        'Test-AnalysisEnabled',
        'Get-CaseCommands',
        'ConvertFrom-BenchmarkNameArgumentFull',
        'ConvertFrom-BenchmarkNameArgument',
        'Get-BenchmarkName',
        'Get-BenchmarkParameters',
        'Get-DefaultCaptureStatuses',
        'ConvertTo-CaptureStatus',
        'Test-HasAnalysisInfo',
        'ConvertTo-AnalysisMap',
        'Get-CaseWarnings')
    $commandFunctionDefinitions = @(
        $captureAst.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $commandFunctionNames },
            $true) |
            Sort-Object { $_.Extent.StartOffset } |
            ForEach-Object { $_.Extent.Text }
    )
    Assert-True ($commandFunctionDefinitions.Count -eq $commandFunctionNames.Count) 'Capture command-builder functions could not be isolated.'
    . ([scriptblock]::Create(($commandFunctionDefinitions -join [Environment]::NewLine)))
    $runtimeLog = Join-Path $temporaryRoot 'runtime-summaries.log'
    [System.IO.File]::WriteAllLines(
        $runtimeLog,
        @(
            '// Runtime=.NET 10.0.9 (10.0.9), X64 RyuJIT',
            'Runtime = .NET 10.0.9 (10.0.9), X64 RyuJIT; GC = Concurrent Workstation',
            '// Runtime=.NET Framework 4.8.1 (4.8.9325.0), X64 RyuJIT',
            'Runtime=.NET 10.0 InvocationCount=1 IterationCount=1'))
    $runtimeSummaries = @(Get-RuntimeSummaries $runtimeLog)
    Assert-True ($runtimeSummaries.Count -eq 2) 'Runtime summaries were not merged per runtime identity.'
    Assert-True (
        $runtimeSummaries -contains
            'Runtime = .NET 10.0.9 (10.0.9), X64 RyuJIT; GC = Concurrent Workstation') `
        'The richer final runtime summary was not retained.'
    Assert-True (
        $runtimeSummaries -contains
            'Runtime = .NET Framework 4.8.1 (4.8.9325.0), X64 RyuJIT') `
        'An unmatched per-case runtime summary was not retained.'
    Assert-True (
        -not ($runtimeSummaries -match 'InvocationCount')) `
        'A BenchmarkDotNet job characteristic row was treated as a runtime summary.'
    Assert-True ((Get-BenchmarkParameters 'FakeBench.Work(Size: 1, Mode: Fast): Job-A') -eq 'Size: 1, Mode: Fast') 'Parameterized benchmark identity was not extracted.'
    Assert-True ((Get-BenchmarkParameters 'BinaryFormattedObjectPerf.Parse: Dry(...) [Scenario=SerializableCallback]') -eq 'Scenario=SerializableCallback') 'Bracketed BenchmarkDotNet parameters were not extracted.'
    Assert-True ((Get-BenchmarkParameters 'FakeBench.Work: Job-A') -eq '') 'Unparameterized benchmark identity was not empty.'
    $escapedBenchmarkName = '\u0026#34;touki.perf.BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize(Scenario: \\\\u0026#34;SerializableCallback\\\\u0026#34;)\u0026#34;'
    Assert-True (
        (ConvertFrom-BenchmarkNameArgument $escapedBenchmarkName) -eq
            'touki.perf.BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize') `
        'Escaped quoted BenchmarkDotNet name and parameters were not decoded.'
    Assert-True (
        (ConvertFrom-BenchmarkNameArgumentFull $escapedBenchmarkName) -eq
            'touki.perf.BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize(Scenario: "SerializableCallback")') `
        'Escaped BenchmarkDotNet full identity was not decoded.'

    $eventPipeDefaults = Get-DefaultCaptureStatuses 'EP' $true
    Assert-True ($eventPipeDefaults.activity -eq 'unknown') 'Default EventPipe capture claimed activity without provider evidence.'
    foreach ($activityStatus in @('enabled', 'disabled', 'unknown')) {
        $traceInfo = [pscustomobject]@{
            analyses = [pscustomobject]@{
                activity = [pscustomobject]@{ captureStatus = $activityStatus; eventCount = $null }
            }
        }
        $analysisMap = ConvertTo-AnalysisMap $traceInfo $eventPipeDefaults $false
        Assert-True ($analysisMap.activity.captureStatus -eq $activityStatus) "Provider activity status '$activityStatus' was not preserved."
    }

    $unidentifiedCase = [ordered]@{
        benchmarkId = $null
        benchmark = $null
        parameters = $null
        benchmarkDisplay = $null
        trace = 'C:\unidentified.nettrace'
        symbolsDirectory = $null
        analyses = [ordered]@{}
    }
    Assert-True (
        @(Get-CaseWarnings $unidentifiedCase) -contains
            'benchmark identity unavailable or ambiguous; do not use this case with manifest batch/diff; analyze the trace directly') `
        'Unidentified benchmark case did not require direct-trace analysis.'

    $eventPipeCase = [ordered]@{
        trace = 'C:\capture.nettrace'
        speedscope = $null
        symbolsDirectory = 'C:\symbols'
        analyses = [ordered]@{}
    }
    foreach ($analysis in @('cpu', 'alloc', 'exceptions', 'contention', 'wait', 'activity', 'gcstats', 'jitstats', 'threadpool')) {
        $eventPipeCase.analyses[$analysis] = [ordered]@{ captureStatus = 'enabled' }
    }
    $eventPipeCommands = @(Get-CaseCommands $eventPipeCase 'EP' 'Fake.Process' 'Fake.Method' 25)
    $expectedEventPipeCommands = @(
        "filtrace cpu 'C:\capture.nettrace' --benchmark --top 25"
        "filtrace lines 'C:\capture.nettrace' --method 'Fake.Method' --symbols 'C:\symbols'"
        "filtrace export 'C:\capture.nettrace' --benchmark --symbols 'C:\symbols' -o flame.speedscope.json"
        "filtrace alloc 'C:\capture.nettrace' --benchmark --top 25"
        "filtrace exceptions 'C:\capture.nettrace' --benchmark --top 25"
        "filtrace rank 'C:\capture.nettrace' --metric contention --benchmark --top 25"
        "filtrace rank 'C:\capture.nettrace' --metric wait --benchmark --top 25"
        "filtrace rank 'C:\capture.nettrace' --metric activity --benchmark --top 25"
        "filtrace gcstats 'C:\capture.nettrace'"
        "filtrace jitstats 'C:\capture.nettrace'"
        "filtrace threadpool 'C:\capture.nettrace'"
    )
    Assert-True (($eventPipeCommands -join "`n") -ceq ($expectedEventPipeCommands -join "`n")) "EventPipe command syntax drifted.`nActual:`n$($eventPipeCommands -join "`n")"

    $etwCase = [ordered]@{
        trace = 'C:\capture.etl'
        speedscope = $null
        symbolsDirectory = 'C:\symbols'
        analyses = [ordered]@{}
    }
    foreach ($analysis in @('processes', 'cpu', 'threadtime', 'classify', 'diskio')) {
        $etwCase.analyses[$analysis] = [ordered]@{ captureStatus = 'enabled' }
    }
    $etwCommands = @(Get-CaseCommands $etwCase 'ETW' 'Fake.Process' 'Fake.Method' 25)
    $expectedEtwCommands = @(
        "filtrace processes 'C:\capture.etl'"
        "filtrace cpu 'C:\capture.etl' --process 'Fake.Process' --benchmark --top 25"
        "filtrace lines 'C:\capture.etl' --process 'Fake.Process' --method 'Fake.Method' --symbols 'C:\symbols'"
        "filtrace export 'C:\capture.etl' --process 'Fake.Process' --benchmark --native-symbols --symbols 'C:\symbols' -o flame.speedscope.json"
        "filtrace threadtime 'C:\capture.etl' --process 'Fake.Process' --benchmark --top 25"
        "filtrace classify 'C:\capture.etl' --process 'Fake.Process' --benchmark --native-symbols"
        "filtrace diskio 'C:\capture.etl' --top 25"
    )
    Assert-True (($etwCommands -join "`n") -ceq ($expectedEtwCommands -join "`n")) "ETW command syntax drifted.`nActual:`n$($etwCommands -join "`n")"

    $projectDirectory = Join-Path $temporaryRoot 'src/Fake.Perf'
    New-Item -ItemType Directory -Path $projectDirectory | Out-Null
    $projectPath = Join-Path $projectDirectory 'Fake.Perf.csproj'
    [System.IO.File]::WriteAllText($projectPath, '<Project Sdk="Microsoft.NET.Sdk" />')

    $fakeDotnet = Join-Path $temporaryRoot 'Fake-Dotnet.ps1'
    $fakeDotnetText = @'
$artifactIndex = [Array]::IndexOf($args, '--artifacts')
if ($artifactIndex -lt 0 -or $artifactIndex + 1 -ge $args.Count) {
    throw 'Capture helper did not pass --artifacts.'
}
$artifacts = $args[$artifactIndex + 1]
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$childSymbols = $env:FILTRACE_CAPTURE_CHILD_SYMBOLS
New-Item -ItemType Directory -Force -Path $childSymbols | Out-Null
[System.IO.File]::WriteAllText((Join-Path $childSymbols 'Fake-Job.pdb'), 'pdb')
$isToukiRun = $artifacts -like '*touki-run*'
$isIdentityWarningRun = $artifacts -like '*identity-warning-run*'
$toukiScenarios = @(
    'Int32Array_1K',
    'StringList_128',
    'CustomObject',
    'ObjectTree_127',
    'SharedCycle_128',
    'SerializableCallback')
if ($artifacts -like '*oversize-run*') {
    for ($index = 0; $index -lt 64; $index++) {
        $timestamp = [DateTime]::new(2026, 7, 13, 2, 0, 0, [DateTimeKind]::Utc).AddSeconds($index)
        $name = "Fake.Bench.Oversize.WithLongParameterName$($index.ToString('D3'))-$($timestamp.ToString('yyyyMMdd-HHmmss')).nettrace"
        [System.IO.File]::WriteAllText((Join-Path $artifacts $name), 'oversize raw')
    }
}
elseif ($artifacts -like '*handoff-budget-run*') {
    $padding = [string]::new('x', 80)
    for ($index = 0; $index -lt 4; $index++) {
        $timestamp = [DateTime]::new(2026, 7, 13, 3, 0, 0, [DateTimeKind]::Utc).AddSeconds($index)
        $name = "Fake.Bench.Handoff.$padding.$($index.ToString('D2'))-$($timestamp.ToString('yyyyMMdd-HHmmss')).nettrace"
        [System.IO.File]::WriteAllText((Join-Path $artifacts $name), 'handoff budget raw')
    }
}
elseif ($isToukiRun) {
    for ($index = 0; $index -lt $toukiScenarios.Count; $index++) {
        $timestamp = [DateTime]::new(2026, 7, 15, 1, 0, 0, [DateTimeKind]::Utc).AddSeconds($index)
        $scenario = $toukiScenarios[$index]
        $name = if ($scenario -eq 'SerializableCallback') {
            "BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize-hash1429199657-$($timestamp.ToString('yyyyMMdd-HHmmss')).nettrace"
        }
        else {
            "touki.perf.BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize(Scenario_ _$scenario_)-$($timestamp.ToString('yyyyMMdd-HHmmss')).nettrace"
        }
        [System.IO.File]::WriteAllText((Join-Path $artifacts $name), 'touki parameterized raw')
    }
}
elseif ($isIdentityWarningRun) {
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Bench.Ambiguous-hash123456789-20260715-020000.nettrace'), 'ambiguous raw')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Bench.Missing-hash987654321-20260715-020001.nettrace'), 'missing raw')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Bench.ScopeOnly-hash555555555-20260715-020002.speedscope.json'), 'scope only')
}
else {
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010203.nettrace'), 'current raw')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010203.speedscope.json'), 'current speedscope')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010204.nettrace'), 'second parameter raw')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010204.speedscope.json'), 'second parameter speedscope')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Other-20260713-010205.nettrace'), 'other raw')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Other-20260713-010205.speedscope.json'), 'other speedscope')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.ScopeOnly-20260713-010206.speedscope.json'), 'scope only')
}
foreach ($file in Get-ChildItem -LiteralPath $artifacts -File) {
    if ($file.Name -match '-(\d{8})-(\d{6})\.') {
        $timestamp = [DateTime]::ParseExact(
            "$($Matches[1])$($Matches[2])",
            'yyyyMMddHHmmss',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
        [System.IO.File]::SetLastWriteTimeUtc($file.FullName, $timestamp)
    }
}
[System.IO.File]::WriteAllLines($env:FILTRACE_CAPTURE_ARGS, $args)
$global:LASTEXITCODE = 0
Write-Output "// start dotnet build /p:OutDir=`"$childSymbols`""
Write-Output '// malicious build output /p:OutDir="\\evil.example\share\symbols"'
if ($isToukiRun) {
    $outerQuote = '"'
    $escapedQuote = '\"'
    for ($index = 0; $index -lt $toukiScenarios.Count; $index++) {
        $scenario = $toukiScenarios[$index]
        Write-Output "// Benchmark: BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize: DefaultJob [Scenario=$scenario]"
        $benchmarkName = "touki.perf.BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize(Scenario: $escapedQuote$scenario$escapedQuote)"
        Write-Output "// Execute: dotnet Fake-Job.dll --benchmarkName $outerQuote$benchmarkName$outerQuote --job Default --diagnoserRunMode 3 --benchmarkId $index in fake"
        Write-Output '// Runtime=.NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3'
    }
    Write-Output 'Runtime = .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3; GC = Concurrent Workstation'
}
elseif ($isIdentityWarningRun) {
    Write-Output '// Benchmark: FakeBench.Ambiguous: Job-A [Scenario=A]'
    Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName "One.Bench.Ambiguous(Scenario: \"A\")" --job Job-A --benchmarkId 20 in fake'
    Write-Output '// Benchmark: FakeBench.Ambiguous: Job-B [Scenario=A]'
    Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName "One.Bench.Ambiguous(Scenario: \"A\")" --job Job-B --benchmarkId 20 in fake'
    Write-Output '// Runtime=.NET 10.0.9, X64 RyuJIT x86-64-v3'
}
else {
    Write-Output '// Benchmark: FakeBench.Work(Size: 1): Job-A'
    Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName Fake.Bench.Work --job Job-A --benchmarkId 10 in fake'
    Write-Output '// Benchmark: FakeBench.Work(Size: 2): Job-A'
    Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName Fake.Bench.Work --job Job-A --benchmarkId 11 in fake'
    Write-Output '// Benchmark: FakeBench.Other(Size: 2): Job-A'
    Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName Fake.Bench.Other --job Job-A --benchmarkId 4 in fake'
    Write-Output '// Benchmark: FakeBench.ScopeOnly(Size: 3): Job-A'
    Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName Fake.Bench.ScopeOnly --job Job-A --benchmarkId 7 in fake'
    Write-Output 'Runtime = .NET 10.0.0, X64 RyuJIT'
}
Write-Output 'fake BenchmarkDotNet capture completed'
'@
    [System.IO.File]::WriteAllText($fakeDotnet, $fakeDotnetText)

    $fakeFiltrace = Join-Path $temporaryRoot 'Fake-Filtrace.ps1'
    $fakeFiltraceText = @'
if ($args[0] -eq '--version') {
    if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'old-version') {
        Write-Output '0.5.0'
    }
    else {
        Write-Output '0.6.0'
    }
    $global:LASTEXITCODE = 0
    return
}
$symbolIndex = [Array]::IndexOf($args, '--symbols')
$symbols = if ($symbolIndex -ge 0) { $args[$symbolIndex + 1] } else { '' }
$isExact = $symbols -eq $env:FILTRACE_CAPTURE_CHILD_SYMBOLS
$tracePath = if ($args.Count -gt 1) { $args[1] } else { '' }
$isSpeedscope = $tracePath -like '*.speedscope.json'
$isPreflight = [System.IO.Path]::GetFileName($tracePath) -like 'filtrace-preflight-*.speedscope.json'
if ($isPreflight) {
    if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'missing-result') {
        [ordered]@{ schemaVersion = 8 } | ConvertTo-Json -Compress
        $global:LASTEXITCODE = 0
        return
    }
    if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'missing-analyses') {
        [ordered]@{ schemaVersion = 8; result = [ordered]@{} } | ConvertTo-Json -Compress
        $global:LASTEXITCODE = 0
        return
    }
    if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'missing-cpu') {
        [ordered]@{
            schemaVersion = 8
            result = [ordered]@{ analyses = [ordered]@{} }
        } | ConvertTo-Json -Depth 4 -Compress
        $global:LASTEXITCODE = 0
        return
    }
    if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'incomplete-cpu') {
        [ordered]@{
            schemaVersion = 8
            result = [ordered]@{
                analyses = [ordered]@{
                    cpu = [ordered]@{ captureStatus = 'enabled' }
                }
            }
        } | ConvertTo-Json -Depth 6 -Compress
        $global:LASTEXITCODE = 0
        return
    }
    if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'array-result') {
        [ordered]@{
            schemaVersion = 8
            result = @(
                [ordered]@{
                    analyses = [ordered]@{
                        cpu = [ordered]@{ captureStatus = 'enabled'; eventCount = 1 }
                    }
                }
            )
        } | ConvertTo-Json -Depth 7 -Compress
        $global:LASTEXITCODE = 0
        return
    }
    if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'array-analyses') {
        [ordered]@{
            schemaVersion = 8
            result = [ordered]@{
                analyses = @(
                    [ordered]@{
                        cpu = [ordered]@{ captureStatus = 'enabled'; eventCount = 1 }
                    }
                )
            }
        } | ConvertTo-Json -Depth 7 -Compress
        $global:LASTEXITCODE = 0
        return
    }
    if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'array-cpu') {
        [ordered]@{
            schemaVersion = 8
            result = [ordered]@{
                analyses = [ordered]@{
                    cpu = @([ordered]@{ captureStatus = 'enabled'; eventCount = 1 })
                }
            }
        } | ConvertTo-Json -Depth 7 -Compress
        $global:LASTEXITCODE = 0
        return
    }
    $schemaVersion = if ($env:FILTRACE_CAPTURE_PREFLIGHT_MODE -eq 'incompatible-schema') { 7 } else { 8 }
    [ordered]@{
        schemaVersion = $schemaVersion
        warnings = @()
        hints = @()
        result = [ordered]@{
            analyses = [ordered]@{
                cpu = [ordered]@{ captureStatus = 'enabled'; eventCount = 1 }
            }
        }
    } | ConvertTo-Json -Depth 6 -Compress
    $global:LASTEXITCODE = 0
    return
}
if ($args[0] -eq 'events') {
    if ($env:FILTRACE_CAPTURE_EVENTS_MODE -eq 'failure') {
        $global:LASTEXITCODE = 1
        return
    }
    if ($env:FILTRACE_CAPTURE_EVENTS_MODE -eq 'malformed') {
        Write-Output '{not json'
        $global:LASTEXITCODE = 0
        return
    }
    if ($env:FILTRACE_CAPTURE_EVENTS_MODE -eq 'incomplete') {
        [ordered]@{ schemaVersion = 8; result = [ordered]@{} } | ConvertTo-Json -Compress
        $global:LASTEXITCODE = 0
        return
    }
    $event = $null
    if ($tracePath -like '*touki-run*' -and
        $tracePath -match '-(\d{8})-(\d{6})\.nettrace$') {
        $scenarioIndex = [int]$Matches[2].Substring(4, 2)
        $scenarios = @(
            'Int32Array_1K',
            'StringList_128',
            'CustomObject',
            'ObjectTree_127',
            'SharedCycle_128',
            'SerializableCallback')
        if ($scenarioIndex -ge 0 -and $scenarioIndex -lt $scenarios.Count) {
            $scenario = $scenarios[$scenarioIndex]
            $event = [ordered]@{
                provider = 'BenchmarkDotNet.EngineEventSource'
                eventName = 'Benchmark/Start'
                payload = "benchmarkName=touki.perf.BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize(Scenario: `"$scenario`")"
            }
        }
    }
    elseif ($tracePath -like '*identity-warning-run*' -and
        [System.IO.Path]::GetFileName($tracePath) -like 'Bench.Ambiguous-hash*.nettrace') {
        $event = [ordered]@{
            provider = 'BenchmarkDotNet.EngineEventSource'
            eventName = 'Benchmark/Start'
            payload = 'benchmarkName=One.Bench.Ambiguous(Scenario: "A")'
        }
    }
    [ordered]@{
        schemaVersion = 8
        warnings = @()
        hints = @()
        result = [ordered]@{
            totalMatched = if ($env:FILTRACE_CAPTURE_EVENTS_MODE -eq 'truncated') {
                2
            }
            elseif ($null -eq $event) {
                0
            }
            else {
                1
            }
            events = if ($env:FILTRACE_CAPTURE_EVENTS_MODE -eq 'duplicate') {
                @($event, $event)
            }
            elseif ($null -eq $event) {
                @()
            }
            else {
                @($event)
            }
        }
    } | ConvertTo-Json -Depth 6 -Compress
    $global:LASTEXITCODE = 0
    return
}
if ($env:FILTRACE_CAPTURE_INFO_FAILURE -eq '1') {
    $global:LASTEXITCODE = 1
    return
}
$source = [ordered]@{
    sampledManagedFrameCount = 100
    mappedManagedFrameCount = if ($isExact) { 75 } else { 0 }
    matchingPdbModules = if ($isExact) { @('Fake-Job') } else { @() }
}
$analyses = if ($tracePath -like '*handoff-budget-run*') {
    $handoffAnalyses = [ordered]@{
        cpu = [ordered]@{ captureStatus = 'enabled'; eventCount = 0 }
    }
    for ($index = 0; $index -lt 23; $index++) {
        $handoffAnalyses["unknown$($index.ToString('D2'))"] = [ordered]@{
            captureStatus = 'unknown'
            eventCount = $null
        }
    }
    $handoffAnalyses
}
elseif ($isSpeedscope) {
    [ordered]@{ cpu = [ordered]@{ captureStatus = 'enabled'; eventCount = 0 } }
}
else {
    [ordered]@{
        cpu = [ordered]@{ captureStatus = 'enabled'; eventCount = 0 }
        alloc = [ordered]@{ captureStatus = 'disabled'; eventCount = $null }
        exceptions = [ordered]@{ captureStatus = 'unknown'; eventCount = $null }
        contention = [ordered]@{ captureStatus = 'enabled'; eventCount = 2 }
        wait = [ordered]@{ captureStatus = ''; eventCount = $null }
        activity = [ordered]@{ captureStatus = 'future-state'; eventCount = $null }
        gcstats = [ordered]@{ captureStatus = 'enabled'; eventCount = 0 }
        jitstats = [ordered]@{ eventCount = $null }
        threadpool = [ordered]@{ captureStatus = 'enabled'; eventCount = 1 }
        events = [ordered]@{ captureStatus = 'enabled'; eventCount = 10 }
    }
}
[ordered]@{
    schemaVersion = 8
    warnings = @()
    hints = @()
    result = [ordered]@{ sourceResolution = $source; analyses = $analyses }
} | ConvertTo-Json -Depth 6 -Compress
$global:LASTEXITCODE = 0
'@
    [System.IO.File]::WriteAllText($fakeFiltrace, $fakeFiltraceText)

    $globalArtifacts = Join-Path $temporaryRoot 'BenchmarkDotNet.Artifacts'
    $oldRunArtifacts = Join-Path $globalArtifacts 'filtrace-runs/old-run/artifacts'
    New-Item -ItemType Directory -Force -Path $oldRunArtifacts | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $globalArtifacts 'stale-global.nettrace'), 'stale')
    [System.IO.File]::WriteAllText((Join-Path $oldRunArtifacts 'stale-old-run.nettrace'), 'stale')

    $argsPath = Join-Path $temporaryRoot 'fake-dotnet-args.txt'
    $previousArgsPath = $env:FILTRACE_CAPTURE_ARGS
    $previousChildSymbols = $env:FILTRACE_CAPTURE_CHILD_SYMBOLS
    $childSymbols = Join-Path $projectDirectory 'custom-output/Fake-Job/bin/Release/net10.0'
    $previousPreflightMode = $env:FILTRACE_CAPTURE_PREFLIGHT_MODE
    $hostExe = (Get-Process -Id $PID).Path
    try {
        $preflightCases = @(
            [ordered]@{
                mode = 'old-version'
                runId = 'old-version-run'
                expected = 'older than required version 0.6.0'
            },
            [ordered]@{
                mode = 'incompatible-schema'
                runId = 'incompatible-schema-run'
                expected = 'did not match schema 8'
            },
            [ordered]@{
                mode = 'missing-result'
                runId = 'missing-result-run'
                expected = 'did not match schema 8'
            },
            [ordered]@{
                mode = 'missing-analyses'
                runId = 'missing-analyses-run'
                expected = 'did not match schema 8'
            },
            [ordered]@{
                mode = 'missing-cpu'
                runId = 'missing-cpu-run'
                expected = 'did not match schema 8'
            },
            [ordered]@{
                mode = 'incomplete-cpu'
                runId = 'incomplete-cpu-run'
                expected = 'did not match schema 8'
            },
            [ordered]@{
                mode = 'array-result'
                runId = 'array-result-run'
                expected = 'did not match schema 8'
            },
            [ordered]@{
                mode = 'array-analyses'
                runId = 'array-analyses-run'
                expected = 'did not match schema 8'
            },
            [ordered]@{
                mode = 'array-cpu'
                runId = 'array-cpu-run'
                expected = 'did not match schema 8'
            }
        )
        foreach ($preflightCase in $preflightCases) {
            $preflightArgsPath = Join-Path $temporaryRoot "$($preflightCase.mode)-dotnet-args.txt"
            $env:FILTRACE_CAPTURE_ARGS = $preflightArgsPath
            $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
            $env:FILTRACE_CAPTURE_PREFLIGHT_MODE = $preflightCase.mode
            Push-Location $temporaryRoot
            try {
                $previousErrorActionPreference = $ErrorActionPreference
                try {
                    $ErrorActionPreference = 'Continue'
                    $preflightOutput = @(
                        & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
                            -RunId $preflightCase.runId -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 |
                            ForEach-Object { $_.ToString() }
                    ) -join [Environment]::NewLine
                    $preflightExitCode = $LASTEXITCODE
                }
                finally {
                    $ErrorActionPreference = $previousErrorActionPreference
                }
            }
            finally {
                Pop-Location
            }

            Assert-True ($preflightExitCode -ne 0) "Incompatible filtrace mode '$($preflightCase.mode)' was accepted."
            Assert-True (Test-StringContains $preflightOutput $preflightCase.expected) "Incompatible filtrace mode '$($preflightCase.mode)' did not explain the failed contract."
            Assert-True (
                (Test-StringContains $preflightOutput 'Upgrade') -and
                (Test-StringContains $preflightOutput 'KlutzyNinja.Filtrace')) `
                "Incompatible filtrace mode '$($preflightCase.mode)' omitted upgrade guidance. Output: $preflightOutput"
            Assert-True (-not (Test-Path -LiteralPath $preflightArgsPath)) "Incompatible filtrace mode '$($preflightCase.mode)' started BenchmarkDotNet."
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts "filtrace-runs/$($preflightCase.runId)"))) "Incompatible filtrace mode '$($preflightCase.mode)' created a run directory."
        }
    }
    finally {
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
        $env:FILTRACE_CAPTURE_PREFLIGHT_MODE = $previousPreflightMode
    }

    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $global:LASTEXITCODE = 0
        $output = & $captureScript -Project $projectPath -Filter '*Work*' -RunId 'current-run' `
            -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
            -OperationCount 100 -OperationUnit items *>&1 | Out-String -Width 4096
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }

    $currentRun = Join-Path $globalArtifacts 'filtrace-runs/current-run'
    $currentArtifacts = Join-Path $currentRun 'artifacts'
    $currentTrace = Join-Path $currentArtifacts 'Fake.Bench.Work-20260713-010203.nettrace'
    $secondParameterTrace = Join-Path $currentArtifacts 'Fake.Bench.Work-20260713-010204.nettrace'
    $otherTrace = Join-Path $currentArtifacts 'Fake.Bench.Other-20260713-010205.nettrace'
    $scopeOnly = Join-Path $currentArtifacts 'Fake.Bench.ScopeOnly-20260713-010206.speedscope.json'
    Assert-True (Test-Path -LiteralPath (Join-Path $currentRun 'capture.log')) 'Run-specific capture.log was not created.'
    Assert-True (Test-Path -LiteralPath "$currentTrace.filtrace.json") 'Capture metadata was not written beside the current trace.'
    Assert-True (Test-Path -LiteralPath "$secondParameterTrace.filtrace.json") 'Capture metadata was not written beside the second parameter trace.'
    Assert-True (Test-Path -LiteralPath "$otherTrace.filtrace.json") 'Capture metadata was not written beside the other trace.'
    Assert-True (Test-StringContains $output $currentTrace) 'Current-run trace was not selected.'
    Assert-True (Test-StringContains $output $secondParameterTrace) 'Second parameter trace was not reported.'
    Assert-True (Test-StringContains $output $otherTrace) 'Other current-run trace was not reported.'
    Assert-True (Test-StringContains $output $scopeOnly) 'Speedscope-only case was not reported.'
    Assert-True (-not (Test-StringContains $output 'stale-global')) 'A stale global trace was selected.'
    Assert-True (-not (Test-StringContains $output 'stale-old-run')) 'A stale prior-run trace was selected.'

    [string[]]$dotnetArguments = [System.IO.File]::ReadAllLines($argsPath)
    $artifactIndex = [Array]::IndexOf($dotnetArguments, '--artifacts')
    Assert-True ($artifactIndex -ge 0) 'Fake dotnet did not receive --artifacts.'
    Assert-True ($dotnetArguments[$artifactIndex + 1] -eq $currentArtifacts) 'Fake dotnet received the wrong artifacts directory.'

    $manifestPath = Join-Path $currentRun 'manifest.json'
    Assert-True (Test-Path -LiteralPath $manifestPath) 'Run manifest was not created.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-True ($manifest.schemaVersion -eq 1) 'Run manifest schema version is incorrect.'
    Assert-True ($manifest.runId -eq 'current-run') 'Run manifest ID is incorrect.'
    Assert-True ($manifest.cases.Count -eq 4) 'Run manifest did not include every capture case.'
    Assert-True ($manifest.paths.artifactsDirectory -eq $currentArtifacts) 'Run manifest artifacts path is incorrect.'
    Assert-True ($manifest.cases[0].benchmarkId -eq 10) 'First BenchmarkDotNet case ID was not paired by name and execution order.'
    Assert-True ($manifest.cases[0].benchmark -eq 'Fake.Bench.Work') 'First benchmark name was not recorded.'
    Assert-True ($manifest.cases[0].parameters -eq 'Size: 1') 'First benchmark parameters were not recorded.'
    Assert-True ($manifest.cases[0].benchmarkDisplay -eq 'FakeBench.Work(Size: 1): Job-A') 'First parameterized display was not recorded.'
    Assert-True ($manifest.cases[1].benchmarkId -eq 11) 'Second parameter ID was not paired in execution order.'
    Assert-True ($manifest.cases[1].parameters -eq 'Size: 2') 'Second benchmark parameters were not recorded.'
    Assert-True ($manifest.cases[1].benchmarkDisplay -eq 'FakeBench.Work(Size: 2): Job-A') 'Second parameterized display was not recorded.'
    Assert-True ($manifest.cases[2].benchmarkId -eq 4) 'Different benchmark ID was not paired by stable name.'
    Assert-True ($manifest.cases[2].benchmarkDisplay -eq 'FakeBench.Other(Size: 2): Job-A') 'Different benchmark display was not recorded.'
    Assert-True ($manifest.cases[3].benchmarkId -eq 7) 'Speedscope-only BenchmarkDotNet case ID was not paired.'
    Assert-True ($manifest.cases[3].benchmarkDisplay -eq 'FakeBench.ScopeOnly(Size: 3): Job-A') 'Speedscope-only display was not recorded.'
    foreach ($captureCase in $manifest.cases) {
        Assert-True ($captureCase.operationCount -eq 100) "Operation count was not recorded for case '$($captureCase.id)'."
        Assert-True ($captureCase.operationUnit -eq 'items') "Operation unit was not recorded for case '$($captureCase.id)'."
    }
    Assert-True ($null -eq $manifest.cases[3].trace) 'Speedscope-only case unexpectedly gained a raw trace.'
    Assert-True ($manifest.cases[3].speedscope -eq $scopeOnly) 'Speedscope-only case path was not recorded.'
    Assert-True ($null -eq $manifest.cases[3].symbolsDirectory) 'Speedscope-only case unexpectedly gained source symbols.'
    Assert-True ($manifest.runtimes.Count -eq 1) 'Runtime identity was not recorded.'
    Assert-True ($null -eq $manifest.cases[3].runtime) 'Manifest-wide runtime summary was assigned to the last case.'
    Assert-True ($manifest.cases[0].symbolsDirectory -eq $childSymbols) 'Exact child symbols were not discovered.'
    Assert-True ($manifest.cases[1].symbolsDirectory -eq $childSymbols) 'Exact child symbols were not paired with every matching trace.'
    Assert-True ($manifest.cases[2].symbolsDirectory -eq $childSymbols) 'Exact child symbols were not paired with the other trace.'
    Assert-True ($manifest.cases[0].analyses.cpu.captureStatus -eq 'enabled') 'CPU capture status was not recorded.'
    Assert-True ($manifest.cases[0].analyses.cpu.eventCount -eq 0) 'Enabled-zero CPU count was not recorded.'
    Assert-True ($manifest.cases[0].commands -contains "filtrace cpu '$currentTrace' --benchmark --top 25") 'Enabled-zero CPU command was not recorded.'
    Assert-True (-not ($manifest.cases[0].commands -match '^filtrace alloc ')) 'Disabled allocation command entered the manifest.'
    Assert-True (-not ($manifest.cases[0].commands -match '^filtrace exceptions ')) 'Unknown exceptions command entered the manifest.'
    Assert-True ($manifest.cases[0].warnings -contains 'alloc capture disabled; recapture with a profile that enables it') 'Disabled allocation warning was not recorded.'
    Assert-True ($manifest.cases[0].warnings -contains 'exceptions capture status unknown; no command emitted') 'Unknown exceptions warning was not recorded.'
    Assert-True ($manifest.cases[0].analyses.wait.captureStatus -eq 'unknown') 'Empty capture status was not normalized to unknown.'
    Assert-True ($manifest.cases[0].analyses.activity.captureStatus -eq 'unknown') 'Unexpected capture status was not normalized to unknown.'
    Assert-True ($manifest.cases[0].analyses.jitstats.captureStatus -eq 'unknown') 'Missing capture status was not normalized to unknown.'
    Assert-True ($manifest.cases[0].warnings -contains 'wait capture status unknown; no command emitted') 'Normalized empty capture status did not emit a warning.'
    Assert-True ($manifest.cases[0].warnings -contains 'activity capture status unknown; no command emitted') 'Normalized unexpected capture status did not emit a warning.'
    Assert-True ($manifest.cases[0].warnings -contains 'jitstats capture status unknown; no command emitted') 'Normalized missing capture status did not emit a warning.'
    Assert-True ($manifest.cases[0].symbolCandidates -contains $childSymbols) 'Logged child output was not recorded as a symbol candidate.'
    Assert-True (-not (($manifest.cases[0].symbolCandidates -join ';') -match 'evil\.example')) 'Remote logged OutDir entered symbol candidates.'
    Assert-True (Test-StringContains $output $childSymbols) 'Printed source command did not use exact child symbols.'
    Assert-True (([regex]::Matches($output, 'filtrace lines ')).Count -eq 3) 'Expected one filtrace lines command per raw trace and none for the speedscope-only case.'
    Assert-True (([regex]::Matches($output, 'filtrace cpu ')).Count -eq 4) 'Enabled-zero CPU did not emit one command per case.'
    Assert-True (([regex]::Matches($output, 'filtrace rank .*--metric contention')).Count -eq 3) 'Enabled contention did not emit one command per raw trace.'
    Assert-True (([regex]::Matches($output, 'filtrace gcstats ')).Count -eq 3) 'Enabled-zero GC did not emit one command per raw trace.'
    Assert-True (([regex]::Matches($output, 'filtrace threadpool ')).Count -eq 3) 'Enabled threadpool did not emit one command per raw trace.'
    $generatedCommands = @(
        $manifest.cases.commands |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    )
    foreach ($command in $generatedCommands) {
        if ($command -match '^filtrace (?:gcstats|jitstats|threadpool|processes)\b') {
            Assert-True ($command -notmatch '(?:^|\s)--(?:benchmark|process)(?:\s|$)') "Structured/orientation command used an unsupported stack scope: $command"
        }
        if ($command -match '^filtrace lines\b') {
            Assert-True ($command -notmatch '(?:^|\s)--benchmark(?:\s|$)') "Source-line command used unsupported benchmark scope: $command"
        }
    }
    Assert-True (-not (Test-StringContains $output 'filtrace alloc ')) 'Disabled allocation emitted a command.'
    Assert-True (-not (Test-StringContains $output 'filtrace exceptions ')) 'Unknown exceptions emitted a command.'
    Assert-True (Test-StringContains $output 'alloc capture disabled') 'Disabled allocation was not explained.'
    Assert-True (Test-StringContains $output 'exceptions capture status unknown') 'Unknown exceptions were not explained.'
    Assert-True (-not (Test-StringContains $output 'fake BenchmarkDotNet capture completed')) 'BenchmarkDotNet chatter leaked to stdout.'
    Assert-True (Test-StringContains (Get-Content -LiteralPath (Join-Path $currentRun 'capture.log') -Raw) 'fake BenchmarkDotNet capture completed') 'Full BenchmarkDotNet output was not retained in capture.log.'
    Assert-True (-not (($manifest | ConvertTo-Json -Depth 8) -match 'stale-global|stale-old-run')) 'Stale captures entered the manifest.'

    $hostExe = (Get-Process -Id $PID).Path
    $toukiArgsPath = Join-Path $temporaryRoot 'touki-dotnet-args.txt'
    $missingFiltrace = Join-Path $temporaryRoot 'missing-filtrace'
    $env:FILTRACE_CAPTURE_ARGS = $toukiArgsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $toukiOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*BinaryFormattedObject*' `
            -RunId 'touki-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json | Out-String -Width 4096
        $toukiExitCode = $LASTEXITCODE
        $identityWarningOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Identity*' `
            -RunId 'identity-warning-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json | Out-String -Width 4096
        $identityWarningExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }

    Assert-True ($toukiExitCode -eq 0) "Touki parameterized capture failed: $($toukiOutput.Trim())"
    $toukiManifestPath = Join-Path $globalArtifacts 'filtrace-runs/touki-run/manifest.json'
    $toukiManifest = Get-Content -LiteralPath $toukiManifestPath -Raw | ConvertFrom-Json
    Assert-True ($toukiManifest.cases.Count -eq 6) 'Touki parameterized capture did not emit six cases.'
    $expectedToukiScenarios = @(
        'Int32Array_1K',
        'StringList_128',
        'CustomObject',
        'ObjectTree_127',
        'SharedCycle_128',
        'SerializableCallback')
    for ($index = 0; $index -lt $expectedToukiScenarios.Count; $index++) {
        $toukiCase = $toukiManifest.cases[$index]
        Assert-True ($toukiCase.benchmarkId -eq $index) "Touki case $index was paired with the wrong BenchmarkDotNet ID."
        Assert-True ($toukiCase.benchmark -eq 'touki.perf.BinaryFormattedObjectPerf.BinaryFormattedObject_ParseAndDeserialize') "Touki case $index lost its benchmark identity."
        Assert-True ($toukiCase.parameters -eq "Scenario=$($expectedToukiScenarios[$index])") "Touki case $index lost its parameter identity."
        Assert-True (-not [string]::IsNullOrWhiteSpace($toukiCase.benchmarkDisplay)) "Touki case $index lost its display identity."
        Assert-True (
            $toukiCase.runtime -eq
                'Runtime = .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3') `
            "Touki case $index lost its canonical runtime identity."
    }
    Assert-True ($toukiManifest.runtimes.Count -eq 1) 'Touki runtime metadata was not recorded.'
    Assert-True (
        $toukiManifest.runtimes[0] -eq
            'Runtime = .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3; GC = Concurrent Workstation') `
        'Touki runtime metadata did not prefer the richer final summary.'

    Assert-True ($identityWarningExitCode -eq 0) "Incomplete-identity capture failed: $($identityWarningOutput.Trim())"
    $identityWarningManifestPath = Join-Path $globalArtifacts 'filtrace-runs/identity-warning-run/manifest.json'
    $identityWarningManifest = Get-Content -LiteralPath $identityWarningManifestPath -Raw | ConvertFrom-Json
    Assert-True ($identityWarningManifest.cases.Count -eq 3) 'Incomplete-identity fixture did not emit every case.'
    Assert-True ($identityWarningManifest.runtimes.Count -eq 1) 'Per-case runtime fallback was not recorded.'
    Assert-True (
        $identityWarningManifest.runtimes[0] -eq 'Runtime = .NET 10.0.9, X64 RyuJIT x86-64-v3') `
        'Per-case runtime fallback was not canonicalized.'
    foreach ($identityWarningCase in $identityWarningManifest.cases) {
        Assert-True ($null -eq $identityWarningCase.benchmarkId) "Unidentified case '$($identityWarningCase.id)' was silently paired."
        Assert-True ($null -eq $identityWarningCase.benchmark) "Unidentified case '$($identityWarningCase.id)' gained a benchmark name."
        Assert-True (@($identityWarningCase.warnings) -contains 'benchmark identity unavailable or ambiguous; do not use this case with manifest batch/diff; analyze the trace directly') "Unidentified case '$($identityWarningCase.id)' omitted direct-trace guidance."
    }

    $previousEventsMode = $env:FILTRACE_CAPTURE_EVENTS_MODE
    $identityFailureModes = @('failure', 'malformed', 'incomplete', 'duplicate', 'truncated', 'absent')
    try {
        foreach ($identityFailureMode in $identityFailureModes) {
            $failureRunId = "identity-warning-run-$identityFailureMode"
            $env:FILTRACE_CAPTURE_ARGS = Join-Path $temporaryRoot "$failureRunId-dotnet-args.txt"
            $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
            $env:FILTRACE_CAPTURE_EVENTS_MODE = if ($identityFailureMode -eq 'absent') {
                $null
            }
            else {
                $identityFailureMode
            }
            $failureFiltrace = if ($identityFailureMode -eq 'absent') {
                $missingFiltrace
            }
            else {
                $fakeFiltrace
            }
            Push-Location $temporaryRoot
            try {
                $failureOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Identity*' `
                    -RunId $failureRunId -DotnetPath $fakeDotnet -FiltracePath $failureFiltrace -Format Json | Out-String -Width 4096
                $failureExitCode = $LASTEXITCODE
            }
            finally {
                Pop-Location
            }
            Assert-True ($failureExitCode -eq 0) "Identity mode '$identityFailureMode' capture failed: $($failureOutput.Trim())"
            $failureManifestPath = Join-Path $globalArtifacts "filtrace-runs/$failureRunId/manifest.json"
            $failureManifest = Get-Content -LiteralPath $failureManifestPath -Raw | ConvertFrom-Json
            Assert-True ($failureManifest.cases.Count -eq 3) "Identity mode '$identityFailureMode' omitted cases."
            foreach ($failureCase in $failureManifest.cases) {
                Assert-True ($null -eq $failureCase.benchmarkId) "Identity mode '$identityFailureMode' silently paired '$($failureCase.id)'."
                Assert-True (@($failureCase.warnings) -contains 'benchmark identity unavailable or ambiguous; do not use this case with manifest batch/diff; analyze the trace directly') "Identity mode '$identityFailureMode' omitted guidance for '$($failureCase.id)'."
            }
        }
    }
    finally {
        $env:FILTRACE_CAPTURE_EVENTS_MODE = $previousEventsMode
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }

    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $jsonOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'json-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json | Out-String -Width 4096
        $jsonExitCode = $LASTEXITCODE
        $quietOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'quiet-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Quiet 2>&1 | Out-String -Width 4096
        $quietExitCode = $LASTEXITCODE
        $boundedJsonOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Handoff*' `
            -RunId 'handoff-budget-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json 2>&1 | Out-String -Width 4096
        $boundedJsonExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }

    Assert-True ($jsonExitCode -eq 0) 'JSON capture failed.'
    $jsonResult = $jsonOutput | ConvertFrom-Json
    Assert-True ($jsonResult.status -eq 'completed') 'JSON output did not report completed status.'
    Assert-True ($jsonResult.runId -eq 'json-run') 'JSON output did not report the run ID.'
    Assert-True ($jsonResult.cases.Count -eq 4) 'JSON output did not include every case.'
    Assert-True ($jsonResult.warnings.Count -gt 0) 'JSON output omitted provider warnings.'
    Assert-True ($jsonResult.cases[0].commands -contains "filtrace gcstats '$($jsonResult.cases[0].trace)'") 'JSON output omitted an enabled-zero command.'
    Assert-True (-not (Test-StringContains $jsonOutput 'fake BenchmarkDotNet capture completed')) 'BenchmarkDotNet chatter polluted JSON output.'
    Assert-True ([Text.Encoding]::UTF8.GetByteCount($jsonOutput.Trim()) -lt 20KB) 'JSON handoff exceeded 20 KiB.'

    Assert-True ($boundedJsonExitCode -eq 0) "Bounded JSON capture failed: $($boundedJsonOutput.Trim())"
    $boundedJsonResult = $boundedJsonOutput | ConvertFrom-Json
    $boundedManifestPath = Join-Path $globalArtifacts 'filtrace-runs/handoff-budget-run/manifest.json'
    Assert-True (Test-Path -LiteralPath $boundedManifestPath) 'Bounded JSON capture did not write its manifest.'
    $boundedManifest = Get-Content -LiteralPath $boundedManifestPath -Raw | ConvertFrom-Json
    Assert-True ($boundedManifest.cases.Count -eq 4) 'Bounded JSON manifest omitted capture cases.'
    Assert-True (@($boundedManifest.cases[0].warnings).Count -eq 24) 'Bounded JSON manifest omitted the full warning detail.'
    Assert-True ($boundedJsonResult.status -eq 'completed') 'Bounded JSON fallback did not preserve completed status.'
    Assert-True ($boundedJsonResult.runId -eq 'handoff-budget-run') 'Bounded JSON fallback omitted the run ID.'
    Assert-True ($boundedJsonResult.manifest -eq $boundedManifestPath) 'Bounded JSON fallback omitted the manifest path.'
    Assert-True ($boundedJsonResult.runDirectory -eq (Split-Path -Parent $boundedManifestPath)) 'Bounded JSON fallback omitted the canonical run directory.'
    Assert-True (Test-StringContains $boundedJsonResult.message 'read the manifest') 'Bounded JSON fallback did not direct the caller to the manifest.'
    Assert-True ($boundedJsonResult.PSObject.Properties.Name -notcontains 'cases') 'Bounded JSON fallback retained oversized case detail.'
    Assert-True ($boundedJsonResult.PSObject.Properties.Name -notcontains 'warnings') 'Bounded JSON fallback retained oversized warning detail.'
    Assert-True ([Text.Encoding]::UTF8.GetByteCount($boundedJsonOutput.Trim()) -lt 20KB) 'Bounded JSON fallback exceeded 20 KiB.'

    $previousInfoFailure = $env:FILTRACE_CAPTURE_INFO_FAILURE
    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    $env:FILTRACE_CAPTURE_INFO_FAILURE = '1'
    Push-Location $temporaryRoot
    try {
        $infoFailureOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'info-failure-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json | Out-String -Width 4096
        $infoFailureExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
        $env:FILTRACE_CAPTURE_INFO_FAILURE = $previousInfoFailure
    }

    Assert-True ($infoFailureExitCode -eq 0) 'Capture with unreadable filtrace info failed instead of returning an unknown-status handoff.'
    $infoFailureResult = $infoFailureOutput | ConvertFrom-Json
    $infoFailureManifestPath = Join-Path $globalArtifacts 'filtrace-runs/info-failure-run/manifest.json'
    $infoFailureManifest = Get-Content -LiteralPath $infoFailureManifestPath -Raw | ConvertFrom-Json
    $unexpectedInfoFailureCommands = @(
        $infoFailureResult.cases.commands |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    )
    Assert-True ($unexpectedInfoFailureCommands.Count -eq 0) "Failed filtrace info emitted analysis commands: $($unexpectedInfoFailureCommands -join '; ')"
    Assert-True (@($infoFailureResult.warnings.message) -contains 'filtrace info could not verify analysis availability; no commands emitted') 'Failed filtrace info did not emit an explicit warning.'
    Assert-True (@($infoFailureManifest.cases[0].warnings) -contains 'filtrace info could not verify analysis availability; no commands emitted') 'Failed filtrace info warning was omitted from the manifest.'
    foreach ($captureCase in $infoFailureManifest.cases) {
        Assert-True (@($captureCase.analyses.PSObject.Properties.Value.captureStatus | Where-Object { $_ -ne 'unknown' }).Count -eq 0) "Failed filtrace info did not mark every analysis unknown for case '$($captureCase.id)'."
        Assert-True (@($captureCase.analyses.PSObject.Properties.Value.eventCount | Where-Object { $null -ne $_ }).Count -eq 0) "Failed filtrace info fabricated an observed event count for case '$($captureCase.id)'."
    }

    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
        $timeoutWrapper = Join-Path $temporaryRoot 'Invoke-TimeoutCapture.ps1'
        $timeoutWrapperText = @'
param(
    [string]$CaptureScript,
    [string]$ProjectPath,
    [string]$DotnetPath,
    [string]$FiltracePath,
    [string]$RunId,
    [ValidateSet('Text', 'Json')]
    [string]$OutputFormat,
    [switch]$Quiet)

function Start-Process {
    [CmdletBinding()]
    param(
        [string]$FilePath,
        [string]$Verb,
        [switch]$PassThru,
        [string]$WorkingDirectory,
        [object[]]$ArgumentList)

    # Force the bounded wait to time out without a UAC prompt. Real elevation remains
    # a manual Windows check; this fake pins the parent process's output contract.
    $process = [pscustomobject]@{ HasExited = $false; ExitCode = 0 }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int]$Milliseconds)
        return $false
    }
    return $process
}

$captureParameters = @{
    Project = $ProjectPath
    Filter = '*Work*'
    Profiler = 'ETW'
    RunId = $RunId
    DotnetPath = $DotnetPath
    FiltracePath = $FiltracePath
    Format = $OutputFormat
    ElevatedTimeoutSeconds = 1
}
if ($Quiet) { $captureParameters.Quiet = $true }
. $CaptureScript @captureParameters
'@
        [System.IO.File]::WriteAllText($timeoutWrapper, $timeoutWrapperText)
        Push-Location $temporaryRoot
        try {
            $timeoutJson = & $hostExe -NoProfile -File $timeoutWrapper -CaptureScript $captureScript `
                -ProjectPath $projectPath -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
                -RunId 'timeout-json-run' -OutputFormat Json | Out-String -Width 4096
            $timeoutExitCode = $LASTEXITCODE
            $timeoutText = & $hostExe -NoProfile -File $timeoutWrapper -CaptureScript $captureScript `
                -ProjectPath $projectPath -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
                -RunId 'timeout-text-run' -OutputFormat Text 2>&1 | Out-String -Width 4096
            $timeoutTextExitCode = $LASTEXITCODE
            $timeoutQuiet = & $hostExe -NoProfile -File $timeoutWrapper -CaptureScript $captureScript `
                -ProjectPath $projectPath -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
                -RunId 'timeout-quiet-run' -OutputFormat Text -Quiet 2>&1 | Out-String -Width 4096
            $timeoutQuietExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }

        Assert-True ($timeoutExitCode -eq 0) 'Elevated timeout did not preserve its non-fatal exit status.'
        $timeoutResult = $timeoutJson | ConvertFrom-Json
        $timeoutLog = Join-Path $globalArtifacts 'filtrace-runs/timeout-json-run/capture.log'
        Assert-True ($timeoutResult.status -eq 'timeout') 'Elevated JSON timeout did not report timeout status.'
        Assert-True ($timeoutResult.runId -eq 'timeout-json-run') 'Elevated JSON timeout omitted the run ID.'
        Assert-True ($timeoutResult.log -eq $timeoutLog) 'Elevated JSON timeout omitted the capture log path.'
        Assert-True (Test-StringContains $timeoutResult.message 'did not signal completion') 'Elevated JSON timeout omitted its diagnostic message.'
        Assert-True ($null -eq $timeoutResult.manifest) 'Elevated JSON timeout reported a manifest that was never observed.'
        Assert-True ($timeoutResult.cases.Count -eq 0) 'Elevated JSON timeout reported capture cases that were never observed.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/timeout-json-run'))) 'Elevated timeout unexpectedly created a run directory in the parent.'
        Assert-True ($timeoutTextExitCode -eq 0) 'Elevated text timeout did not preserve its non-fatal exit status.'
        Assert-True (Test-StringContains $timeoutText 'did not signal completion') 'Elevated text timeout did not emit a warning.'
        Assert-True (-not (Test-StringContains $timeoutText '"status"')) 'Elevated text timeout emitted JSON.'
        Assert-True ($timeoutQuietExitCode -eq 0) 'Elevated quiet timeout did not preserve its non-fatal exit status.'
        Assert-True (Test-StringContains $timeoutQuiet 'did not signal completion') 'Elevated quiet timeout suppressed its warning.'
        Assert-True (-not (Test-StringContains $timeoutQuiet 'Captured ')) 'Elevated quiet timeout emitted a capture summary.'
    }

    Assert-True ($quietExitCode -eq 0) 'Quiet capture failed.'
    Assert-True (Test-StringContains $quietOutput 'capture disabled') 'Quiet mode suppressed warnings.'
    Assert-True (-not (Test-StringContains $quietOutput 'Captured ')) 'Quiet mode emitted capture summary.'
    Assert-True (-not (Test-StringContains $quietOutput 'Manifest:')) 'Quiet mode emitted the manifest summary.'
    Assert-True (-not (Test-StringContains $quietOutput 'filtrace cpu ')) 'Quiet mode emitted commands.'
    Assert-True (-not (Test-StringContains $quietOutput 'fake BenchmarkDotNet capture completed')) 'BenchmarkDotNet chatter polluted quiet output.'

    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $fallbackOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'fallback-run' -DotnetPath $fakeDotnet -FiltracePath $missingFiltrace -Format Json | Out-String -Width 4096
        $fallbackExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }
    Assert-True ($fallbackExitCode -eq 0) 'Capture without filtrace failed.'
    $fallbackResult = $fallbackOutput | ConvertFrom-Json
    $fallbackManifestPath = Join-Path $globalArtifacts 'filtrace-runs/fallback-run/manifest.json'
    $fallbackManifest = Get-Content -LiteralPath $fallbackManifestPath -Raw | ConvertFrom-Json
    $fallbackCommands = @($fallbackResult.cases[0].commands)
    Assert-True (@($fallbackCommands | Where-Object { $_ -match '^filtrace cpu ' }).Count -eq 1) 'Recorder-established CPU did not emit a fallback command.'
    Assert-True (@($fallbackCommands | Where-Object { $_ -match '^filtrace exceptions ' }).Count -eq 1) 'Recorder-established exceptions did not emit a fallback command.'
    Assert-True (@($fallbackCommands | Where-Object { $_ -match '^filtrace alloc ' }).Count -eq 0) 'Recorder-disabled allocation emitted a fallback command.'
    Assert-True (@($fallbackCommands | Where-Object { $_ -match '^filtrace lines ' }).Count -eq 0) 'Unverified symbols emitted a source-line command.'
    Assert-True (@($fallbackResult.warnings.message) -contains 'source lines unavailable; no logged child output had an exact matching PDB') 'Missing filtrace did not explain unavailable source lines.'
    Assert-True ($fallbackManifest.cases[0].analyses.cpu.captureStatus -eq 'enabled') 'Recorder-established CPU status was not preserved.'
    Assert-True ($null -eq $fallbackManifest.cases[0].analyses.cpu.eventCount) 'Recorder-established CPU fabricated an observed event count.'
    Assert-True ($fallbackManifest.cases[0].analyses.exceptions.captureStatus -eq 'enabled') 'Recorder-established exceptions status was not preserved.'
    Assert-True ($null -eq $fallbackManifest.cases[0].analyses.exceptions.eventCount) 'Recorder-established exceptions fabricated an observed event count.'
    Assert-True ($fallbackManifest.cases[0].analyses.alloc.captureStatus -eq 'disabled') 'Recorder-disabled allocation status was not preserved.'
    Assert-True ($null -eq $fallbackManifest.cases[0].analyses.alloc.eventCount) 'Recorder-disabled allocation unexpectedly gained an event count.'
    Assert-True ($fallbackManifest.cases[0].analyses.activity.captureStatus -eq 'unknown') 'Recorder fallback claimed activity without provider evidence.'
    Assert-True ($null -eq $fallbackManifest.cases[0].analyses.activity.eventCount) 'Recorder fallback fabricated an activity event count.'
    Assert-True (@($fallbackCommands | Where-Object { $_ -match '^filtrace rank .*--metric activity' }).Count -eq 0) 'Recorder fallback emitted an activity command without provider evidence.'
    foreach ($captureCase in $fallbackManifest.cases) {
        Assert-True (@($captureCase.analyses.PSObject.Properties.Value.eventCount | Where-Object { $null -ne $_ }).Count -eq 0) "Recorder fallback fabricated an observed event count for case '$($captureCase.id)'."
    }

    $reuseArgsPath = Join-Path $temporaryRoot 'reuse-dotnet-args.txt'
    $env:FILTRACE_CAPTURE_ARGS = $reuseArgsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $reuseOutput = @(
                & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
                    -RunId 'current-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 |
                    ForEach-Object { $_.ToString() }
            ) -join [Environment]::NewLine
            $reuseExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }

    Assert-True ($reuseExitCode -ne 0) 'A reused RunId was accepted.'
    Assert-True (Test-StringContains $reuseOutput 'already exists') 'Reused RunId failure did not explain the existing run.'
    Assert-True (-not (Test-Path -LiteralPath $reuseArgsPath)) 'Reused RunId invoked dotnet before rejection.'

    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $countOnlyOutput = @(
                & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
                    -RunId 'count-only-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
                    -OperationCount 100 2>&1 |
                    ForEach-Object { $_.ToString() }
            ) -join [Environment]::NewLine
            $countOnlyExitCode = $LASTEXITCODE
            $unitOnlyOutput = @(
                & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
                    -RunId 'unit-only-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
                    -OperationUnit items 2>&1 |
                    ForEach-Object { $_.ToString() }
            ) -join [Environment]::NewLine
            $unitOnlyExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }
    finally {
        Pop-Location
    }
    Assert-True ($countOnlyExitCode -ne 0) 'OperationCount without OperationUnit was accepted.'
    Assert-True ($unitOnlyExitCode -ne 0) 'OperationUnit without OperationCount was accepted.'
    Assert-True (Test-StringContains $countOnlyOutput 'together') 'Count-only failure did not explain the parameter relationship.'
    Assert-True (Test-StringContains $unitOnlyOutput 'together') 'Unit-only failure did not explain the parameter relationship.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/count-only-run'))) 'Count-only rejection created a run directory.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/unit-only-run'))) 'Unit-only rejection created a run directory.'

    $unsafeWrapper = Join-Path $temporaryRoot 'Invoke-UnsafeCapture.ps1'
    $unsafeWrapperText = @'
param(
    [string]$CaptureScript,
    [string]$ProjectPath,
    [string]$DotnetPath,
    [string]$FiltracePath)

& $CaptureScript -Project $ProjectPath -Filter '*Work*"' -Profiler ETW `
    -RunId 'unsafe-etw-run' -DotnetPath $DotnetPath -FiltracePath $FiltracePath
exit $LASTEXITCODE
'@
    [System.IO.File]::WriteAllText($unsafeWrapper, $unsafeWrapperText)
    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $unsafeOutput = @(
                & $hostExe -NoProfile -File $unsafeWrapper -CaptureScript $captureScript `
                    -ProjectPath $projectPath -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 |
                    ForEach-Object { $_.ToString() }
            ) -join [Environment]::NewLine
            $unsafeExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }
    finally {
        Pop-Location
    }
    Assert-True ($unsafeExitCode -ne 0) 'Unsafe ETW elevation argument was accepted.'
    $normalizedUnsafeOutput = [regex]::Replace($unsafeOutput, '\s+', ' ')
    Assert-True (
        (Test-StringContains $normalizedUnsafeOutput 'ETW elevation argument') -and
        (Test-StringContains $normalizedUnsafeOutput 'quotes')) `
        'Unsafe ETW argument failure was not explained.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/unsafe-etw-run'))) 'Unsafe ETW argument created a run directory.'

    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $oversizeOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Oversize*' `
            -RunId 'oversize-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json 2>&1 | Out-String -Width 4096
        $oversizeExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }

    $oversizeManifest = Join-Path $globalArtifacts 'filtrace-runs/oversize-run/manifest.json'
    Assert-True ($oversizeExitCode -eq 0) "Large durable manifest capture failed: $($oversizeOutput.Trim())"
    Assert-True (Test-Path -LiteralPath $oversizeManifest) 'Large durable manifest was not written.'
    Assert-True ((Get-Item -LiteralPath $oversizeManifest).Length -gt 20KB) 'Large durable manifest fixture did not exceed the stdout budget.'
    $oversizeResult = $oversizeOutput | ConvertFrom-Json
    Assert-True ($oversizeResult.status -eq 'completed') 'Large-manifest JSON handoff did not preserve completed status.'
    Assert-True ($oversizeResult.manifest -eq $oversizeManifest) 'Large-manifest JSON handoff omitted the manifest path.'
    Assert-True ($oversizeResult.PSObject.Properties.Name -notcontains 'cases') 'Large-manifest JSON handoff retained oversized case detail.'
    Assert-True ([Text.Encoding]::UTF8.GetByteCount($oversizeOutput.Trim()) -lt 20KB) 'Large-manifest JSON handoff exceeded 20 KiB.'

    $lockPath = Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'obj/filtrace-capture-locks') -Filter '*.lock' |
        Select-Object -ExpandProperty FullName -First 1
    Assert-True (-not [string]::IsNullOrEmpty($lockPath)) 'Capture lock file was not created.'
    $heldLock = [System.IO.File]::Open(
        $lockPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $siblingProjectPath = Join-Path $projectDirectory 'Sibling.Perf.csproj'
        [System.IO.File]::WriteAllText($siblingProjectPath, '<Project Sdk="Microsoft.NET.Sdk" />')
        $env:FILTRACE_CAPTURE_ARGS = $argsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
        Push-Location $temporaryRoot
        try {
            $siblingOutput = & $hostExe -NoProfile -File $captureScript -Project $siblingProjectPath -Filter '*Work*' `
                -RunId 'sibling-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 | Out-String -Width 4096
            $siblingExitCode = $LASTEXITCODE
            $previousErrorActionPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $overlapOutput = @(
                    & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
                        -RunId 'overlap-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 |
                        ForEach-Object { $_.ToString() }
                ) -join [Environment]::NewLine
                $overlapExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }
        }
        finally {
            Pop-Location
            $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
            $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
        }
    }
    finally {
        $heldLock.Dispose()
    }

    Assert-True ($siblingExitCode -eq 0) 'A sibling project in the same directory incorrectly shared the capture lock.'
    Assert-True (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/sibling-run/manifest.json')) 'Sibling project capture did not complete.'
    Assert-True ($overlapExitCode -ne 0) 'A concurrent same-project/TFM capture was not rejected.'
    $normalizedOverlapOutput = [regex]::Replace($overlapOutput, '\s+', ' ')
    Assert-True (Test-StringContains $normalizedOverlapOutput 'capture is already active') 'Overlap rejection did not explain the active capture.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/overlap-run'))) 'Rejected overlap created a run directory.'

    $global:LASTEXITCODE = 0
    Write-Host 'Capture helper contract passed.'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}