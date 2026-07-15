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

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $help = Get-Help $captureScript -Full | Out-String
    Assert-True ($help.Contains('[-ElevatedChild]', [StringComparison]::OrdinalIgnoreCase)) 'ElevatedChild was not discoverable in Get-Help syntax.'
    $captureSource = Get-Content -LiteralPath $captureScript -Raw
    Assert-True ($captureSource.Contains('.PARAMETER ElevatedChild', [StringComparison]::Ordinal)) 'ElevatedChild has no comment-based help entry.'
    Assert-True ($captureSource.Contains('Internal switch reserved for the self-elevated ETW child process', [StringComparison]::OrdinalIgnoreCase)) 'ElevatedChild help did not identify the switch as internal.'
    Assert-True ($captureSource.Contains('reports the capture.log path', [StringComparison]::OrdinalIgnoreCase)) 'Elevated timeout help did not describe reporting capture.log.'
    foreach ($staleElevationPhrase in @('surfaces the log tail', 'live progress', 'references for progress', 'log still surfaces', 'hang/no-tail')) {
        Assert-True (-not $captureSource.Contains($staleElevationPhrase, [StringComparison]::OrdinalIgnoreCase)) "Stale ETW elevation wording remained: '$staleElevationPhrase'."
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
        'ConvertTo-PowerShellArgument',
        'Test-AnalysisEnabled',
        'Get-CaseCommands',
        'Get-BenchmarkParameters')
    $commandFunctionDefinitions = @(
        $captureAst.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $commandFunctionNames },
            $true) |
            Sort-Object { $_.Extent.StartOffset } |
            ForEach-Object { $_.Extent.Text }
    )
    Assert-True ($commandFunctionDefinitions.Count -eq $commandFunctionNames.Count) 'Capture command-builder functions could not be isolated.'
    . ([scriptblock]::Create(($commandFunctionDefinitions -join [Environment]::NewLine)))
    Assert-True ((Get-BenchmarkParameters 'FakeBench.Work(Size: 1, Mode: Fast): Job-A') -eq 'Size: 1, Mode: Fast') 'Parameterized benchmark identity was not extracted.'
    Assert-True ((Get-BenchmarkParameters 'FakeBench.Work: Job-A') -eq '') 'Unparameterized benchmark identity was not empty.'

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
else {
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010203.nettrace'), 'current raw')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010203.speedscope.json'), 'current speedscope')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010204.nettrace'), 'second parameter raw')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010204.speedscope.json'), 'second parameter speedscope')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Other-20260713-010205.nettrace'), 'other raw')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Other-20260713-010205.speedscope.json'), 'other speedscope')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.ScopeOnly-20260713-010206.speedscope.json'), 'scope only')
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Scenario-20260713-010207.nettrace'), 'scenario raw')
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
Write-Output '// Benchmark: FakeBench.Work(Size: 1): Job-A'
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName "Fake.Bench.Work(Size: 1)" --job Job-A --benchmarkId 10 in fake'
Write-Output '// Benchmark: FakeBench.Work(Size: 2): Job-A'
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName "Fake.Bench.Work(Size: 2)" --job Job-A --benchmarkId 11 in fake'
Write-Output '// Benchmark: FakeBench.Other(Size: 2): Job-A'
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName "Fake.Bench.Other(Size: 2)" --job Job-A --benchmarkId 4 in fake'
Write-Output '// Benchmark: FakeBench.ScopeOnly(Size: 3): Job-A'
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName "Fake.Bench.ScopeOnly(Size: 3)" --job Job-A --benchmarkId 7 in fake'
Write-Output '// Benchmark: FakeBench.Scenario: Job-A [Scenario=Value]'
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName "Fake.Bench.Scenario[Scenario=Value]" --job Job-A --benchmarkId 15 in fake'
Write-Output '// Runtime=.NET 10.0.0, X64 RyuJIT'
Write-Output 'fake BenchmarkDotNet capture completed'
'@
    [System.IO.File]::WriteAllText($fakeDotnet, $fakeDotnetText)

    $fakeFiltrace = Join-Path $temporaryRoot 'Fake-Filtrace.ps1'
    $fakeFiltraceText = @'
