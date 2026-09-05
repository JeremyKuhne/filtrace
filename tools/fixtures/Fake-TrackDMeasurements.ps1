#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

param(
    [string] $ArmName,
    [string] $ArmDirectory,
    [string] $Checkout,
    [string] $Trace,
    [string] $BenchmarkFilter,
    [string] $CliScenario,
    [int] $TelemetryIterations)

$ErrorActionPreference = 'Stop'
if ($env:FILTRACE_TRACKD_FAKE_FAIL_ARM -eq $ArmName) {
    throw "Injected $ArmName adapter failure."
}

if ($env:FILTRACE_TRACKD_FAKE_SLEEP_ARM -eq $ArmName) {
    Start-Sleep -Seconds 10
}

if ($env:FILTRACE_TRACKD_FAKE_OUTPUT_ARM -eq $ArmName) {
    [Console]::Out.Write('x' * (11 * 1024 * 1024))
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
$results = Join-Path $ArmDirectory 'bdn/results'
$telemetryDirectory = Join-Path $ArmDirectory 'cli-benchmark'
[System.IO.Directory]::CreateDirectory($results) | Out-Null
[System.IO.Directory]::CreateDirectory($telemetryDirectory) | Out-Null
$benchmarks = @(
    [ordered]@{
        FullName = 'Fake.Benchmark(Scenario: "one")'
        Statistics = [ordered]@{ Mean = 1000000.0 }
        Memory = [ordered]@{ BytesAllocatedPerOperation = 1024 }
    },
    [ordered]@{
        FullName = 'Fake.Benchmark(Scenario: "two")'
        Statistics = [ordered]@{ Mean = 2000000.0 }
        Memory = [ordered]@{ BytesAllocatedPerOperation = 2048 }
    })
$bdn = [ordered]@{ Benchmarks = $benchmarks }
[System.IO.File]::WriteAllText(
    (Join-Path $results 'fake-report-full-compressed.json'),
    ($bdn | ConvertTo-Json -Depth 10),
    $utf8)

$elapsedValues = if ($TelemetryIterations -eq 25) {
    [double[]](@(1000) + @(1..24))
}
else {
    [double[]]@(for ($iteration = 1; $iteration -le $TelemetryIterations; $iteration++) {
        100 + $iteration
    })
}
$launches = @(for ($index = 0; $index -lt $TelemetryIterations; $index++) {
    [int] $iteration = $index + 1
    [ordered]@{
        iteration = $iteration
        arguments = @('info', $Trace, '--format', 'json')
        elapsedMilliseconds = $elapsedValues[$index]
        totalProcessorMilliseconds = 100.0
        peakWorkingSetBytes = 50000000
        maxPrivateMemoryBytes = 25000000
        exitCode = 0
        standardOutputLength = 42
        standardErrorLength = 0
        outputSha256 = if ($ArmName -eq 'baseline') {
            'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
        } else {
            'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'
        }
        comparisonOutputSha256 = 'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC'
    }
})
[double[]] $sortedElapsed = @($elapsedValues | Sort-Object)
$telemetry = [ordered]@{
    schemaVersion = 2
    scenario = $CliScenario
    iterations = $TelemetryIterations
    complete = $true
    childWallP50Milliseconds = $sortedElapsed[[Math]::Ceiling(0.50 * $TelemetryIterations) - 1]
    childWallP95Milliseconds = $sortedElapsed[[Math]::Ceiling(0.95 * $TelemetryIterations) - 1]
    failure = $null
    launches = $launches
}

[bool] $zeroResources =
    $env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE -eq 'zero-resources-both' -or
    ($env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE -eq 'zero-resources-baseline' -and $ArmName -eq 'baseline') -or
    ($env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE -eq 'zero-resources-candidate' -and $ArmName -eq 'candidate')
if ($zeroResources) {
    foreach ($launch in $launches) {
        $launch.totalProcessorMilliseconds = 0.0
        $launch.peakWorkingSetBytes = 0
        $launch.maxPrivateMemoryBytes = 0
    }
}

if ($env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE -eq 'finite-delta-overflow') {
    [double] $elapsed = if ($ArmName -eq 'baseline') { 1e-300 } else { 1e300 }
    foreach ($launch in $launches) {
        $launch.elapsedMilliseconds = $elapsed
    }
    $telemetry.childWallP50Milliseconds = $elapsed
    $telemetry.childWallP95Milliseconds = $elapsed
}
if ($env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE -eq 'cpu-average-overflow') {
    foreach ($launch in $launches) {
        $launch.totalProcessorMilliseconds = [double]::MaxValue
    }
}

if ($ArmName -eq 'candidate') {
    switch ($env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE) {
        'old-artifact' {
            $telemetry.schemaVersion = 1
            $telemetry.Remove('complete')
            $telemetry.Remove('childWallP50Milliseconds')
            $telemetry.Remove('childWallP95Milliseconds')
            $telemetry.Remove('failure')
            foreach ($launch in $launches) {
                $launch.Remove('elapsedMilliseconds')
            }
        }
        'failure' {
            $telemetry.complete = $false
            $telemetry.childWallP50Milliseconds = $null
            $telemetry.childWallP95Milliseconds = $null
            $telemetry.failure = 'Injected child launch failure.'
        }
        'incomplete' {
            $telemetry.launches = @($launches | Select-Object -First ($TelemetryIterations - 1))
        }
        'empty-launches' {
            $telemetry.launches = @()
        }
        'malformed-elapsed' {
            $launches[0].elapsedMilliseconds = 'NaN'
        }
        'null-report' {
            $telemetry = $null
        }
        'null-launches' {
            $telemetry.launches = $null
        }
        'null-launch' {
            [object[]] $nullableLaunches = [object[]]::new($launches.Count)
            [Array]::Copy($launches, $nullableLaunches, $launches.Count)
            $nullableLaunches[0] = $null
            $telemetry.launches = $nullableLaunches
        }
        'null-arguments' {
            $launches[0].arguments = $null
        }
        'null-argument-token' {
            $launches[0].arguments = @('info', $null)
        }
        'numeric-argument-token' {
            $launches[0].arguments = @('info', 123)
        }
        'scalar-arguments' {
            $launches[0].arguments = 'info'
        }
        'negative-working-set' {
            $launches[0].peakWorkingSetBytes = -1
        }
        'negative-private-memory' {
            $launches[0].maxPrivateMemoryBytes = -1
        }
        'negative-cpu' {
            $launches[0].totalProcessorMilliseconds = -1.0
        }
        'reordered-iterations' {
            $launches[0].iteration = 2
            $launches[1].iteration = 1
        }
        'duplicate-iterations' {
            $launches[1].iteration = 1
        }
        'missing-complete' {
            $telemetry.Remove('complete')
        }
        'missing-p50' {
            $telemetry.Remove('childWallP50Milliseconds')
        }
        'corrupt-p95' {
            $telemetry.childWallP95Milliseconds++
        }
        'failure-on-complete' {
            $telemetry.failure = 'Unexpected retained failure.'
        }
        'nonzero' {
            $launches[0].exitCode = 1
        }
        'empty-output' {
            $launches[0].standardOutputLength = 0
        }
        'stderr' {
            $launches[0].standardErrorLength = 1
        }
        'inconsistent-digest' {
            $launches[0].comparisonOutputSha256 = 'D' * 64
        }
        'different-comparison-digest' {
            foreach ($launch in $launches) {
                $launch.comparisonOutputSha256 = 'D' * 64
            }
        }
        'missing-comparison-digest' {
            $launches[0].Remove('comparisonOutputSha256')
        }
        'malformed-comparison-digest' {
            $launches[0].comparisonOutputSha256 = 'not-a-digest'
        }
        'fractional-iteration' {
            $launches[0].iteration = 1.5
        }
        'string-exit-code' {
            $launches[0].exitCode = '0'
        }
        'string-elapsed' {
            $launches[0].elapsedMilliseconds = '1'
        }
        'boolean-cpu' {
            $launches[0].totalProcessorMilliseconds = $true
        }
    }
}

[string] $telemetryJson = if (
    $ArmName -eq 'candidate' -and
    $env:FILTRACE_TRACKD_FAKE_TELEMETRY_CASE -eq 'malformed-json'
) {
    '{'
}
else {
    ConvertTo-Json -InputObject $telemetry -Depth 10
}
[System.IO.File]::WriteAllText(
    (Join-Path $telemetryDirectory 'cli-process.json'),
    $telemetryJson,
    $utf8)
