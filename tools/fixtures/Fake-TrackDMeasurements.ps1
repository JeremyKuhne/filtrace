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

$launches = @(for ($iteration = 1; $iteration -le $TelemetryIterations; $iteration++) {
    [ordered]@{
        iteration = $iteration
        arguments = @('info', $Trace, '--format', 'json')
        totalProcessorMilliseconds = 100.0
        peakWorkingSetBytes = 50000000
        maxPrivateMemoryBytes = 25000000
        exitCode = 0
        standardOutputLength = 42
        standardErrorLength = 0
        outputSha256 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
    }
})
$telemetry = [ordered]@{
    schemaVersion = 1
    scenario = $CliScenario
    iterations = $TelemetryIterations
    launches = $launches
}
[System.IO.File]::WriteAllText(
    (Join-Path $telemetryDirectory 'cli-process.json'),
    ($telemetry | ConvertTo-Json -Depth 10),
    $utf8)
