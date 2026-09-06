#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Run a reconstructable Track D baseline/candidate measurement comparison.

.DESCRIPTION
  Creates exact detached worktrees (or accepts explicit checkouts for a smoke),
  verifies both use the same benchmark tree, restores identical corpus bytes,
  builds both arms, runs one filtered BenchmarkDotNet comparison and one CLI
  telemetry scenario per arm, and writes run.json, comparison.json, commands.txt,
  and ledger.md. This wrapper does not decide whether an optimization is retained.

.PARAMETER InputCorpusDirectory
  Directory containing input-corpus.zip and input-corpus.manifest.json.

.PARAMETER HarnessCommit
  Commit containing the shared benchmark harness. Defaults to HEAD.

.PARAMETER BaselineCommit
  Baseline product commit. Defaults to HarnessCommit.

.PARAMETER CandidateCommit
  Candidate product commit. Defaults to HarnessCommit for a no-op comparison.

.PARAMETER BaselineCheckout
  Existing checkout used instead of creating a detached baseline worktree. Test-only.

.PARAMETER CandidateCheckout
  Existing checkout used instead of creating a detached candidate worktree. Test-only.

.PARAMETER AllowDirtyCheckouts
  Allow explicit test checkouts with local changes. Retained runs should omit this.

.PARAMETER OutputDirectory
  Empty output directory. Defaults to a unique ignored artifacts/perf/Phase-0 path.

.PARAMETER BenchmarkFilter
  BenchmarkDotNet filter. Defaults to the focused metric-parity SelfTime rows.

.PARAMETER BenchmarkJob
  BenchmarkDotNet job: default, short, or dry. Defaults to dry for harness iteration.

.PARAMETER CliScenario
  Implemented CLI benchmark scenario used for child telemetry. Defaults to info-warm.

.PARAMETER TelemetryIterations
  Child telemetry launches per arm. Defaults to 3; retained runs use 25.

.PARAMETER TraceArchivePath
  Trace path inside input-corpus.zip. Defaults to inputs/cpu-10k-d20.nettrace.

.PARAMETER DotnetPath
  dotnet host path or command name.

.PARAMETER GitPath
  git path or command name.

.PARAMETER NoBuild
  Reuse existing Release outputs. Intended only with explicit checkouts.

.PARAMETER KeepWorktrees
  Keep detached worktrees after the run for inspection.

.PARAMETER NativeTimeoutSeconds
    Maximum time for one build, benchmark, telemetry, git, or adapter process.
    Defaults to 7200 seconds.

.PARAMETER TestAdapterPath
    Internal. Test-only PowerShell adapter that writes BDN and telemetry artifacts.
    Requires explicit checkouts, AllowDirtyCheckouts, and NoBuild.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $InputCorpusDirectory,
    [string] $HarnessCommit = 'HEAD',
    [string] $BaselineCommit,
    [string] $CandidateCommit,
    [string] $BaselineCheckout,
    [string] $CandidateCheckout,
    [switch] $AllowDirtyCheckouts,
    [string] $OutputDirectory,
    [string] $BenchmarkFilter = '*FoldingAggregatorMetricBenchmarks.SelfTime*',
    [ValidateSet('default', 'short', 'dry')][string] $BenchmarkJob = 'dry',
    [string] $CliScenario = 'info-warm',
    [ValidateRange(1, 100)][int] $TelemetryIterations = 3,
    [string] $TraceArchivePath = 'inputs/cpu-10k-d20.nettrace',
    [string] $DotnetPath = 'dotnet',
    [string] $GitPath = 'git',
    [ValidateRange(1, 86400)][int] $NativeTimeoutSeconds = 7200,
    [switch] $NoBuild,
    [switch] $KeepWorktrees,
    [string] $TestAdapterPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$commandLog = [System.Collections.Generic.List[string]]::new()
$maximumCorpusEntries = 64
$maximumCorpusEntryBytes = 512MB
$maximumCorpusExpandedBytes = 2GB
$maximumCapturedBytes = 10 * 1024 * 1024
$filtracePathEnvironmentVariable = 'FILTRACE_BENCHMARK_CLI_PATH'
$nativeCleanupTimeoutMilliseconds = 10000

