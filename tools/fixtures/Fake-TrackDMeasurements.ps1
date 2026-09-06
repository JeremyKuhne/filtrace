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

[string] $zeroCpuArm = $env:FILTRACE_TRACKD_FAKE_ZERO_CPU_ARM
if (
    -not [string]::IsNullOrEmpty($zeroCpuArm) -and
    $zeroCpuArm -notin @('baseline', 'candidate')
) {
    throw "Unknown fake zero-CPU arm '$zeroCpuArm'."
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
    [System.Collections.Specialized.OrderedDictionary] $launch = [ordered]@{
        iteration = $iteration
        arguments = @('info', $Trace, '--format', 'json')
        launchToExitMilliseconds = 250.0
        totalProcessorMilliseconds = 100.0
        peakWorkingSetBytes = 50000000
        maxPrivateMemoryBytes = 25000000
        exitCode = 0
        standardOutputLength = 42
        standardErrorLength = 0
        outputSha256 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
    }
    switch ($env:FILTRACE_TRACKD_FAKE_DIGEST_MODE) {
        'alternating-case' {
            if ($iteration % 2 -eq 0) {
                $launch.outputSha256 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
            }
        }
        'different-value' {
            if ($iteration % 2 -eq 0) {
                $launch.outputSha256 = 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'
            }
        }
        { [string]::IsNullOrEmpty($_) } { }
        default { throw "Unknown fake digest mode '$env:FILTRACE_TRACKD_FAKE_DIGEST_MODE'." }
    }
    switch ($env:FILTRACE_TRACKD_FAKE_ELAPSED) {
        'missing' { $launch.Remove('launchToExitMilliseconds') }
        'malformed' { $launch.launchToExitMilliseconds = 'not-a-number' }
        'nonfinite' { $launch.launchToExitMilliseconds = 'Infinity' }
        'negative' { $launch.launchToExitMilliseconds = -1.0 }
    }
    if ($env:FILTRACE_TRACKD_FAKE_ZERO_COUNTERS -eq '1') {
        $launch.totalProcessorMilliseconds = 0.0
        $launch.peakWorkingSetBytes = 0
        $launch.maxPrivateMemoryBytes = 0
    }
    if ($zeroCpuArm -eq $ArmName) {
        $launch.totalProcessorMilliseconds = 0.0
    }
    if (-not [string]::IsNullOrEmpty($env:FILTRACE_TRACKD_FAKE_INVALID_FIELD)) {
        switch ($env:FILTRACE_TRACKD_FAKE_INVALID_VALUE) {
            'missing' { $launch.Remove($env:FILTRACE_TRACKD_FAKE_INVALID_FIELD) }
            'null' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = $null }
            'string' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = 'not-a-number' }
            'boolean' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = $true }
            'nonfinite' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = 'Infinity' }
            'negative' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = -1 }
            'fractional' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = 1.5 }
            'overflow' {
                $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] =
                    [System.Numerics.BigInteger]::Parse('9223372036854775808')
            }
            'zero' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = 0 }
            'one' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = 1 }
            'bad-digest-length' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = 'AAAA' }
            'bad-digest-hex' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = 'G' * 64 }
            'empty-array' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = @() }
            'not-array' { $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = 'info' }
            'nonstring-array' {
                $launch[$env:FILTRACE_TRACKD_FAKE_INVALID_FIELD] = @('info', 1)
            }
            default {
                throw "Unknown fake telemetry value '$env:FILTRACE_TRACKD_FAKE_INVALID_VALUE'."
            }
        }
    }

    $launch
})
$telemetry = [ordered]@{
    schemaVersion = 2
    scenario = $CliScenario
    iterations = $TelemetryIterations
    launches = $launches
}
switch ($env:FILTRACE_TRACKD_FAKE_ELAPSED) {
    'schema1' { $telemetry.schemaVersion = 1 }
    'schema-missing' { $telemetry.Remove('schemaVersion') }
    'schema-null' { $telemetry.schemaVersion = $null }
    'schema-boolean' { $telemetry.schemaVersion = $true }
    'schema-string' { $telemetry.schemaVersion = '2' }
    'schema-fractional' { $telemetry.schemaVersion = 2.4 }
    'schema-nonfinite' { $telemetry.schemaVersion = 'Infinity' }
    'schema-integral-double' { $telemetry.schemaVersion = 2.0 }
    'empty' {
        $telemetry.iterations = 0
        $telemetry.launches = @()
    }
    'iterations-fractional' { $telemetry.iterations = 1.5 }
    'iterations-null' { $telemetry.iterations = $null }
    'iterations-missing' { $telemetry.Remove('iterations') }
    'iterations-string' { $telemetry.iterations = '1' }
    'iterations-boolean' { $telemetry.iterations = $true }
    'launches-missing' { $telemetry.Remove('launches') }
    'launches-null' { $telemetry.launches = $null }
    'launches-empty' { $telemetry.launches = @() }
    'launches-object' {
        $telemetry.iterations = 1
        $telemetry.launches = $launches[0]
    }
    'launches-string' {
        $telemetry.iterations = 1
        $telemetry.launches = 'not-an-array'
    }
}
[System.IO.File]::WriteAllText(
    (Join-Path $telemetryDirectory 'cli-process.json'),
    ($telemetry | ConvertTo-Json -Depth 10),
    $utf8)
