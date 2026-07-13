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
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace-capture-contract-$([Guid]::NewGuid().ToString('N'))"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
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
[System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010203.nettrace'), 'current raw')
[System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010203.speedscope.json'), 'current speedscope')
[System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010204.nettrace'), 'second parameter raw')
[System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Work-20260713-010204.speedscope.json'), 'second parameter speedscope')
[System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Other-20260713-010205.nettrace'), 'other raw')
[System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.Other-20260713-010205.speedscope.json'), 'other speedscope')
[System.IO.File]::WriteAllText((Join-Path $artifacts 'Fake.Bench.ScopeOnly-20260713-010206.speedscope.json'), 'scope only')
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
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName Fake.Bench.Work --job Job-A --benchmarkId 10 in fake'
Write-Output '// Benchmark: FakeBench.Work(Size: 2): Job-A'
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName Fake.Bench.Work --job Job-A --benchmarkId 11 in fake'
Write-Output '// Benchmark: FakeBench.Other(Size: 2): Job-A'
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName Fake.Bench.Other --job Job-A --benchmarkId 4 in fake'
Write-Output '// Benchmark: FakeBench.ScopeOnly(Size: 3): Job-A'
Write-Output '// Execute: dotnet Fake-Job.dll --benchmarkName Fake.Bench.ScopeOnly --job Job-A --benchmarkId 7 in fake'
Write-Output 'Runtime = .NET 10.0.0, X64 RyuJIT'
Write-Output 'fake BenchmarkDotNet capture completed'
'@
    [System.IO.File]::WriteAllText($fakeDotnet, $fakeDotnetText)

    $fakeFiltrace = Join-Path $temporaryRoot 'Fake-Filtrace.ps1'
    $fakeFiltraceText = @'
$symbolIndex = [Array]::IndexOf($args, '--symbols')
$symbols = if ($symbolIndex -ge 0) { $args[$symbolIndex + 1] } else { '' }
$isExact = $symbols -eq $env:FILTRACE_CAPTURE_CHILD_SYMBOLS
$source = [ordered]@{
    sampledManagedFrameCount = 100
    mappedManagedFrameCount = if ($isExact) { 75 } else { 0 }
    matchingPdbModules = if ($isExact) { @('Fake-Job') } else { @() }
}
[ordered]@{ result = [ordered]@{ sourceResolution = $source } } | ConvertTo-Json -Depth 4 -Compress
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
            -DotnetPath $fakeDotnet -FiltracePath $fakeFiltrace 6>&1 | Out-String
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
    Assert-True ($manifest.cases.Count -eq 4) 'Run manifest did not include every capture case.'
    Assert-True ($manifest.paths.artifactsDirectory -eq $currentArtifacts) 'Run manifest artifacts path is incorrect.'
    Assert-True ($manifest.cases[0].benchmarkId -eq 10) 'First BenchmarkDotNet case ID was not paired by name and execution order.'
    Assert-True ($manifest.cases[0].benchmark -eq 'Fake.Bench.Work') 'First benchmark name was not recorded.'
    Assert-True ($manifest.cases[0].benchmarkDisplay -eq 'FakeBench.Work(Size: 1): Job-A') 'First parameterized display was not recorded.'
    Assert-True ($manifest.cases[1].benchmarkId -eq 11) 'Second parameter ID was not paired in execution order.'
    Assert-True ($manifest.cases[1].benchmarkDisplay -eq 'FakeBench.Work(Size: 2): Job-A') 'Second parameterized display was not recorded.'
    Assert-True ($manifest.cases[2].benchmarkId -eq 4) 'Different benchmark ID was not paired by stable name.'
    Assert-True ($manifest.cases[2].benchmarkDisplay -eq 'FakeBench.Other(Size: 2): Job-A') 'Different benchmark display was not recorded.'
    Assert-True ($manifest.cases[3].benchmarkId -eq 7) 'Speedscope-only BenchmarkDotNet case ID was not paired.'
    Assert-True ($manifest.cases[3].benchmarkDisplay -eq 'FakeBench.ScopeOnly(Size: 3): Job-A') 'Speedscope-only display was not recorded.'
    Assert-True ($null -eq $manifest.cases[3].trace) 'Speedscope-only case unexpectedly gained a raw trace.'
    Assert-True ($manifest.cases[3].speedscope -eq $scopeOnly) 'Speedscope-only case path was not recorded.'
    Assert-True ($null -eq $manifest.cases[3].symbolsDirectory) 'Speedscope-only case unexpectedly gained source symbols.'
    Assert-True ($manifest.runtimes.Count -eq 1) 'Runtime identity was not recorded.'
    Assert-True ($manifest.cases[0].symbolsDirectory -eq $childSymbols) 'Exact child symbols were not discovered.'
    Assert-True ($manifest.cases[1].symbolsDirectory -eq $childSymbols) 'Exact child symbols were not paired with every matching trace.'
    Assert-True ($manifest.cases[2].symbolsDirectory -eq $childSymbols) 'Exact child symbols were not paired with the other trace.'
    Assert-True ($manifest.cases[0].symbolCandidates -contains $childSymbols) 'Logged child output was not recorded as a symbol candidate.'
    Assert-True (-not (($manifest.cases[0].symbolCandidates -join ';') -match 'evil\.example')) 'Remote logged OutDir entered symbol candidates.'
    Assert-True ($output.Contains("--symbols `"$childSymbols`"", [StringComparison]::OrdinalIgnoreCase)) 'Printed source command did not use exact child symbols.'
    Assert-True (([regex]::Matches($output, 'filtrace lines ')).Count -eq 3) 'Speedscope-only case printed a source-line command.'
    Assert-True (-not (($manifest | ConvertTo-Json -Depth 8) -match 'stale-global|stale-old-run')) 'Stale captures entered the manifest.'

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