function Resolve-Executable([string] $Command, [string] $Purpose) {
    if (Test-Path -LiteralPath $Command -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Command).Path
    }

    [System.Management.Automation.CommandInfo[]] $resolved = @(
        Get-Command `
            $Command `
            -CommandType Application `
            -ErrorAction SilentlyContinue)
    if ($resolved.Count -eq 0) {
        throw "$Purpose was not found at '$Command' or on PATH."
    }

    return $resolved[0].Source
}

function Format-Command([string] $Executable, [string[]] $Arguments) {
    [string[]] $pieces = @($Executable) + @($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            return '"' + $_.Replace('"', '\"', [StringComparison]::Ordinal) + '"'
        }

        return $_
    })
    return $pieces -join ' '
}

function Invoke-NativeChecked(
    [string] $Executable,
    [string[]] $Arguments,
    [string] $WorkingDirectory,
    [string] $Purpose) {
    $commandLog.Add("[$WorkingDirectory] $(Format-Command $Executable $Arguments)")
    [System.Diagnostics.Process] $process = Start-NativeProcess `
        $Executable `
        $Arguments `
        $WorkingDirectory `
        $false
    try {
        Wait-NativeProcess $process $Purpose
        if ($process.ExitCode -ne 0) {
            throw "$Purpose exited with code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-NativeText(
    [string] $Executable,
    [string[]] $Arguments,
    [string] $WorkingDirectory,
    [string] $Purpose) {
    $commandLog.Add("[$WorkingDirectory] $(Format-Command $Executable $Arguments)")
    [string] $outputPath = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        "filtrace-trackd-output-$([Guid]::NewGuid().ToString('N')).tmp"
    [string] $errorPath = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        "filtrace-trackd-error-$([Guid]::NewGuid().ToString('N')).tmp"
    [System.Diagnostics.Process] $process = Start-NativeProcess `
        $Executable `
        $Arguments `
        $WorkingDirectory `
        $true
    [System.IO.FileStream] $outputStream = [System.IO.File]::Create($outputPath)
    [System.IO.FileStream] $errorStream = [System.IO.File]::Create($errorPath)
    [System.Threading.Tasks.Task] $standardOutput = `
        $process.StandardOutput.BaseStream.CopyToAsync($outputStream)
    [System.Threading.Tasks.Task] $standardError = `
        $process.StandardError.BaseStream.CopyToAsync($errorStream)
    try {
        Wait-NativeProcess $process $Purpose @($outputPath, $errorPath)
        [System.Threading.Tasks.Task]::WhenAll(
            [System.Threading.Tasks.Task[]]@($standardOutput, $standardError)).GetAwaiter().GetResult()
        $outputStream.Flush()
        $errorStream.Flush()
        $outputStream.Dispose()
        $errorStream.Dispose()
        if (
            (Get-Item -LiteralPath $outputPath).Length -gt $maximumCapturedBytes -or
            (Get-Item -LiteralPath $errorPath).Length -gt $maximumCapturedBytes
        ) {
            throw [System.IO.InvalidDataException]::new(
                "$Purpose output exceeded $maximumCapturedBytes bytes.")
        }

        [string] $text = [System.IO.File]::ReadAllText($outputPath)
        [string] $error = [System.IO.File]::ReadAllText($errorPath)

        if ($process.ExitCode -ne 0) {
            [string] $detail = if ($error.Length -le 1000) { $error } else { $error.Substring(0, 1000) }
            throw "$Purpose exited with code $($process.ExitCode): $detail"
        }

        return $text.Trim()
    }
    finally {
        try {
            [System.Threading.Tasks.Task]::WhenAll(
                [System.Threading.Tasks.Task[]]@($standardOutput, $standardError)).Wait(
                    $nativeCleanupTimeoutMilliseconds) | Out-Null
        }
        catch { }
        $outputStream.Dispose()
        $errorStream.Dispose()
        $process.Dispose()
        Remove-Item -LiteralPath $outputPath,$errorPath -Force -ErrorAction SilentlyContinue
    }
}

function Start-NativeProcess(
    [string] $Executable,
    [string[]] $Arguments,
    [string] $WorkingDirectory,
    [bool] $CaptureOutput) {
    [System.Diagnostics.ProcessStartInfo] $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $CaptureOutput
    $start.RedirectStandardError = $CaptureOutput
    foreach ($argument in $Arguments) {
        $start.ArgumentList.Add($argument)
    }

    [System.Diagnostics.Process] $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) {
        $process.Dispose()
        throw "Could not start '$Executable'."
    }

    return $process
}

function Wait-NativeProcess(
    [System.Diagnostics.Process] $Process,
    [string] $Purpose,
    [string[]] $BoundedOutputPaths = @()) {
    [int] $timeoutMilliseconds = [int][Math]::Min(
        [long]$NativeTimeoutSeconds * 1000,
        [int]::MaxValue)
    [System.Diagnostics.Stopwatch] $elapsed = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $Process.WaitForExit(100)) {
        foreach ($path in $BoundedOutputPaths) {
            if (
                (Test-Path -LiteralPath $path) -and
                (Get-Item -LiteralPath $path).Length -gt $maximumCapturedBytes
            ) {
                [string] $cleanup = Stop-NativeProcess $Process
                throw [System.IO.InvalidDataException]::new(
                    "$Purpose output exceeded $maximumCapturedBytes bytes.$cleanup")
            }
        }

        if ($elapsed.ElapsedMilliseconds -ge $timeoutMilliseconds) {
            [string] $cleanup = Stop-NativeProcess $Process
            throw [TimeoutException]::new(
                "$Purpose did not finish within $NativeTimeoutSeconds seconds.$cleanup")
        }
    }

    foreach ($path in $BoundedOutputPaths) {
        if (
            (Test-Path -LiteralPath $path) -and
            (Get-Item -LiteralPath $path).Length -gt $maximumCapturedBytes
        ) {
            throw [System.IO.InvalidDataException]::new(
                "$Purpose output exceeded $maximumCapturedBytes bytes.")
        }
    }
}

function Stop-NativeProcess([System.Diagnostics.Process] $Process) {
    [string] $cleanup = ''
    try {
        $Process.Kill($true)
    }
    catch {
        $cleanup = " Process-tree termination failed: $($_.Exception.Message)"
    }

    try {
        if (-not $Process.WaitForExit($nativeCleanupTimeoutMilliseconds)) {
            $cleanup += " Process did not exit within $nativeCleanupTimeoutMilliseconds ms after termination."
        }
    }
    catch {
        $cleanup += " Process cleanup wait failed: $($_.Exception.Message)"
    }

    return $cleanup
}

function Write-JsonAtomic([string] $Path, [object] $Value) {
    [string] $directory = [System.IO.Path]::GetDirectoryName($Path)
    [string] $name = [System.IO.Path]::GetFileName($Path)
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    [string] $temporary = Join-Path $directory ".$name.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [string] $json = $Value | ConvertTo-Json -Depth 20
        [System.IO.File]::WriteAllText($temporary, "$json`n", $utf8)
        Move-Item -LiteralPath $temporary -Destination $Path -Force -ErrorAction Stop
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction Stop
        }
    }
}