if ($args.Count -gt 0 -and $args[0] -eq '--version') {
    if ($env:FILTRACE_CAPTURE_OLD_VERSION -eq '1') {
        Write-Output '0.5.0'
    } else {
        Write-Output '0.7.0'
    }
    $global:LASTEXITCODE = 0
    return
}
$symbolIndex = [Array]::IndexOf($args, '--symbols')
$symbols = if ($symbolIndex -ge 0) { $args[$symbolIndex + 1] } else { '' }
$isExact = $symbols -eq $env:FILTRACE_CAPTURE_CHILD_SYMBOLS
$tracePath = if ($args.Count -gt 1) { $args[1] } else { '' }
$isSpeedscope = $tracePath -like '*.speedscope.json'
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
[ordered]@{ result = [ordered]@{ sourceResolution = $source; analyses = $analyses } } | ConvertTo-Json -Depth 6 -Compress
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
    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $global:LASTEXITCODE = 0
        $output = & $captureScript -Project $projectPath -Filter '*Work*' -RunId 'current-run' `
            -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
            -OperationCount 100 -OperationUnit items *>&1 | Out-String
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
    Assert-True ($output.Contains($currentTrace, [StringComparison]::OrdinalIgnoreCase)) 'Current-run trace was not selected.'
    Assert-True ($output.Contains($secondParameterTrace, [StringComparison]::OrdinalIgnoreCase)) 'Second parameter trace was not reported.'
    Assert-True ($output.Contains($otherTrace, [StringComparison]::OrdinalIgnoreCase)) 'Other current-run trace was not reported.'
    Assert-True ($output.Contains($scopeOnly, [StringComparison]::OrdinalIgnoreCase)) 'Speedscope-only case was not reported.'
    Assert-True (-not $output.Contains('stale-global', [StringComparison]::OrdinalIgnoreCase)) 'A stale global trace was selected.'
    Assert-True (-not $output.Contains('stale-old-run', [StringComparison]::OrdinalIgnoreCase)) 'A stale prior-run trace was selected.'

    [string[]]$dotnetArguments = [System.IO.File]::ReadAllLines($argsPath)
    $artifactIndex = [Array]::IndexOf($dotnetArguments, '--artifacts')
    Assert-True ($artifactIndex -ge 0) 'Fake dotnet did not receive --artifacts.'
    Assert-True ($dotnetArguments[$artifactIndex + 1] -eq $currentArtifacts) 'Fake dotnet received the wrong artifacts directory.'

    $manifestPath = Join-Path $currentRun 'manifest.json'
    Assert-True (Test-Path -LiteralPath $manifestPath) 'Run manifest was not created.'
    Assert-True ((Get-Item -LiteralPath $manifestPath).Length -lt 20KB) 'Run manifest exceeded the 20 KiB compact-output target.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-True ($manifest.schemaVersion -eq 1) 'Run manifest schema version is incorrect.'
    Assert-True ($manifest.runId -eq 'current-run') 'Run manifest ID is incorrect.'
    Assert-True ($manifest.cases.Count -eq 5) 'Run manifest did not include every capture case.'
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
    Assert-True ($manifest.cases[4].benchmarkId -eq 15) 'Bracket-param BenchmarkDotNet case ID was not paired.'
    Assert-True ($manifest.cases[4].benchmark -eq 'Fake.Bench.Scenario') 'Bracket-param benchmark name was not stripped of its bracket suffix.'
    Assert-True ($manifest.cases[4].parameters -eq 'Scenario=Value') 'Bracket-param parameters were not extracted from the display name.'
    Assert-True ($manifest.cases[4].benchmarkDisplay -eq 'FakeBench.Scenario: Job-A [Scenario=Value]') 'Bracket-param display was not recorded.'
    foreach ($captureCase in $manifest.cases) {
        Assert-True ($captureCase.operationCount -eq 100) "Operation count was not recorded for case '$($captureCase.id)'."
        Assert-True ($captureCase.operationUnit -eq 'items') "Operation unit was not recorded for case '$($captureCase.id)'."
    }
    Assert-True ($null -eq $manifest.cases[3].trace) 'Speedscope-only case unexpectedly gained a raw trace.'
    Assert-True ($manifest.cases[3].speedscope -eq $scopeOnly) 'Speedscope-only case path was not recorded.'
    Assert-True ($null -eq $manifest.cases[3].symbolsDirectory) 'Speedscope-only case unexpectedly gained source symbols.'
    Assert-True ($manifest.runtimes.Count -eq 1) 'Runtime identity was not recorded.'
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
    Assert-True ($output.Contains($childSymbols, [StringComparison]::OrdinalIgnoreCase)) 'Printed source command did not use exact child symbols.'
    Assert-True (([regex]::Matches($output, 'filtrace lines ')).Count -eq 4) 'Expected one filtrace lines command per raw trace and none for the speedscope-only case.'
    Assert-True (([regex]::Matches($output, 'filtrace cpu ')).Count -eq 5) 'Enabled-zero CPU did not emit one command per case.'
    Assert-True (([regex]::Matches($output, 'filtrace rank .*--metric contention')).Count -eq 4) 'Enabled contention did not emit one command per raw trace.'
    Assert-True (([regex]::Matches($output, 'filtrace gcstats ')).Count -eq 4) 'Enabled-zero GC did not emit one command per raw trace.'
    Assert-True (([regex]::Matches($output, 'filtrace threadpool ')).Count -eq 4) 'Enabled threadpool did not emit one command per raw trace.'
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
    Assert-True (-not $output.Contains('filtrace alloc ', [StringComparison]::OrdinalIgnoreCase)) 'Disabled allocation emitted a command.'
    Assert-True (-not $output.Contains('filtrace exceptions ', [StringComparison]::OrdinalIgnoreCase)) 'Unknown exceptions emitted a command.'
    Assert-True ($output.Contains('alloc capture disabled', [StringComparison]::OrdinalIgnoreCase)) 'Disabled allocation was not explained.'
    Assert-True ($output.Contains('exceptions capture status unknown', [StringComparison]::OrdinalIgnoreCase)) 'Unknown exceptions were not explained.'
    Assert-True (-not $output.Contains('fake BenchmarkDotNet capture completed', [StringComparison]::OrdinalIgnoreCase)) 'BenchmarkDotNet chatter leaked to stdout.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $currentRun 'capture.log') -Raw).Contains('fake BenchmarkDotNet capture completed', [StringComparison]::OrdinalIgnoreCase)) 'Full BenchmarkDotNet output was not retained in capture.log.'
    Assert-True (-not (($manifest | ConvertTo-Json -Depth 8) -match 'stale-global|stale-old-run')) 'Stale captures entered the manifest.'

    $hostExe = (Get-Process -Id $PID).Path
    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $jsonOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'json-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json | Out-String
        $jsonExitCode = $LASTEXITCODE
        $quietOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'quiet-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Quiet 2>&1 | Out-String
        $quietExitCode = $LASTEXITCODE
        $boundedJsonOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Handoff*' `
            -RunId 'handoff-budget-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json 2>&1 | Out-String
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
    Assert-True ($jsonResult.cases.Count -eq 5) 'JSON output did not include every case.'
    Assert-True ($jsonResult.warnings.Count -gt 0) 'JSON output omitted provider warnings.'
    Assert-True ($jsonResult.cases[0].commands -contains "filtrace gcstats '$($jsonResult.cases[0].trace)'") 'JSON output omitted an enabled-zero command.'
    Assert-True (-not $jsonOutput.Contains('fake BenchmarkDotNet capture completed', [StringComparison]::OrdinalIgnoreCase)) 'BenchmarkDotNet chatter polluted JSON output.'
    Assert-True ([Text.Encoding]::UTF8.GetByteCount($jsonOutput.Trim()) -lt 20KB) 'JSON handoff exceeded 20 KiB.'

    Assert-True ($boundedJsonExitCode -eq 0) "Bounded JSON capture failed: $($boundedJsonOutput.Trim())"
    $boundedJsonResult = $boundedJsonOutput | ConvertFrom-Json
    $boundedManifestPath = Join-Path $globalArtifacts 'filtrace-runs/handoff-budget-run/manifest.json'
    Assert-True (Test-Path -LiteralPath $boundedManifestPath) 'Bounded JSON capture did not write its manifest.'
    Assert-True ((Get-Item -LiteralPath $boundedManifestPath).Length -lt 20KB) 'Bounded JSON fixture exceeded the manifest budget instead of only the handoff budget.'
    $boundedManifest = Get-Content -LiteralPath $boundedManifestPath -Raw | ConvertFrom-Json
    Assert-True ($boundedManifest.cases.Count -eq 4) 'Bounded JSON manifest omitted capture cases.'
    Assert-True (@($boundedManifest.cases[0].warnings).Count -eq 23) 'Bounded JSON manifest omitted the full warning detail.'
    Assert-True ($boundedJsonResult.status -eq 'completed') 'Bounded JSON fallback did not preserve completed status.'
    Assert-True ($boundedJsonResult.runId -eq 'handoff-budget-run') 'Bounded JSON fallback omitted the run ID.'
    Assert-True ($boundedJsonResult.manifest -eq $boundedManifestPath) 'Bounded JSON fallback omitted the manifest path.'
    Assert-True ($boundedJsonResult.runDirectory -eq (Split-Path -Parent $boundedManifestPath)) 'Bounded JSON fallback omitted the canonical run directory.'
    Assert-True ($boundedJsonResult.message.Contains('read the manifest', [StringComparison]::OrdinalIgnoreCase)) 'Bounded JSON fallback did not direct the caller to the manifest.'
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
            -RunId 'info-failure-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace -Format Json | Out-String
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
                -RunId 'timeout-json-run' -OutputFormat Json | Out-String
            $timeoutExitCode = $LASTEXITCODE
            $timeoutText = & $hostExe -NoProfile -File $timeoutWrapper -CaptureScript $captureScript `
                -ProjectPath $projectPath -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
                -RunId 'timeout-text-run' -OutputFormat Text 2>&1 | Out-String
            $timeoutTextExitCode = $LASTEXITCODE
            $timeoutQuiet = & $hostExe -NoProfile -File $timeoutWrapper -CaptureScript $captureScript `
                -ProjectPath $projectPath -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
                -RunId 'timeout-quiet-run' -OutputFormat Text -Quiet 2>&1 | Out-String
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
        Assert-True ($timeoutResult.message.Contains('did not signal completion', [StringComparison]::OrdinalIgnoreCase)) 'Elevated JSON timeout omitted its diagnostic message.'
        Assert-True ($null -eq $timeoutResult.manifest) 'Elevated JSON timeout reported a manifest that was never observed.'
        Assert-True ($timeoutResult.cases.Count -eq 0) 'Elevated JSON timeout reported capture cases that were never observed.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/timeout-json-run'))) 'Elevated timeout unexpectedly created a run directory in the parent.'
        Assert-True ($timeoutTextExitCode -eq 0) 'Elevated text timeout did not preserve its non-fatal exit status.'
        Assert-True ($timeoutText.Contains('did not signal completion', [StringComparison]::OrdinalIgnoreCase)) 'Elevated text timeout did not emit a warning.'
        Assert-True (-not $timeoutText.Contains('"status"', [StringComparison]::OrdinalIgnoreCase)) 'Elevated text timeout emitted JSON.'
        Assert-True ($timeoutQuietExitCode -eq 0) 'Elevated quiet timeout did not preserve its non-fatal exit status.'
        Assert-True ($timeoutQuiet.Contains('did not signal completion', [StringComparison]::OrdinalIgnoreCase)) 'Elevated quiet timeout suppressed its warning.'
        Assert-True (-not $timeoutQuiet.Contains('Captured ', [StringComparison]::OrdinalIgnoreCase)) 'Elevated quiet timeout emitted a capture summary.'
    }

    Assert-True ($quietExitCode -eq 0) 'Quiet capture failed.'
    Assert-True ($quietOutput.Contains('capture disabled', [StringComparison]::OrdinalIgnoreCase)) 'Quiet mode suppressed warnings.'
    Assert-True (-not $quietOutput.Contains('Captured ', [StringComparison]::OrdinalIgnoreCase)) 'Quiet mode emitted capture summary.'
    Assert-True (-not $quietOutput.Contains('Manifest:', [StringComparison]::OrdinalIgnoreCase)) 'Quiet mode emitted the manifest summary.'
    Assert-True (-not $quietOutput.Contains('filtrace cpu ', [StringComparison]::OrdinalIgnoreCase)) 'Quiet mode emitted commands.'
    Assert-True (-not $quietOutput.Contains('fake BenchmarkDotNet capture completed', [StringComparison]::OrdinalIgnoreCase)) 'BenchmarkDotNet chatter polluted quiet output.'

    $missingFiltrace = Join-Path $temporaryRoot 'missing-filtrace'
    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $fallbackOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'fallback-run' -DotnetPath $fakeDotnet -FiltracePath $missingFiltrace -Format Json | Out-String
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
    Assert-True ($fallbackManifest.cases[0].analyses.activity.captureStatus -eq 'unknown') 'Recorder-default EP activity was not unknown.'
    foreach ($captureCase in $fallbackManifest.cases) {
        Assert-True (@($captureCase.analyses.PSObject.Properties.Value.eventCount | Where-Object { $null -ne $_ }).Count -eq 0) "Recorder fallback fabricated an observed event count for case '$($captureCase.id)'."
    }

    $reuseArgsPath = Join-Path $temporaryRoot 'reuse-dotnet-args.txt'
    $env:FILTRACE_CAPTURE_ARGS = $reuseArgsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $reuseOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'current-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 | Out-String
        $reuseExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }

    Assert-True ($reuseExitCode -ne 0) 'A reused RunId was accepted.'
    Assert-True ($reuseOutput.Contains('already exists', [StringComparison]::OrdinalIgnoreCase)) 'Reused RunId failure did not explain the existing run.'
    Assert-True (-not (Test-Path -LiteralPath $reuseArgsPath)) 'Reused RunId invoked dotnet before rejection.'

    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $countOnlyOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'count-only-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
            -OperationCount 100 2>&1 | Out-String
        $countOnlyExitCode = $LASTEXITCODE
        $unitOnlyOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'unit-only-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace `
            -OperationUnit items 2>&1 | Out-String
        $unitOnlyExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    Assert-True ($countOnlyExitCode -ne 0) 'OperationCount without OperationUnit was accepted.'
    Assert-True ($unitOnlyExitCode -ne 0) 'OperationUnit without OperationCount was accepted.'
    Assert-True ($countOnlyOutput.Contains('together', [StringComparison]::OrdinalIgnoreCase)) 'Count-only failure did not explain the parameter relationship.'
    Assert-True ($unitOnlyOutput.Contains('together', [StringComparison]::OrdinalIgnoreCase)) 'Unit-only failure did not explain the parameter relationship.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/count-only-run'))) 'Count-only rejection created a run directory.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/unit-only-run'))) 'Unit-only rejection created a run directory.'

    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $unsafeOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*"' `
            -Profiler ETW -RunId 'unsafe-etw-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 | Out-String
        $unsafeExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    Assert-True ($unsafeExitCode -ne 0) 'Unsafe ETW elevation argument was accepted.'
    Assert-True ($unsafeOutput.Contains('cannot contain quotes', [StringComparison]::OrdinalIgnoreCase)) 'Unsafe ETW argument failure was not explained.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/unsafe-etw-run'))) 'Unsafe ETW argument created a run directory.'

    $env:FILTRACE_CAPTURE_ARGS = $argsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $oversizeOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Oversize*' `
            -RunId 'oversize-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 | Out-String
        $oversizeExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
    }

    $oversizeManifest = Join-Path $globalArtifacts 'filtrace-runs/oversize-run/manifest.json'
    Assert-True ($oversizeExitCode -eq 0) 'A capture with more than 20 KiB of manifest data should succeed; there is no on-disk size limit.'
    Assert-True (Test-Path -LiteralPath $oversizeManifest) 'Oversize manifest was not written.'
    Assert-True ((Get-Item -LiteralPath $oversizeManifest).Length -gt 20KB) 'Oversize manifest is unexpectedly small; it should exceed the old 20 KiB threshold.'

    $previousOldVersion = $env:FILTRACE_CAPTURE_OLD_VERSION
    $preflightArgsPath = Join-Path $temporaryRoot 'preflight-dotnet-args.txt'
    $env:FILTRACE_CAPTURE_ARGS = $preflightArgsPath
    $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $childSymbols
    $env:FILTRACE_CAPTURE_OLD_VERSION = '1'
    Push-Location $temporaryRoot
    try {
        $hostExe = (Get-Process -Id $PID).Path
        $preflightOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
            -RunId 'preflight-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 | Out-String
        $preflightExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $env:FILTRACE_CAPTURE_ARGS = $previousArgsPath
        $env:FILTRACE_CAPTURE_CHILD_SYMBOLS = $previousChildSymbols
        $env:FILTRACE_CAPTURE_OLD_VERSION = $previousOldVersion
    }
    Assert-True ($preflightExitCode -ne 0) 'Old filtrace version was not rejected by the preflight check.'
    Assert-True ($preflightOutput.Contains('0.6.0', [StringComparison]::OrdinalIgnoreCase)) 'Preflight failure did not report the minimum required version.'
    Assert-True ($preflightOutput.Contains('dotnet tool update', [StringComparison]::OrdinalIgnoreCase)) 'Preflight failure did not suggest how to upgrade filtrace.'
    Assert-True (-not (Test-Path -LiteralPath $preflightArgsPath)) 'Preflight rejection invoked dotnet before verifying the filtrace version.'

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
                -RunId 'sibling-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 | Out-String
            $siblingExitCode = $LASTEXITCODE
            $overlapOutput = & $hostExe -NoProfile -File $captureScript -Project $projectPath -Filter '*Work*' `
                -RunId 'overlap-run' -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 2>&1 | Out-String
            $overlapExitCode = $LASTEXITCODE
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
    Assert-True ($overlapOutput.Contains('capture is already active', [StringComparison]::OrdinalIgnoreCase)) 'Overlap rejection did not explain the active capture.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $globalArtifacts 'filtrace-runs/overlap-run'))) 'Rejected overlap created a run directory.'

    $global:LASTEXITCODE = 0
    Write-Host 'Capture helper contract passed.'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}