function Get-TreeHash([string] $Checkout, [string] $RelativeDirectory) {
    [string] $directory = Join-Path $Checkout $RelativeDirectory
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "Tree '$directory' does not exist."
    }

    [System.Security.Cryptography.IncrementalHash] $hash =
        [System.Security.Cryptography.IncrementalHash]::CreateHash(
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        [System.IO.FileInfo[]] $files = @(
            Get-ChildItem -LiteralPath $directory -Recurse -File |
                Where-Object {
                    $_.FullName -notmatch '[\\/](bin|obj|BenchmarkDotNet\.Artifacts)[\\/]'
                } |
                Sort-Object FullName)
        foreach ($file in $files) {
            [string] $relative = [System.IO.Path]::GetRelativePath($directory, $file.FullName)
            $relative = $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            $hash.AppendData([Text.Encoding]::UTF8.GetBytes("$relative`0"))
            [System.IO.FileStream] $stream = $file.OpenRead()
            try {
                [byte[]] $buffer = [byte[]]::new(81920)
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if ($read -eq $buffer.Length) {
                        $hash.AppendData($buffer)
                    }
                    else {
                        [byte[]] $partial = [byte[]]::new($read)
                        [Array]::Copy($buffer, $partial, $read)
                        $hash.AppendData($partial)
                    }
                }
            }
            finally {
                $stream.Dispose()
            }
        }

        return [Convert]::ToHexString($hash.GetHashAndReset())
    }
    finally {
        $hash.Dispose()
    }
}

function Resolve-CorpusTrace([string] $ExtractionRoot, [string] $RelativePath) {
    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw 'TraceArchivePath must be relative to the corpus archive root.'
    }

    [string] $fullRoot = [System.IO.Path]::GetFullPath($ExtractionRoot)
    [string] $fullPath = [System.IO.Path]::GetFullPath((Join-Path $fullRoot $RelativePath))
    [string] $relative = [System.IO.Path]::GetRelativePath($fullRoot, $fullPath)
    if ($relative -eq '..' -or $relative.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) {
        throw "TraceArchivePath '$RelativePath' escapes the corpus root."
    }

    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Corpus trace '$RelativePath' was not found after extraction."
    }

    return $fullPath
}

function Expand-CorpusArchive([string] $ArchivePath, [string] $Destination) {
    [System.IO.Compression.ZipArchive] $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -gt $maximumCorpusEntries) {
            throw "Corpus archive has $($archive.Entries.Count) entries; maximum is $maximumCorpusEntries."
        }

        [long] $expandedBytes = 0
        [long] $actualExpandedBytes = 0
        [System.Collections.Generic.HashSet[string]] $destinations =
            [System.Collections.Generic.HashSet[string]]::new(
                $(if ([OperatingSystem]::IsWindows() -or [OperatingSystem]::IsMacOS()) {
                    [StringComparer]::OrdinalIgnoreCase
                } else {
                    [StringComparer]::Ordinal
                }))
        [string] $fullRoot = [System.IO.Path]::GetFullPath($Destination)
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) {
                throw "Corpus archive entry '$($entry.FullName)' is not a regular file."
            }

            if ($entry.Length -gt $maximumCorpusEntryBytes) {
                throw "Corpus archive entry '$($entry.FullName)' is $($entry.Length) bytes; maximum is $maximumCorpusEntryBytes."
            }

            if ($entry.Length -gt $maximumCorpusExpandedBytes - $expandedBytes) {
                throw "Corpus archive expands to more than $maximumCorpusExpandedBytes bytes."
            }

            $expandedBytes += $entry.Length
            if ($expandedBytes -gt $maximumCorpusExpandedBytes) {
                throw "Corpus archive expands to more than $maximumCorpusExpandedBytes bytes."
            }

            [int] $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixType -eq 0xA000) {
                throw "Corpus archive entry '$($entry.FullName)' is a symbolic link."
            }

            [string] $destinationPath = [System.IO.Path]::GetFullPath(
                (Join-Path $fullRoot $entry.FullName))
            [string] $relative = [System.IO.Path]::GetRelativePath($fullRoot, $destinationPath)
            if (
                $relative -eq '..' -or
                $relative.StartsWith(
                    "..$([System.IO.Path]::DirectorySeparatorChar)",
                    [StringComparison]::Ordinal) -or
                -not $destinations.Add($destinationPath)
            ) {
                throw "Corpus archive entry '$($entry.FullName)' has an unsafe or duplicate destination."
            }

            [string] $parent = [System.IO.Path]::GetDirectoryName($destinationPath)
            [System.IO.Directory]::CreateDirectory($parent) | Out-Null
            [System.IO.Stream] $source = $entry.Open()
            [System.IO.FileStream] $target = [System.IO.File]::Create($destinationPath)
            [long] $copiedBytes = 0
            try {
                [byte[]] $buffer = [byte[]]::new(81920)
                while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if (
                        $read -gt $entry.Length - $copiedBytes -or
                        $read -gt $maximumCorpusExpandedBytes - $actualExpandedBytes
                    ) {
                        throw "Corpus archive entry '$($entry.FullName)' expanded beyond its declared or total size limit."
                    }

                    $target.Write($buffer, 0, $read)
                    $copiedBytes += $read
                    $actualExpandedBytes += $read
                }
            }
            catch {
                $target.Dispose()
                $source.Dispose()
                Remove-Item -LiteralPath $destinationPath -Force -ErrorAction SilentlyContinue
                throw
            }
            finally {
                $target.Dispose()
                $source.Dispose()
            }

            if ($copiedBytes -ne $entry.Length) {
                throw "Corpus archive entry '$($entry.FullName)' extracted with the wrong length."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-RestoredCorpus([string] $ExtractionRoot, [object] $Manifest) {
    [object[]] $traceRecords = @($Manifest.traces)
    if ($traceRecords.Count -eq 0 -or $traceRecords.Count -gt $maximumCorpusEntries) {
        throw "Corpus manifest has $($traceRecords.Count) trace records; expected 1-$maximumCorpusEntries."
    }

    [System.Collections.Generic.HashSet[string]] $expectedPaths =
        [System.Collections.Generic.HashSet[string]]::new(
            $(if ([OperatingSystem]::IsWindows() -or [OperatingSystem]::IsMacOS()) {
                [StringComparer]::OrdinalIgnoreCase
            } else {
                [StringComparer]::Ordinal
            }))
    foreach ($traceRecord in $traceRecords) {
        [string] $relativePath = [string]$traceRecord.archivePath
        [string] $path = Resolve-CorpusTrace $ExtractionRoot $relativePath
        if (-not $expectedPaths.Add($path)) {
            throw "Corpus manifest repeats trace path '$relativePath'."
        }

        [System.IO.FileInfo] $file = Get-Item -LiteralPath $path
        [string] $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (
            $file.Length -ne [long]$traceRecord.bytes -or
            $hash -cne [string]$traceRecord.sha256
        ) {
            throw "Restored trace '$relativePath' does not match its manifest length and hash."
        }
    }

    [string[]] $actualFiles = @(
        Get-ChildItem -LiteralPath $ExtractionRoot -Recurse -File |
            ForEach-Object FullName)
    if (
        $actualFiles.Count -ne $expectedPaths.Count -or
        @($actualFiles | Where-Object { -not $expectedPaths.Contains($_) }).Count -ne 0
    ) {
        throw 'Restored corpus contains files not declared by the manifest.'
    }
}

function Get-BenchmarkComparison([string] $BaselineReport, [string] $CandidateReport) {
    [object] $baseline = Get-Content -LiteralPath $BaselineReport -Raw | ConvertFrom-Json
    [object] $candidate = Get-Content -LiteralPath $CandidateReport -Raw | ConvertFrom-Json
    [System.Collections.Generic.Dictionary[string, object]] $candidateByName =
        [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($benchmark in $candidate.Benchmarks) {
        $candidateByName.Add([string]$benchmark.FullName, $benchmark)
    }

    [System.Collections.Generic.List[object]] $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($before in $baseline.Benchmarks) {
        [object] $after = $null
        if (-not $candidateByName.TryGetValue([string]$before.FullName, [ref]$after)) {
            throw "Candidate BenchmarkDotNet report is missing '$($before.FullName)'."
        }

        [double] $beforeMean = $before.Statistics.Mean
        [double] $afterMean = $after.Statistics.Mean
        [double] $delta = if ($beforeMean -eq 0.0) { 0.0 } else { ($afterMean - $beforeMean) / $beforeMean * 100.0 }
        [long] $beforeBytes = $before.Memory.BytesAllocatedPerOperation
        [long] $afterBytes = $after.Memory.BytesAllocatedPerOperation
        $rows.Add([ordered]@{
            fullName = $before.FullName
            baselineMeanNanoseconds = $beforeMean
            candidateMeanNanoseconds = $afterMean
            meanDeltaPercent = $delta
            baselineAllocatedBytes = $beforeBytes
            candidateAllocatedBytes = $afterBytes
            allocatedDeltaBytes = $afterBytes - $beforeBytes
        })
    }

    if ($rows.Count -ne $candidate.Benchmarks.Count) {
        throw 'BenchmarkDotNet reports contain different row counts.'
    }

    return @($rows)
}

function Get-RequiredTelemetryValue([object] $Record, [string] $Name, [string] $Path) {
    [object] $property = $Record.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Telemetry report '$Path' is missing $Name."
    }

    return ,$property.Value
}

function Test-JsonNumber([object] $Value) {
    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [System.Numerics.BigInteger]) {
        return $true
    }

    [TypeCode] $typeCode = [Type]::GetTypeCode($Value.GetType())
    return $typeCode -in @(
        [TypeCode]::SByte,
        [TypeCode]::Byte,
        [TypeCode]::Int16,
        [TypeCode]::UInt16,
        [TypeCode]::Int32,
        [TypeCode]::UInt32,
        [TypeCode]::Int64,
        [TypeCode]::UInt64,
        [TypeCode]::Single,
        [TypeCode]::Double,
        [TypeCode]::Decimal)
}

function Get-TelemetryNumber([object] $Record, [string] $Name, [string] $Path) {
    [object] $value = Get-RequiredTelemetryValue $Record $Name $Path
    if (
        $value -is [string] -and
        $value -in @('NaN', 'Infinity', '+Infinity', '-Infinity')
    ) {
        throw "Telemetry report '$Path' has nonfinite $Name."
    }

    if (-not (Test-JsonNumber $value)) {
        throw "Telemetry report '$Path' $Name must be a JSON number."
    }

    if (
        $value -is [float] -or
        $value -is [double]
    ) {
        [double] $floatingPointValue = $value
        if (-not [double]::IsFinite($floatingPointValue)) {
            throw "Telemetry report '$Path' has nonfinite $Name."
        }
    }

    return $value
}

function Get-TelemetryNonnegativeDouble([object] $Record, [string] $Name, [string] $Path) {
    [object] $value = Get-TelemetryNumber $Record $Name $Path
    [double] $number = $value
    if (-not [double]::IsFinite($number)) {
        throw "Telemetry report '$Path' has nonfinite $Name."
    }

    if ($number -lt 0.0) {
        throw "Telemetry report '$Path' has negative $Name."
    }

    return $number
}

function Get-TelemetryNonnegativeInt64([object] $Record, [string] $Name, [string] $Path) {
    [object] $value = Get-TelemetryNumber $Record $Name $Path
    try {
        [decimal] $number = $value
    }
    catch {
        throw "Telemetry report '$Path' $Name must be an integer from 0 through Int64.MaxValue."
    }

    if (
        $number -lt 0 -or
        $number -ne [decimal]::Truncate($number) -or
        $number -gt [long]::MaxValue
    ) {
        throw "Telemetry report '$Path' $Name must be an integer from 0 through Int64.MaxValue."
    }

    return [long]$number
}

function Get-TelemetrySummary([string] $Path) {
    [object] $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([int]$report.schemaVersion -ne 2) {
        throw "Telemetry report '$Path' does not use schema version 2."
    }

    [object] $iterationsProperty = $report.PSObject.Properties['iterations']
    [object] $iterationsValue = if ($null -eq $iterationsProperty) {
        $null
    }
    else {
        $iterationsProperty.Value
    }
    if (
        $null -eq $iterationsValue -or
        $iterationsValue -is [string] -or
        $iterationsValue -is [bool] -or
        $iterationsValue -isnot [ValueType]
    ) {
        throw "Telemetry report '$Path' iterations must be a positive integer JSON number."
    }

    [double] $iterationsNumber = $iterationsValue
    if (
        -not [double]::IsFinite($iterationsNumber) -or
        $iterationsNumber -le 0.0 -or
        $iterationsNumber -ne [Math]::Truncate($iterationsNumber) -or
        $iterationsNumber -gt [int]::MaxValue
    ) {
        throw "Telemetry report '$Path' iterations must be a positive integer JSON number."
    }

    [int] $iterations = [int]$iterationsNumber
    [object[]] $launches = @($report.launches)
    if ($launches.Count -ne $iterations) {
        throw "Telemetry report '$Path' has an incomplete launch set."
    }

    [System.Collections.Generic.List[double]] $launchToExitMilliseconds = @()
    [System.Collections.Generic.List[double]] $totalProcessorMilliseconds = @()
    [System.Collections.Generic.List[long]] $peakWorkingSetBytes = @()
    [System.Collections.Generic.List[long]] $maxPrivateMemoryBytes = @()
    [System.Collections.Generic.List[long]] $standardOutputLength = @()
    [System.Collections.Generic.HashSet[string]] $outputDigests =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($launchIndex = 0; $launchIndex -lt $launches.Count; $launchIndex++) {
        [object] $launch = $launches[$launchIndex]
        [long] $iteration = Get-TelemetryNonnegativeInt64 $launch 'iteration' $Path
        if ($iteration -ne $launchIndex + 1) {
            throw "Telemetry report '$Path' launch iterations must be the unique ordinals 1 through $iterations."
        }

        [object] $arguments = Get-RequiredTelemetryValue $launch 'arguments' $Path
        if (
            $arguments -isnot [array] -or
            $arguments.Count -eq 0 -or
            @($arguments | Where-Object { $_ -isnot [string] }).Count -ne 0
        ) {
            throw "Telemetry report '$Path' arguments must be a nonempty JSON array of strings."
        }

        $launchToExitMilliseconds.Add(
            (Get-TelemetryNonnegativeDouble $launch 'launchToExitMilliseconds' $Path))
        $totalProcessorMilliseconds.Add(
            (Get-TelemetryNonnegativeDouble $launch 'totalProcessorMilliseconds' $Path))
        $peakWorkingSetBytes.Add(
            (Get-TelemetryNonnegativeInt64 $launch 'peakWorkingSetBytes' $Path))
        $maxPrivateMemoryBytes.Add(
            (Get-TelemetryNonnegativeInt64 $launch 'maxPrivateMemoryBytes' $Path))

        [long] $exitCode = Get-TelemetryNonnegativeInt64 $launch 'exitCode' $Path
        if ($exitCode -ne 0) {
            throw "Telemetry report '$Path' has nonzero exitCode."
        }

        [long] $outputLength = Get-TelemetryNonnegativeInt64 $launch 'standardOutputLength' $Path
        if ($outputLength -eq 0) {
            throw "Telemetry report '$Path' standardOutputLength must be positive."
        }
        $standardOutputLength.Add($outputLength)

        [long] $errorLength = Get-TelemetryNonnegativeInt64 $launch 'standardErrorLength' $Path
        if ($errorLength -ne 0) {
            throw "Telemetry report '$Path' has nonzero standardErrorLength."
        }

        [object] $digest = Get-RequiredTelemetryValue $launch 'outputSha256' $Path
        if ($digest -isnot [string] -or $digest -notmatch '\A[0-9A-Fa-f]{64}\z') {
            throw "Telemetry report '$Path' outputSha256 must be a 64-character hexadecimal string."
        }
        $outputDigests.Add($digest) | Out-Null
    }

    return [ordered]@{
        scenario = $report.scenario
        iterations = $iterations
        averageLaunchToExitMilliseconds = [double](
            $launchToExitMilliseconds | Measure-Object -Average).Average
        averageCpuMilliseconds = [double](
            $totalProcessorMilliseconds | Measure-Object -Average).Average
        maxPeakWorkingSetBytes = [long](
            $peakWorkingSetBytes | Measure-Object -Maximum).Maximum
        maxPrivateMemoryBytes = [long](
            $maxPrivateMemoryBytes | Measure-Object -Maximum).Maximum
        averageOutputLength = [double](
            $standardOutputLength | Measure-Object -Average).Average
        distinctOutputDigests = $outputDigests.Count
    }
}

function Get-ReportPath([string] $ResultsDirectory) {
    [System.IO.FileInfo[]] $reports = @(
        Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*-report-full-compressed.json' -File)
    if ($reports.Count -ne 1) {
        throw "Expected one BenchmarkDotNet JSON report in '$ResultsDirectory'; found $($reports.Count)."
    }

    return $reports[0].FullName
}

function Test-TelemetryCapability([string] $Checkout, [string] $ArmName) {
    [string] $help = Invoke-NativeText `
        $script:dotnet `
        @(
            'run', '-c', 'Release', '--no-build',
            '--project', 'benchmarks/Filtrace.Benchmarks', '--',
            '--cli-telemetry', '--help') `
        $Checkout `
        "$ArmName telemetry capability"
    if (-not $help.StartsWith('Usage: --cli-telemetry', [StringComparison]::Ordinal)) {
        throw "$ArmName checkout does not contain the required --cli-telemetry harness."
    }
}

$script:dotnet = Resolve-Executable $DotnetPath 'dotnet'
$script:git = Resolve-Executable $GitPath 'git'
$script:powershellHost = (Get-Process -Id $PID).Path
$inputDirectory = [System.IO.Path]::GetFullPath($InputCorpusDirectory)
$inputArchive = Join-Path $inputDirectory 'input-corpus.zip'
$inputManifest = Join-Path $inputDirectory 'input-corpus.manifest.json'
if (
    -not (Test-Path -LiteralPath $inputArchive -PathType Leaf) -or
    -not (Test-Path -LiteralPath $inputManifest -PathType Leaf)
) {
    throw "InputCorpusDirectory '$inputDirectory' must contain input-corpus.zip and input-corpus.manifest.json."
}

[object] $corpusManifest = Get-Content -LiteralPath $inputManifest -Raw | ConvertFrom-Json
[string] $archiveHash = (Get-FileHash -LiteralPath $inputArchive -Algorithm SHA256).Hash
if ([string]$corpusManifest.archive.sha256 -cne $archiveHash) {
    throw 'Input corpus archive hash does not match its manifest.'
}

if ([string]::IsNullOrEmpty($BaselineCommit)) { $BaselineCommit = $HarnessCommit }
if ([string]::IsNullOrEmpty($CandidateCommit)) { $CandidateCommit = $HarnessCommit }
if ([string]::IsNullOrEmpty($OutputDirectory)) {
    [string] $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $OutputDirectory = Join-Path $root "artifacts/perf/Phase-0/noop-$stamp"
}

$runDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $runDirectory) {
    if (
        -not (Test-Path -LiteralPath $runDirectory -PathType Container) -or
        @(Get-ChildItem -LiteralPath $runDirectory -Force).Count -ne 0
    ) {
        throw "OutputDirectory '$runDirectory' must be absent or empty."
    }
}
else {
    [System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null
}

$explicitCheckouts = (
    -not [string]::IsNullOrEmpty($BaselineCheckout) -or
    -not [string]::IsNullOrEmpty($CandidateCheckout))

$createdWorktrees = [System.Collections.Generic.List[string]]::new()
$baselinePath = $null
$candidatePath = $null
$runError = $null
$resolvedTestAdapter = $null
$runCompleted = $false
try {
    Write-JsonAtomic (Join-Path $runDirectory 'run-status.json') ([ordered]@{
        status = 'in-progress'
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    })

    if ($explicitCheckouts -and (
        [string]::IsNullOrEmpty($BaselineCheckout) -or [string]::IsNullOrEmpty($CandidateCheckout))) {
        throw 'BaselineCheckout and CandidateCheckout must be supplied together.'
    }

    if (-not [string]::IsNullOrEmpty($TestAdapterPath)) {
        if (-not $explicitCheckouts -or -not $AllowDirtyCheckouts -or -not $NoBuild) {
            throw 'TestAdapterPath requires explicit checkouts, AllowDirtyCheckouts, and NoBuild.'
        }

        $resolvedTestAdapter = (Resolve-Path -LiteralPath $TestAdapterPath).Path
    }

    if ($explicitCheckouts) {
        $baselinePath = (Resolve-Path -LiteralPath $BaselineCheckout).Path
        $candidatePath = (Resolve-Path -LiteralPath $CandidateCheckout).Path
    }
    else {
        [string] $resolvedHarness = Invoke-NativeText $script:git @('rev-parse', "$HarnessCommit^{commit}") $root 'resolve harness commit'
        [string] $resolvedBaseline = Invoke-NativeText $script:git @('rev-parse', "$BaselineCommit^{commit}") $root 'resolve baseline commit'
        [string] $resolvedCandidate = Invoke-NativeText $script:git @('rev-parse', "$CandidateCommit^{commit}") $root 'resolve candidate commit'
        $HarnessCommit = $resolvedHarness
        $BaselineCommit = $resolvedBaseline
        $CandidateCommit = $resolvedCandidate
        $baselinePath = Join-Path $runDirectory '.worktrees/baseline'
        $candidatePath = Join-Path $runDirectory '.worktrees/candidate'
        Invoke-NativeChecked $script:git @('worktree', 'add', '--detach', $baselinePath, $BaselineCommit) $root 'create baseline worktree'
        $createdWorktrees.Add($baselinePath)
        Invoke-NativeChecked $script:git @('worktree', 'add', '--detach', $candidatePath, $CandidateCommit) $root 'create candidate worktree'
        $createdWorktrees.Add($candidatePath)
    }

    [string] $baselineHead = Invoke-NativeText $script:git @('rev-parse', 'HEAD') $baselinePath 'read baseline commit'
    [string] $candidateHead = Invoke-NativeText $script:git @('rev-parse', 'HEAD') $candidatePath 'read candidate commit'
    [string] $baselineStatus = Invoke-NativeText $script:git @('status', '--porcelain') $baselinePath 'read baseline status'
    [string] $candidateStatus = Invoke-NativeText $script:git @('status', '--porcelain') $candidatePath 'read candidate status'
    if (-not $AllowDirtyCheckouts -and ($baselineStatus.Length -ne 0 -or $candidateStatus.Length -ne 0)) {
        throw 'Baseline and candidate checkouts must be clean for a retained run.'
    }

    [string] $baselineHarnessHash = Get-TreeHash $baselinePath 'benchmarks'
    [string] $candidateHarnessHash = Get-TreeHash $candidatePath 'benchmarks'
    if ($baselineHarnessHash -cne $candidateHarnessHash) {
        throw "Benchmark trees differ: baseline $baselineHarnessHash, candidate $candidateHarnessHash."
    }

    Copy-Item -LiteralPath $inputArchive -Destination (Join-Path $runDirectory 'input-corpus.zip')
    Copy-Item -LiteralPath $inputManifest -Destination (Join-Path $runDirectory 'input-corpus.manifest.json')

    foreach ($arm in @(
        [pscustomobject]@{ Name = 'baseline'; Checkout = $baselinePath },
        [pscustomobject]@{ Name = 'candidate'; Checkout = $candidatePath })) {
        [string] $armDirectory = Join-Path $runDirectory $arm.Name
        [string] $inputRoot = Join-Path $armDirectory 'input-corpus'
        [System.IO.Directory]::CreateDirectory($inputRoot) | Out-Null
        Expand-CorpusArchive $inputArchive $inputRoot
        Test-RestoredCorpus $inputRoot $corpusManifest
        [string] $trace = Resolve-CorpusTrace $inputRoot $TraceArchivePath

        if ($null -ne $resolvedTestAdapter) {
            $null = Invoke-NativeText `
                $script:powershellHost `
                @(
                    '-NoProfile', '-File', $resolvedTestAdapter,
                    '-ArmName', $arm.Name,
                    '-ArmDirectory', $armDirectory,
                    '-Checkout', $arm.Checkout,
                    '-Trace', $trace,
                    '-BenchmarkFilter', $BenchmarkFilter,
                    '-CliScenario', $CliScenario,
                    '-TelemetryIterations', $TelemetryIterations.ToString(
                        [Globalization.CultureInfo]::InvariantCulture)) `
                $root `
                "$($arm.Name) test adapter"
            continue
        }

        if (-not $NoBuild) {
            Invoke-NativeChecked `
                $script:dotnet `
                @('build', 'filtrace.slnx', '-c', 'Release') `
                $arm.Checkout `
                "$($arm.Name) build"
        }

        Test-TelemetryCapability $arm.Checkout $arm.Name
        [string] $subjectExecutable = Join-Path `
            $arm.Checkout `
            "src/Filtrace/bin/Release/net10.0/$(if ([OperatingSystem]::IsWindows()) { 'filtrace.exe' } else { 'filtrace' })"
        if (-not (Test-Path -LiteralPath $subjectExecutable -PathType Leaf)) {
            throw "$($arm.Name) filtrace executable was not found at '$subjectExecutable'."
        }

        [string] $bdnDirectory = Join-Path $armDirectory 'bdn'
        [System.Collections.Generic.List[string]] $bdnArguments = @(
            'run',
            '-c', 'Release',
            '--no-build',
            '--project', 'benchmarks/Filtrace.Benchmarks',
            '--',
            '--filter', $BenchmarkFilter,
            '--artifacts', $bdnDirectory,
            '--exporters', 'json', 'github')
        if ($BenchmarkJob -ne 'default') {
            $bdnArguments.Add('--job')
            $bdnArguments.Add($BenchmarkJob)
        }
        [string] $previousFiltracePath = [Environment]::GetEnvironmentVariable(
            $filtracePathEnvironmentVariable)
        [Environment]::SetEnvironmentVariable(
            $filtracePathEnvironmentVariable,
            $subjectExecutable)
        $commandLog.Add("[env] $filtracePathEnvironmentVariable=$subjectExecutable")
        try {
            Invoke-NativeChecked `
                $script:dotnet `
                $bdnArguments.ToArray() `
                $arm.Checkout `
                "$($arm.Name) benchmarks"
        }
        finally {
            [Environment]::SetEnvironmentVariable(
                $filtracePathEnvironmentVariable,
                $previousFiltracePath)
        }

        [string] $telemetryDirectory = Join-Path $armDirectory 'cli-benchmark'
        [System.IO.Directory]::CreateDirectory($telemetryDirectory) | Out-Null
        [string] $telemetryPath = Join-Path $telemetryDirectory 'cli-process.json'
        Invoke-NativeChecked `
            $script:dotnet `
            @(
                'run', '-c', 'Release', '--no-build',
                '--project', 'benchmarks/Filtrace.Benchmarks', '--',
                '--cli-telemetry',
                '--scenario', $CliScenario,
                '--trace', $trace,
                '--output', $telemetryPath,
                '--iterations', $TelemetryIterations.ToString([Globalization.CultureInfo]::InvariantCulture),
                '--filtrace', $subjectExecutable) `
            $arm.Checkout `
            "$($arm.Name) CLI telemetry"
    }

    [string] $baselineBdn = Get-ReportPath (Join-Path $runDirectory 'baseline/bdn/results')
    [string] $candidateBdn = Get-ReportPath (Join-Path $runDirectory 'candidate/bdn/results')
    [object[]] $benchmarkRows = @(Get-BenchmarkComparison $baselineBdn $candidateBdn)
    [object] $baselineTelemetry = Get-TelemetrySummary (Join-Path $runDirectory 'baseline/cli-benchmark/cli-process.json')
    [object] $candidateTelemetry = Get-TelemetrySummary (Join-Path $runDirectory 'candidate/cli-benchmark/cli-process.json')
    [object] $cliCpuDelta = if ($baselineTelemetry.averageCpuMilliseconds -eq 0.0) {
        if ($candidateTelemetry.averageCpuMilliseconds -eq 0.0) {
            [double]0.0
        }
        else {
            $null
        }
    }
    else {
        [double](($candidateTelemetry.averageCpuMilliseconds - $baselineTelemetry.averageCpuMilliseconds) `
            / $baselineTelemetry.averageCpuMilliseconds * 100.0
        )
    }
    [double] $cliCpuDeltaMilliseconds = `
        $candidateTelemetry.averageCpuMilliseconds - $baselineTelemetry.averageCpuMilliseconds

    [System.Collections.Specialized.OrderedDictionary] $comparison = [ordered]@{
        benchmarkRows = $benchmarkRows
        cliTelemetry = [ordered]@{
            baseline = $baselineTelemetry
            candidate = $candidateTelemetry
            averageLaunchToExitDeltaMilliseconds = `
                $candidateTelemetry.averageLaunchToExitMilliseconds `
                - $baselineTelemetry.averageLaunchToExitMilliseconds
            averageCpuDeltaPercent = $cliCpuDelta
            averageCpuDeltaMilliseconds = $cliCpuDeltaMilliseconds
            peakWorkingSetDeltaBytes = $candidateTelemetry.maxPeakWorkingSetBytes `
                - $baselineTelemetry.maxPeakWorkingSetBytes
            privateMemoryDeltaBytes = $candidateTelemetry.maxPrivateMemoryBytes `
                - $baselineTelemetry.maxPrivateMemoryBytes
        }
    }
    Write-JsonAtomic (Join-Path $runDirectory 'comparison.json') $comparison

    [string] $baselineBinary = Join-Path $baselinePath 'src/Filtrace/bin/Release/net10.0/filtrace.dll'
    [string] $candidateBinary = Join-Path $candidatePath 'src/Filtrace/bin/Release/net10.0/filtrace.dll'
    [System.Collections.Specialized.OrderedDictionary] $run = [ordered]@{
        schemaVersion = 1
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        harnessCommit = $HarnessCommit
        baselineCommit = $baselineHead
        candidateCommit = $candidateHead
        baselineDirty = $baselineStatus.Length -ne 0
        candidateDirty = $candidateStatus.Length -ne 0
        benchmarkTreeSha256 = $baselineHarnessHash
        inputCorpusSha256 = $archiveHash
        inputCorpusBytes = (Get-Item -LiteralPath $inputArchive).Length
        traceArchivePath = $TraceArchivePath.Replace('\\', '/', [StringComparison]::Ordinal)
        benchmarkFilter = $BenchmarkFilter
        benchmarkJob = $BenchmarkJob
        cliScenario = $CliScenario
        telemetryIterations = $TelemetryIterations
        sdkVersion = Invoke-NativeText $script:dotnet @('--version') $root 'read SDK version'
        os = [Environment]::OSVersion.VersionString
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        processorCount = [Environment]::ProcessorCount
        baselineBinarySha256 = if (Test-Path -LiteralPath $baselineBinary) {
            (Get-FileHash -LiteralPath $baselineBinary -Algorithm SHA256).Hash
        } else { $null }
        candidateBinarySha256 = if (Test-Path -LiteralPath $candidateBinary) {
            (Get-FileHash -LiteralPath $candidateBinary -Algorithm SHA256).Hash
        } else { $null }
        commandCount = $commandLog.Count
    }
    Write-JsonAtomic (Join-Path $runDirectory 'run.json') $run
    [System.IO.File]::WriteAllLines(
        (Join-Path $runDirectory 'commands.txt'),
        $commandLog,
        $utf8)
    [string] $ledger = @"
# Track D experiment ledger

| Hypothesis | One-variable change | Benchmark | CLI scenario | Allocation / memory | Target frame | Decision |
|---|---|---|---|---|---|---|
| Phase 0 reconstruction | none (baseline vs candidate) | $BenchmarkFilter | $CliScenario | comparison.json | n/a | pending review |
"@
    [System.IO.File]::WriteAllText((Join-Path $runDirectory 'ledger.md'), $ledger, $utf8)
    $runCompleted = $true

    Write-Host "Track D run: $runDirectory" -ForegroundColor Green
    Write-Host "Comparison: $(Join-Path $runDirectory 'comparison.json')" -ForegroundColor Green
}
catch {
    $runError = $_
    [System.IO.File]::WriteAllText(
        (Join-Path $runDirectory 'failure.txt'),
        "$($_.Exception.Message)$([Environment]::NewLine)",
        $utf8)
    [System.IO.File]::WriteAllLines(
        (Join-Path $runDirectory 'commands.txt'),
        $commandLog,
        $utf8)
    Write-JsonAtomic (Join-Path $runDirectory 'run-status.json') ([ordered]@{
        status = 'failed'
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        message = $_.Exception.Message
    })
    throw
}
finally {
    $cleanupError = $null
    if (-not $KeepWorktrees) {
        foreach ($worktree in @($createdWorktrees)) {
            try {
                Invoke-NativeChecked $script:git @('worktree', 'remove', '--force', $worktree) $root 'remove worktree'
            }
            catch {
                if ($null -eq $cleanupError) { $cleanupError = $_ }
                Write-Warning "Could not remove worktree '$worktree': $($_.Exception.Message)"
            }
        }
    }

    if ($null -ne $cleanupError -and $null -eq $runError) {
        [System.IO.File]::WriteAllText(
            (Join-Path $runDirectory 'failure.txt'),
            "$($cleanupError.Exception.Message)$([Environment]::NewLine)",
            $utf8)
        Write-JsonAtomic (Join-Path $runDirectory 'run-status.json') ([ordered]@{
            status = 'failed'
            updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            message = $cleanupError.Exception.Message
        })
        throw $cleanupError
    }

    if ($runCompleted -and $null -eq $runError) {
        Write-JsonAtomic (Join-Path $runDirectory 'run-status.json') ([ordered]@{
            status = 'completed'
            updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        })
    }
}
