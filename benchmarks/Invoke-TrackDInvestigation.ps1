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

.PARAMETER CaptureProfiles
    Capture one CPU and one allocation EventPipe trace per measured arm after both
    arms complete. Supported only for persistent single-trace warm CLI scenarios.

.PARAMETER AnalyzerPath
    Explicit frozen local filtrace executable used to analyze every profile capture.

.PARAMETER DotnetTracePath
    dotnet-trace executable path or command name. No tool is installed automatically.
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
    [string] $TestAdapterPath,
    [switch] $CaptureProfiles,
    [string] $AnalyzerPath,
    [string] $DotnetTracePath = 'dotnet-trace'
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
$profileScenarios = @(
    'info-warm',
    'rank-self-warm',
    'rank-inclusive-warm',
    'rank-activity-warm')
$maximumAnalyzerEntries = 512
$maximumAnalyzerFiles = 256
$maximumAnalyzerFileBytes = 128MB
$maximumAnalyzerDirectoryBytes = 512MB
$maximumProfileTraceBytes = 2GB
$profileQualityWarningCodes = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        'low_frame_resolution',
        'thin_scope',
        'ambiguous_selector',
        'truncated_output'),
    [StringComparer]::Ordinal)

. (Join-Path $root '.agents/skills/filtrace/scripts/Get-DotnetTraceRecorder.ps1')

if ($CaptureProfiles) {
    if ($CliScenario -notin $profileScenarios) {
        throw 'CaptureProfiles supports only persistent single-trace warm scenarios: ' +
            ($profileScenarios -join ', ') + '.'
    }

    if (-not $PSBoundParameters.ContainsKey('AnalyzerPath') -or
        [string]::IsNullOrWhiteSpace($AnalyzerPath)) {
        throw 'CaptureProfiles requires an explicit AnalyzerPath.'
    }
}

function Resolve-Executable([string] $Command, [string] $Purpose) {
    [string] $candidate = if (Test-Path -LiteralPath $Command -PathType Leaf) {
        $Command
    }
    else {
        [System.Management.Automation.CommandInfo[]] $resolved = @(
            Get-Command `
                $Command `
                -CommandType Application `
                -ErrorAction SilentlyContinue)
        if ($resolved.Count -eq 0) {
            throw "$Purpose was not found at '$Command' or on PATH."
        }
        $resolved[0].Source
    }

    [string] $canonical = Resolve-LocalFile $candidate $Purpose
    if (
        [OperatingSystem]::IsWindows() -and
        [System.IO.Path]::GetExtension($canonical) -ine '.exe'
    ) {
        throw "$Purpose must resolve to a native .exe on Windows because shell execution is disabled: '$canonical'."
    }

    return $canonical
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
    [string] $Purpose,
    [string] $RetainedOutputPath,
    [string] $RetainedErrorPath,
    [string] $BoundedArtifactPath,
    [long] $MaximumArtifactBytes) {
    $commandLog.Add("[$WorkingDirectory] $(Format-Command $Executable $Arguments)")
    if (
        [string]::IsNullOrEmpty($RetainedOutputPath) -ne
        [string]::IsNullOrEmpty($RetainedErrorPath)
    ) {
        throw 'Retained native output and error paths must be supplied together.'
    }
    [System.Collections.IDictionary] $boundedArtifacts = @{}
    if (-not [string]::IsNullOrEmpty($BoundedArtifactPath)) {
        if ($MaximumArtifactBytes -le 0) {
            throw 'A bounded native artifact requires a positive byte limit.'
        }
        $boundedArtifacts[[System.IO.Path]::GetFullPath($BoundedArtifactPath)] = $MaximumArtifactBytes
    }

    [bool] $removeOutput = [string]::IsNullOrEmpty($RetainedOutputPath)
    [string] $outputPath = if ($removeOutput) {
        Join-Path `
            ([System.IO.Path]::GetTempPath()) `
            "filtrace-trackd-output-$([Guid]::NewGuid().ToString('N')).tmp"
    }
    else {
        [System.IO.Path]::GetFullPath($RetainedOutputPath)
    }
    [string] $errorPath = if ($removeOutput) {
        Join-Path `
            ([System.IO.Path]::GetTempPath()) `
            "filtrace-trackd-error-$([Guid]::NewGuid().ToString('N')).tmp"
    }
    else {
        [System.IO.Path]::GetFullPath($RetainedErrorPath)
    }
    if (-not $removeOutput) {
        [System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::GetDirectoryName($outputPath)) | Out-Null
        [System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::GetDirectoryName($errorPath)) | Out-Null
    }

    [System.Diagnostics.Process] $process = $null
    [System.IO.FileStream] $outputStream = $null
    [System.IO.FileStream] $errorStream = $null
    [System.Threading.Tasks.Task] $standardOutput = $null
    [System.Threading.Tasks.Task] $standardError = $null
    try {
        $outputStream = [System.IO.File]::Create($outputPath)
        try {
            $errorStream = [System.IO.File]::Create($errorPath)
        }
        catch {
            $outputStream.Dispose()
            $outputStream = $null
            throw
        }

        try {
            $process = Start-NativeProcess `
                $Executable `
                $Arguments `
                $WorkingDirectory `
                $true
        }
        catch {
            throw "$Purpose could not start '$Executable': $($_.Exception.Message)"
        }
        $standardOutput = $process.StandardOutput.BaseStream.CopyToAsync($outputStream)
        $standardError = $process.StandardError.BaseStream.CopyToAsync($errorStream)
        Wait-NativeProcess $process $Purpose @($outputPath, $errorPath) $boundedArtifacts
        [System.Threading.Tasks.Task] $drain = [System.Threading.Tasks.Task]::WhenAll(
            [System.Threading.Tasks.Task[]]@($standardOutput, $standardError))
        $drain.WaitAsync(
            [TimeSpan]::FromMilliseconds($nativeCleanupTimeoutMilliseconds)).GetAwaiter().GetResult()
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
    catch {
        if ($null -ne $process) {
            [bool] $hasExited = $false
            try {
                $hasExited = $process.HasExited
            }
            catch { }
            if (-not $hasExited) {
                $null = Stop-NativeProcess $process
            }
        }
        throw
    }
    finally {
        if ($null -ne $standardOutput -and $null -ne $standardError) {
            try {
                [System.Threading.Tasks.Task] $cleanupDrain = [System.Threading.Tasks.Task]::WhenAll(
                    [System.Threading.Tasks.Task[]]@($standardOutput, $standardError))
                $cleanupDrain.WaitAsync(
                    [TimeSpan]::FromMilliseconds($nativeCleanupTimeoutMilliseconds)).GetAwaiter().GetResult()
            }
            catch {
                if ($null -ne $process) {
                    $null = Stop-NativeProcess $process
                }
            }
        }
        if ($null -ne $outputStream) { $outputStream.Dispose() }
        if ($null -ne $errorStream) { $errorStream.Dispose() }
        if ($null -ne $process) { $process.Dispose() }
        if ($removeOutput) {
            Remove-Item -LiteralPath $outputPath,$errorPath -Force -ErrorAction SilentlyContinue
        }
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
    [string[]] $BoundedOutputPaths = @(),
    [System.Collections.IDictionary] $BoundedArtifacts = @{}) {
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
        foreach ($entry in $BoundedArtifacts.GetEnumerator()) {
            if (
                (Test-Path -LiteralPath $entry.Key -PathType Leaf) -and
                (Get-Item -LiteralPath $entry.Key).Length -gt [long]$entry.Value
            ) {
                [string] $cleanup = Stop-NativeProcess $Process
                throw [System.IO.InvalidDataException]::new(
                    "$Purpose artifact '$($entry.Key)' exceeded $($entry.Value) bytes.$cleanup")
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
    foreach ($entry in $BoundedArtifacts.GetEnumerator()) {
        if (
            (Test-Path -LiteralPath $entry.Key -PathType Leaf) -and
            (Get-Item -LiteralPath $entry.Key).Length -gt [long]$entry.Value
        ) {
            throw [System.IO.InvalidDataException]::new(
                "$Purpose artifact '$($entry.Key)' exceeded $($entry.Value) bytes.")
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

function Resolve-LocalFile([string] $Path, [string] $Purpose) {
    if (
        [string]::IsNullOrWhiteSpace($Path) -or
        $Path.IndexOfAny([char[]]@([char]0, "`r", "`n")) -ge 0
    ) {
        throw "$Purpose must be a nonempty local file path without control characters."
    }

    [string] $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (
        $fullPath.StartsWith('\\', [StringComparison]::Ordinal) -or
        $fullPath.StartsWith('//', [StringComparison]::Ordinal)
    ) {
        throw "$Purpose must be local, not a UNC or network path: '$Path'."
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Purpose was not found at '$fullPath'."
    }

    return (Resolve-Path -LiteralPath $fullPath).Path
}

function Get-BoundedAnalyzerFileIdentity(
    [System.IO.FileInfo] $File,
    [long] $RemainingDirectoryBytes) {
    [System.IO.FileStream] $stream = [System.IO.File]::Open(
        $File.FullName,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    [System.Security.Cryptography.IncrementalHash] $hash =
        [System.Security.Cryptography.IncrementalHash]::CreateHash(
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        [byte[]] $buffer = [byte[]]::new(81920)
        [long] $bytesRead = 0
        while ($true) {
            [long] $remaining = [Math]::Min(
                $maximumAnalyzerFileBytes - $bytesRead,
                $RemainingDirectoryBytes - $bytesRead)
            [int] $requested = [int][Math]::Min($buffer.Length, $remaining + 1)
            [int] $read = $stream.Read($buffer, 0, $requested)
            if ($read -eq 0) {
                break
            }
            $bytesRead += $read
            if ($bytesRead -gt $maximumAnalyzerFileBytes) {
                throw "Analyzer file '$($File.FullName)' exceeds $maximumAnalyzerFileBytes bytes."
            }
            if ($bytesRead -gt $RemainingDirectoryBytes) {
                throw "Analyzer directory exceeds $maximumAnalyzerDirectoryBytes bytes."
            }
            $hash.AppendData($buffer, 0, $read)
        }

        return [pscustomobject]@{
            FullName = $File.FullName
            Bytes = $bytesRead
            Sha256 = [Convert]::ToHexString($hash.GetHashAndReset())
        }
    }
    finally {
        $hash.Dispose()
        $stream.Dispose()
    }
}

function Get-AnalyzerIdentity([string] $Executable) {
    [string] $canonicalExecutable = Resolve-LocalFile $Executable 'AnalyzerPath'
    [string] $directory = [System.IO.Path]::GetDirectoryName($canonicalExecutable)
    [string] $baseName = [System.IO.Path]::GetFileNameWithoutExtension($canonicalExecutable)
    [string] $managedAssembly = Join-Path $directory "$baseName.dll"
    [string] $depsFile = Join-Path $directory "$baseName.deps.json"
    [string] $runtimeConfig = Join-Path $directory "$baseName.runtimeconfig.json"
    foreach ($required in @($managedAssembly, $depsFile, $runtimeConfig)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "AnalyzerPath requires adjacent managed DLL, deps, and runtimeconfig files; missing '$required'."
        }
    }

    [System.IO.DirectoryInfo] $rootDirectory = [System.IO.DirectoryInfo]::new($directory)
    if (($rootDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Analyzer directory is a reparse point: '$directory'."
    }
    [System.IO.FileInfo] $executableFile = [System.IO.FileInfo]::new($canonicalExecutable)
    if (($executableFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Analyzer executable is a reparse point: '$canonicalExecutable'."
    }

    [System.Collections.Generic.Stack[System.IO.DirectoryInfo]] $pendingDirectories = @()
    [System.Collections.Generic.List[System.IO.FileInfo]] $files = @()
    $pendingDirectories.Push($rootDirectory)
    [int] $entryCount = 0
    while ($pendingDirectories.Count -ne 0) {
        [System.IO.DirectoryInfo] $currentDirectory = $pendingDirectories.Pop()
        foreach ($entry in $currentDirectory.EnumerateFileSystemInfos()) {
            $entryCount++
            if ($entryCount -gt $maximumAnalyzerEntries) {
                throw "Analyzer directory '$directory' exceeds $maximumAnalyzerEntries entries including directories."
            }
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Analyzer directory contains reparse point '$($entry.FullName)'."
            }
            [string] $relative = [System.IO.Path]::GetRelativePath($directory, $entry.FullName)
            if (
                [System.IO.Path]::IsPathRooted($relative) -or
                $relative -ceq '..' -or
                $relative.StartsWith(
                    "..$([System.IO.Path]::DirectorySeparatorChar)",
                    [StringComparison]::Ordinal)
            ) {
                throw "Analyzer entry is outside its directory: '$($entry.FullName)'."
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                $pendingDirectories.Push($entry)
            }
            elseif ($entry -is [System.IO.FileInfo]) {
                if ($files.Count -ge $maximumAnalyzerFiles) {
                    throw "Analyzer directory '$directory' exceeds $maximumAnalyzerFiles files."
                }
                $files.Add($entry)
            }
            else {
                throw "Analyzer directory contains unsupported entry '$($entry.FullName)'."
            }
        }
    }
    if ($files.Count -eq 0) {
        throw "Analyzer directory '$directory' contains no files."
    }
    $files.Sort([System.Comparison[System.IO.FileInfo]]{
        param($left, $right)
        return [StringComparer]::Ordinal.Compare($left.FullName, $right.FullName)
    })

    [long] $totalBytes = 0
    [System.Collections.Generic.List[object]] $inventory = @()
    [System.Collections.Generic.Dictionary[string, object]] $identities =
        [System.Collections.Generic.Dictionary[string, object]]::new(
            $(if ([OperatingSystem]::IsWindows() -or [OperatingSystem]::IsMacOS()) {
                [StringComparer]::OrdinalIgnoreCase
            }
            else {
                [StringComparer]::Ordinal
            }))
    foreach ($file in $files) {
        [object] $identity = Get-BoundedAnalyzerFileIdentity `
            $file `
            ($maximumAnalyzerDirectoryBytes - $totalBytes)
        $totalBytes += $identity.Bytes
        [string] $relative = [System.IO.Path]::GetRelativePath($directory, $file.FullName)
        $relative = $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
        [System.Collections.IDictionary] $record = [ordered]@{
            path = $relative
            bytes = $identity.Bytes
            sha256 = $identity.Sha256
        }
        $inventory.Add($record)
        $identities.Add($identity.FullName, $record)
    }

    [string] $executableRelativePath = [System.IO.Path]::GetRelativePath(
        $directory,
        $canonicalExecutable).Replace([System.IO.Path]::DirectorySeparatorChar, '/')
    [string] $fingerprint = ConvertTo-Json -InputObject @($inventory) -Depth 5 -Compress
    return [pscustomobject]@{
        CanonicalExecutablePath = $canonicalExecutable
        Directory = $directory
        ExecutableRelativePath = $executableRelativePath
        Fingerprint = $fingerprint
        Record = [ordered]@{
            canonicalExecutablePath = $canonicalExecutable
            executableRelativePath = $executableRelativePath
            executableSha256 = $identities[$canonicalExecutable].sha256
            managedAssemblySha256 = $identities[$managedAssembly].sha256
            depsSha256 = $identities[$depsFile].sha256
            runtimeConfigSha256 = $identities[$runtimeConfig].sha256
            totalBytes = $totalBytes
            files = @($inventory)
        }
    }
}

function Assert-AnalyzerIdentity(
    [object] $Expected,
    [object] $Actual,
    [string] $Phase,
    [bool] $RequireSamePath) {
    [StringComparison] $pathComparison = if (
        [OperatingSystem]::IsWindows() -or [OperatingSystem]::IsMacOS()
    ) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    if (
        ($RequireSamePath -and -not [string]::Equals(
            $Expected.CanonicalExecutablePath,
            $Actual.CanonicalExecutablePath,
            $pathComparison)) -or
        $Expected.Fingerprint -cne $Actual.Fingerprint
    ) {
        throw "Analyzer identity changed $Phase."
    }
}

function Copy-AnalyzerSnapshot([object] $Identity, [string] $Destination) {
    if (Test-Path -LiteralPath $Destination) {
        throw "Analyzer snapshot destination already exists: '$Destination'."
    }
    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in $Identity.Record.files) {
        [string] $source = Join-Path $Identity.Directory ([string]$file.path)
        [string] $target = Join-Path $Destination ([string]$file.path)
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }

    [string] $snapshotExecutable = Join-Path $Destination $Identity.ExecutableRelativePath
    [object] $snapshotIdentity = Get-AnalyzerIdentity $snapshotExecutable
    Assert-AnalyzerIdentity $Identity $snapshotIdentity 'while creating the owned snapshot' $false
    return $snapshotIdentity
}

function Get-ProfileReplay(
    [string] $TelemetryPath,
    [string] $ExpectedScenario,
    [int] $ExpectedIterations,
    [string] $ExpectedTrace,
    [string] $ExpectedExecutable,
    [string] $WorkingDirectory) {
    [object] $report = Get-Content -LiteralPath $TelemetryPath -Raw | ConvertFrom-Json -Depth 20
    if ([int]$report.schemaVersion -ne 2 -or [string]$report.scenario -cne $ExpectedScenario) {
        throw "Telemetry report '$TelemetryPath' does not match the requested profile scenario."
    }
    [object[]] $launches = @($report.launches)
    if ($launches.Count -ne $ExpectedIterations) {
        throw "Telemetry report '$TelemetryPath' does not contain $ExpectedIterations launches."
    }

    [string] $executable = Resolve-LocalFile ([string]$report.executable) 'Telemetry executable'
    [StringComparison] $pathComparison = if (
        [OperatingSystem]::IsWindows() -or [OperatingSystem]::IsMacOS()
    ) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    if (-not [string]::Equals(
        $executable,
        (Resolve-LocalFile $ExpectedExecutable 'Measured filtrace executable'),
        $pathComparison)) {
        throw "Telemetry report '$TelemetryPath' names an unexpected executable."
    }

    [string[]] $arguments = @($launches[0].arguments | ForEach-Object { [string]$_ })
    if (
        $arguments.Count -lt 4 -or
        $arguments.Count -gt 12 -or
        $arguments[0] -notin @('info', 'rank') -or
        $arguments[-2] -cne '--format' -or
        $arguments[-1] -cne 'json'
    ) {
        throw "Telemetry report '$TelemetryPath' has an unsupported persistent single-trace argv shape."
    }
    [string] $trace = Resolve-LocalFile $arguments[1] 'Telemetry trace argument'
    if (-not [string]::Equals($trace, (Resolve-Path -LiteralPath $ExpectedTrace).Path, $pathComparison)) {
        throw "Telemetry report '$TelemetryPath' does not replay its restored input trace."
    }

    [string] $argumentFingerprint = ConvertTo-Json -InputObject $arguments -Compress
    foreach ($launch in $launches) {
        [string[]] $launchArguments = @($launch.arguments | ForEach-Object { [string]$_ })
        if (
            [int]$launch.exitCode -ne 0 -or
            (ConvertTo-Json -InputObject $launchArguments -Compress) -cne $argumentFingerprint
        ) {
            throw "Telemetry report '$TelemetryPath' does not contain identical successful launch argv."
        }
    }

    return [pscustomobject]@{
        Executable = $executable
        Arguments = $arguments
        WorkingDirectory = $WorkingDirectory
        Trace = $trace
        ArgumentShape = @($arguments[0], '<trace>') + @($arguments[2..($arguments.Count - 1)])
    }
}

function Write-ProfileCaptureMetadata(
    [string] $TracePath,
    [string] $Metric,
    [object] $Recorder) {
    [System.Collections.IDictionary] $analyses = if ($Metric -ceq 'cpu') {
        [ordered]@{ cpu = 'enabled'; events = 'enabled' }
    }
    else {
        [ordered]@{ alloc = 'enabled'; gcstats = 'enabled'; events = 'enabled' }
    }
    Write-JsonAtomic "$TracePath.filtrace.json" ([ordered]@{
        schemaVersion = 1
        analyses = $analyses
        recorder = $Recorder.Metadata
    })
}

function Test-FiniteJsonNumber([object] $Value) {
    return $null -ne $Value -and
        $Value -is [ValueType] -and
        $Value -isnot [bool] -and
        [double]::IsFinite([double]$Value)
}

function Get-ValidatedProfileWarnings([object] $Envelope, [string] $Owner) {
    [object] $warningsProperty = $Envelope.PSObject.Properties['warnings']
    if ($null -eq $warningsProperty -or $null -eq $warningsProperty.Value) {
        throw "$Owner omitted warnings."
    }

    [System.Collections.Generic.List[object]] $warnings = @()
    [bool] $qualityLimited = $false
    foreach ($warning in @($warningsProperty.Value)) {
        if ($null -eq $warning) {
            throw "$Owner returned a malformed warning."
        }
        [object] $codeProperty = $warning.PSObject.Properties['code']
        [object] $severityProperty = $warning.PSObject.Properties['severity']
        [object] $messageProperty = $warning.PSObject.Properties['message']
        if (
            $null -eq $codeProperty -or
            [string]::IsNullOrWhiteSpace([string]$codeProperty.Value) -or
            $null -eq $severityProperty -or
            [string]::IsNullOrWhiteSpace([string]$severityProperty.Value) -or
            $null -eq $messageProperty -or
            [string]::IsNullOrWhiteSpace([string]$messageProperty.Value)
        ) {
            throw "$Owner returned a malformed warning."
        }
        $warnings.Add($warning)
        $qualityLimited = $qualityLimited -or
            $profileQualityWarningCodes.Contains([string]$codeProperty.Value)
    }

    return [pscustomobject]@{
        Warnings = $warnings
        QualityLimited = $qualityLimited
    }
}

function Get-ValidatedProfileResult(
    [string] $AnalysisDirectory,
    [object] $Query,
    [string] $AnalysisName,
    [ValidateSet('rank', 'gc')][string] $ExpectedSummaryKind) {
    if ([string]$Query.status -cne 'completed') {
        throw "Profile analysis '$AnalysisName' query '$($Query.id)' did not complete."
    }

    [string] $resultPath = Join-Path $AnalysisDirectory ([string]$Query.stdout)
    [object] $envelope = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -Depth 32
    [object] $schemaProperty = $envelope.PSObject.Properties['schemaVersion']
    if (
        $null -eq $schemaProperty -or
        -not (Test-FiniteJsonNumber $schemaProperty.Value) -or
        [double]$schemaProperty.Value -ne 16
    ) {
        throw "Profile analysis '$AnalysisName' query '$($Query.id)' did not return schema 16."
    }

    [object] $validatedWarnings = Get-ValidatedProfileWarnings `
        $envelope `
        "Profile analysis '$AnalysisName' query '$($Query.id)'"
    [System.Collections.Generic.List[object]] $warnings = $validatedWarnings.Warnings
    [bool] $qualityLimited = $validatedWarnings.QualityLimited

    [object] $contextProperty = $envelope.PSObject.Properties['context']
    [object] $resultProperty = $envelope.PSObject.Properties['result']
    if (
        $null -eq $contextProperty -or
        $null -eq $contextProperty.Value -or
        $null -eq $resultProperty -or
        $null -eq $resultProperty.Value
    ) {
        throw "Profile analysis '$AnalysisName' query '$($Query.id)' omitted context or result."
    }
    [object] $context = $contextProperty.Value
    [object] $result = $resultProperty.Value

    if ($ExpectedSummaryKind -ceq 'rank') {
        [string] $expectedMetric = $AnalysisName
        [string] $expectedUnit = if ($AnalysisName -ceq 'cpu') { 'ms' } else { 'bytes' }
        [object] $operationProperty = $context.PSObject.Properties['operation']
        [object] $metricProperty = $context.PSObject.Properties['metric']
        [object] $unitProperty = $context.PSObject.Properties['unit']
        if (
            $null -eq $operationProperty -or
            [string]$operationProperty.Value -cne 'rank' -or
            $null -eq $metricProperty -or
            [string]$metricProperty.Value -cne $expectedMetric -or
            $null -eq $unitProperty -or
            [string]$unitProperty.Value -cne $expectedUnit
        ) {
            throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned the wrong rank context."
        }

        [object] $rowsProperty = $result.PSObject.Properties['rows']
        [System.Collections.Generic.List[object]] $rows = @()
        if ($null -ne $rowsProperty -and $null -ne $rowsProperty.Value) {
            foreach ($row in @($rowsProperty.Value)) {
                $rows.Add($row)
            }
        }
        if ($rows.Count -eq 0) {
            throw "Profile capture contained no $AnalysisName rank rows."
        }
        [object] $scopeWeightProperty = $result.PSObject.Properties['scopeWeight']
        [object] $contributingRecordsProperty = $result.PSObject.Properties['contributingRecordCount']
        if (
            $null -eq $scopeWeightProperty -or
            -not (Test-FiniteJsonNumber $scopeWeightProperty.Value) -or
            [double]$scopeWeightProperty.Value -le 0 -or
            $null -eq $contributingRecordsProperty -or
            -not (Test-FiniteJsonNumber $contributingRecordsProperty.Value) -or
            [double]$contributingRecordsProperty.Value -le 0 -or
            [double]$contributingRecordsProperty.Value -ne
                [Math]::Truncate([double]$contributingRecordsProperty.Value)
        ) {
            throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned invalid rank scope totals."
        }
        foreach ($row in $rows) {
            [object] $frameProperty = $row.PSObject.Properties['frame']
            [object] $weightProperty = $row.PSObject.Properties['weight']
            [object] $percentProperty = $row.PSObject.Properties['percentOfScope']
            if (
                $null -eq $row -or
                $null -eq $frameProperty -or
                [string]::IsNullOrWhiteSpace([string]$frameProperty.Value) -or
                $null -eq $weightProperty -or
                -not (Test-FiniteJsonNumber $weightProperty.Value) -or
                [double]$weightProperty.Value -le 0 -or
                $null -eq $percentProperty -or
                -not (Test-FiniteJsonNumber $percentProperty.Value) -or
                [double]$percentProperty.Value -lt 0 -or
                [double]$percentProperty.Value -gt 100
            ) {
                throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned a malformed rank row."
            }
        }

        return [ordered]@{
            queryId = [string]$Query.id
            status = if ($qualityLimited) { 'insufficientQuality' } else { 'observed' }
            scopeWeight = [double]$scopeWeightProperty.Value
            contributingRecordCount = [long]$contributingRecordsProperty.Value
            rowCount = $rows.Count
            warnings = @($warnings)
        }
    }

    [object] $gcOperationProperty = $context.PSObject.Properties['operation']
    if ($null -eq $gcOperationProperty -or [string]$gcOperationProperty.Value -cne 'gc') {
        throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned the wrong GC context."
    }
    foreach ($propertyName in @('gcCount', 'gen0Count', 'gen1Count', 'gen2Count', 'inducedCount')) {
        [object] $property = $result.PSObject.Properties[$propertyName]
        if (
            $null -eq $property -or
            -not (Test-FiniteJsonNumber $property.Value) -or
            [double]$property.Value -lt 0 -or
            [double]$property.Value -ne [Math]::Truncate([double]$property.Value)
        ) {
            throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned invalid GC property '$propertyName'."
        }
    }
    foreach ($propertyName in @(
        'totalPauseMs', 'maxPauseMs', 'meanPauseMs', 'percentTimeInGc',
        'peakHeapSizeMB', 'totalPromotedMB')) {
        [object] $property = $result.PSObject.Properties[$propertyName]
        if (
            $null -eq $property -or
            -not (Test-FiniteJsonNumber $property.Value) -or
            [double]$property.Value -lt 0
        ) {
            throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned invalid GC property '$propertyName'."
        }
    }
    [object] $gcsProperty = $result.PSObject.Properties['gcs']
    if ($null -eq $gcsProperty -or $null -eq $gcsProperty.Value) {
        throw "Profile analysis '$AnalysisName' query '$($Query.id)' omitted GC records."
    }
    [System.Collections.Generic.List[object]] $gcs = @()
    foreach ($gc in @($gcsProperty.Value)) {
        $gcs.Add($gc)
    }
    [long] $gcCount = [long]$result.gcCount
    if ($gcCount -eq 0 -and $gcs.Count -ne 0) {
        throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned GC records with a zero count."
    }
    if ($gcCount -gt 0 -and $gcs.Count -eq 0) {
        throw "Profile analysis '$AnalysisName' query '$($Query.id)' omitted observed GC records."
    }
    if ($gcs.Count -gt $gcCount) {
        throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned more GC records than its count."
    }
    foreach ($gc in $gcs) {
        [object] $numberProperty = $gc.PSObject.Properties['number']
        [object] $generationProperty = $gc.PSObject.Properties['generation']
        [object] $kindProperty = $gc.PSObject.Properties['kind']
        [object] $reasonProperty = $gc.PSObject.Properties['reason']
        if (
            $null -eq $numberProperty -or
            -not (Test-FiniteJsonNumber $numberProperty.Value) -or
            [double]$numberProperty.Value -lt 0 -or
            [double]$numberProperty.Value -ne [Math]::Truncate([double]$numberProperty.Value) -or
            $null -eq $generationProperty -or
            -not (Test-FiniteJsonNumber $generationProperty.Value) -or
            [double]$generationProperty.Value -lt 0 -or
            [double]$generationProperty.Value -gt 2 -or
            [double]$generationProperty.Value -ne [Math]::Truncate([double]$generationProperty.Value) -or
            $null -eq $kindProperty -or
            [string]::IsNullOrWhiteSpace([string]$kindProperty.Value) -or
            $null -eq $reasonProperty -or
            [string]::IsNullOrWhiteSpace([string]$reasonProperty.Value)
        ) {
            throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned a malformed GC record."
        }
        foreach ($propertyName in @('pauseMs', 'heapSizeAfterMB', 'promotedMB')) {
            [object] $property = $gc.PSObject.Properties[$propertyName]
            if (
                $null -eq $property -or
                -not (Test-FiniteJsonNumber $property.Value) -or
                [double]$property.Value -lt 0
            ) {
                throw "Profile analysis '$AnalysisName' query '$($Query.id)' returned a malformed GC record."
            }
        }
    }

    return [ordered]@{
        queryId = [string]$Query.id
        status = if ($gcCount -eq 0) { 'empty' } elseif ($qualityLimited) { 'insufficientQuality' } else { 'observed' }
        gcCount = $gcCount
        recordCount = $gcs.Count
        warnings = @($warnings)
    }
}

function Get-AnalysisEvidence(
    [string] $AnalysisDirectory,
    [string] $AnalysisName,
    [bool] $RequireEvents) {
    [string] $runPath = Join-Path $AnalysisDirectory 'run.json'
    [object] $runRecord = Get-Content -LiteralPath $runPath -Raw | ConvertFrom-Json -Depth 32
    if ([string]$runRecord.status -cne 'completed') {
        throw "Profile analysis '$AnalysisName' did not complete."
    }

    [object] $infoQuery = @($runRecord.queries | Where-Object { $_.operation -ceq 'info' })[0]
    if ($null -eq $infoQuery) {
        throw "Profile analysis '$AnalysisName' omitted its info query."
    }
    [object] $info = Get-Content `
        -LiteralPath (Join-Path $AnalysisDirectory ([string]$infoQuery.stdout)) `
        -Raw | ConvertFrom-Json -Depth 32
    if ([int]$info.schemaVersion -ne 16) {
        throw "Profile analysis '$AnalysisName' did not return info schema 16."
    }
    [object] $validatedInfoWarnings = Get-ValidatedProfileWarnings `
        $info `
        "Profile analysis '$AnalysisName' info"
    [object] $analysesProperty = $info.PSObject.Properties['analyses']
    [object] $analysisProperty = if ($null -eq $analysesProperty) {
        $null
    }
    else {
        $analysesProperty.Value.PSObject.Properties[$AnalysisName]
    }
    if ($null -eq $analysisProperty) {
        throw "Profile info omitted analysis '$AnalysisName'."
    }
    [object] $analysis = $analysisProperty.Value
    if ([string]$analysis.captureStatus -cne 'enabled') {
        throw "Profile analysis '$AnalysisName' is unavailable with capture status '$($analysis.captureStatus)'."
    }
    [object] $eventProperty = $analysis.PSObject.Properties['eventCount']
    if (
        $null -eq $eventProperty -or
        $null -eq $eventProperty.Value -or
        $eventProperty.Value -isnot [ValueType] -or
        [long]$eventProperty.Value -lt 0
    ) {
        throw "Profile analysis '$AnalysisName' did not report a valid event count."
    }

    [long] $eventCount = [long]$eventProperty.Value
    if ($RequireEvents -and $eventCount -eq 0) {
        throw "Profile capture contained no $AnalysisName events."
    }

    [System.Collections.Generic.List[object]] $summaries = @()
    [string] $summaryKind = if ($AnalysisName -ceq 'gcstats') { 'gc' } else { 'rank' }
    [string] $summaryOperation = if ($summaryKind -ceq 'gc') { 'report' } else { 'rank' }
    [object[]] $summaryQueries = @(
        $runRecord.queries | Where-Object { $_.operation -ceq $summaryOperation })
    if ($summaryQueries.Count -eq 0) {
        throw "Profile analysis '$AnalysisName' omitted its $summaryKind result."
    }
    foreach ($query in $summaryQueries) {
        $summaries.Add((Get-ValidatedProfileResult `
            $AnalysisDirectory `
            $query `
            $AnalysisName `
            $summaryKind))
    }
    [bool] $qualityLimited = $validatedInfoWarnings.QualityLimited -or @(
        $summaries | Where-Object { $_.status -ceq 'insufficientQuality' }).Count -ne 0
    return [ordered]@{
        status = if ($eventCount -eq 0) {
            'empty'
        }
        elseif ($qualityLimited) {
            'insufficientQuality'
        }
        else {
            'observed'
        }
        eventCount = $eventCount
        warnings = @($validatedInfoWarnings.Warnings)
        summaries = @($summaries)
        runPath = $runPath
        runSha256 = (Get-FileHash -LiteralPath $runPath -Algorithm SHA256).Hash
    }
}

function Invoke-ProfileAnalysis(
    [string] $TracePath,
    [string] $CaptureDirectory,
    [string] $RecordName,
    [string] $AnalysisName,
    [object[]] $Queries,
    [bool] $RequireEvents,
    [string] $AnalyzerExecutable,
    [System.Collections.Generic.List[object]] $AnalysisRecords) {
    [string] $planPath = Join-Path $CaptureDirectory "$RecordName-plan.json"
    [string] $analysisDirectory = Join-Path $CaptureDirectory "$RecordName-analysis"
    [string] $stdoutPath = Join-Path $CaptureDirectory "$RecordName.stdout.txt"
    [string] $stderrPath = Join-Path $CaptureDirectory "$RecordName.stderr.txt"
    Write-JsonAtomic $planPath ([ordered]@{
        schemaVersion = 1
        inputs = @([ordered]@{ id = 'capture'; kind = 'trace'; path = $TracePath })
        queries = $Queries
    })
    [System.Collections.IDictionary] $record = [ordered]@{
        name = $AnalysisName
        status = 'running'
        planPath = $planPath
        outputDirectory = $analysisDirectory
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
    }
    $AnalysisRecords.Add($record)
    try {
        $null = Invoke-NativeText `
            $script:powershellHost `
            @(
                '-NoProfile',
                '-File', (Join-Path $root '.agents/skills/filtrace/scripts/Invoke-FiltraceAnalysis.ps1'),
                '-Plan', $planPath,
                '-OutputDirectory', $analysisDirectory,
                '-FiltracePath', $AnalyzerExecutable,
                '-TimeoutSeconds', $NativeTimeoutSeconds.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)) `
            $root `
            "$AnalysisName profile analysis" `
            $stdoutPath `
            $stderrPath
        [System.Collections.IDictionary] $evidence = Get-AnalysisEvidence `
            $analysisDirectory `
            $AnalysisName `
            $RequireEvents
        $record.status = 'completed'
        $record['evidence'] = $evidence
    }
    catch {
        $record.status = 'failed'
        $record['failure'] = $_.Exception.Message
        throw "Profile analysis '$AnalysisName' did not complete: $($_.Exception.Message)"
    }
}

function Invoke-TrackDProfiles(
    [string] $RunDirectory,
    [object[]] $ArmInputs,
    [object] $InitialAnalyzerIdentity,
    [object] $CpuRecorder,
    [object] $AllocationRecorder,
    [string] $DotnetTraceExecutable,
    [System.Collections.IDictionary] $ProfileRecord) {
    $ProfileRecord.status = 'running'
    $ProfileRecord['startedUtc'] = [DateTimeOffset]::UtcNow.ToString('O')
    [string] $recordPath = Join-Path $RunDirectory 'profiles.json'
    try {
        [System.Collections.Generic.List[object]] $replays = @()
        foreach ($arm in $ArmInputs) {
            [string] $telemetryPath = Join-Path $arm.Directory 'cli-benchmark/cli-process.json'
            [object] $replay = Get-ProfileReplay `
                $telemetryPath `
                $CliScenario `
                $TelemetryIterations `
                $arm.Trace `
                $arm.SubjectExecutable `
                $arm.Checkout
            $replays.Add([pscustomobject]@{
                Name = $arm.Name
                Directory = $arm.Directory
                Checkout = $arm.Checkout
                Replay = $replay
                TelemetryPath = $telemetryPath
            })
        }
        if (
            $replays.Count -ne 2 -or
            (ConvertTo-Json -InputObject $replays[0].Replay.ArgumentShape -Compress) -cne
                (ConvertTo-Json -InputObject $replays[1].Replay.ArgumentShape -Compress)
        ) {
            throw 'Baseline and candidate telemetry reports do not contain the same CLI argv shape.'
        }

        [object] $preSnapshotIdentity = Get-AnalyzerIdentity $InitialAnalyzerIdentity.CanonicalExecutablePath
        Assert-AnalyzerIdentity $InitialAnalyzerIdentity $preSnapshotIdentity 'before profiling' $true
        [string] $artifactRoot = Join-Path $RunDirectory 'profile-artifacts'
        [string] $snapshotDirectory = Join-Path $artifactRoot 'analyzer'
        [object] $snapshotIdentity = Copy-AnalyzerSnapshot $preSnapshotIdentity $snapshotDirectory
        $ProfileRecord['analyzer'] = [ordered]@{
            source = $InitialAnalyzerIdentity.Record
            snapshot = $snapshotIdentity.Record
        }
        Write-JsonAtomic (Join-Path $artifactRoot 'analyzer-identity.json') $ProfileRecord.analyzer

        [System.Collections.Generic.List[object]] $armRecords = @()
        $ProfileRecord['arms'] = $armRecords
        foreach ($replayRecord in $replays) {
            [System.Collections.Generic.List[object]] $captures = @()
            [System.Collections.IDictionary] $armRecord = [ordered]@{
                name = $replayRecord.Name
                telemetryPath = $replayRecord.TelemetryPath
                subject = [ordered]@{
                    executable = $replayRecord.Replay.Executable
                    arguments = @($replayRecord.Replay.Arguments)
                    workingDirectory = $replayRecord.Replay.WorkingDirectory
                    trace = $replayRecord.Replay.Trace
                }
                captures = $captures
            }
            $armRecords.Add($armRecord)
            foreach ($definition in @(
                [pscustomobject]@{
                    Name = 'cpu'
                    Recorder = $CpuRecorder
                },
                [pscustomobject]@{
                    Name = 'allocation'
                    Recorder = $AllocationRecorder
                })) {
                [string] $captureDirectory = Join-Path `
                    $replayRecord.Directory `
                    "profiles/$($definition.Name)"
                [System.IO.Directory]::CreateDirectory($captureDirectory) | Out-Null
                [string] $tracePath = Join-Path $captureDirectory 'capture.nettrace'
                [string] $stdoutPath = Join-Path $captureDirectory 'collect.stdout.txt'
                [string] $stderrPath = Join-Path $captureDirectory 'collect.stderr.txt'
                [string[]] $collectArguments = @(
                    'collect',
                    '--output', $tracePath,
                    '--profile', $definition.Recorder.ProfileArgument,
                    '--',
                    $replayRecord.Replay.Executable) + @($replayRecord.Replay.Arguments)
                [System.Collections.Generic.List[object]] $analysisRecords = @()
                [System.Collections.IDictionary] $captureRecord = [ordered]@{
                    metric = $definition.Name
                    status = 'running'
                    tracePath = $tracePath
                    traceSha256 = $null
                    recorder = $definition.Recorder.Metadata
                    command = [ordered]@{
                        executable = $DotnetTraceExecutable
                        arguments = $collectArguments
                        workingDirectory = $replayRecord.Checkout
                    }
                    stdoutPath = $stdoutPath
                    stderrPath = $stderrPath
                    analyses = $analysisRecords
                }
                $captures.Add($captureRecord)
                try {
                    $null = Invoke-NativeText `
                        $DotnetTraceExecutable `
                        $collectArguments `
                        $replayRecord.Checkout `
                        "$($replayRecord.Name) $($definition.Name) profile capture" `
                        $stdoutPath `
                        $stderrPath `
                        $tracePath `
                        $maximumProfileTraceBytes
                    if (-not (Test-Path -LiteralPath $tracePath -PathType Leaf)) {
                        throw "Profile recorder did not create '$tracePath'."
                    }
                    if ((Get-Item -LiteralPath $tracePath).Length -eq 0) {
                        throw "Profile recorder created an empty trace at '$tracePath'."
                    }
                    $captureRecord.traceSha256 = (
                        Get-FileHash -LiteralPath $tracePath -Algorithm SHA256).Hash
                    Write-ProfileCaptureMetadata `
                        $tracePath `
                        $(if ($definition.Name -ceq 'cpu') { 'cpu' } else { 'allocation' }) `
                        $definition.Recorder

                    if ($definition.Name -ceq 'cpu') {
                        Invoke-ProfileAnalysis `
                            $tracePath `
                            $captureDirectory `
                            'cpu' `
                            'cpu' `
                            @(
                                [ordered]@{
                                    id = 'orientation'
                                    operation = 'info'
                                    inputIds = @('capture')
                                    arguments = @('--strict', '--require-enabled', 'cpu', '--require-events', 'cpu')
                                },
                                [ordered]@{
                                    id = 'rank-self'
                                    operation = 'rank'
                                    inputIds = @('capture')
                                    arguments = @('--metric', 'cpu', '--top', '25')
                                },
                                [ordered]@{
                                    id = 'rank-inclusive'
                                    operation = 'rank'
                                    inputIds = @('capture')
                                    arguments = @('--metric', 'cpu', '--measure', 'inclusive', '--top', '25')
                                }) `
                            $true `
                            $snapshotIdentity.CanonicalExecutablePath `
                            $analysisRecords
                    }
                    else {
                        Invoke-ProfileAnalysis `
                            $tracePath `
                            $captureDirectory `
                            'allocation' `
                            'alloc' `
                            @(
                                [ordered]@{
                                    id = 'orientation'
                                    operation = 'info'
                                    inputIds = @('capture')
                                    arguments = @('--require-enabled', 'alloc', '--require-events', 'alloc')
                                },
                                [ordered]@{
                                    id = 'rank'
                                    operation = 'rank'
                                    inputIds = @('capture')
                                    arguments = @('--metric', 'alloc', '--top', '25')
                                }) `
                            $true `
                            $snapshotIdentity.CanonicalExecutablePath `
                            $analysisRecords
                        Invoke-ProfileAnalysis `
                            $tracePath `
                            $captureDirectory `
                            'gc' `
                            'gcstats' `
                            @(
                                [ordered]@{
                                    id = 'orientation'
                                    operation = 'info'
                                    inputIds = @('capture')
                                    arguments = @('--require-enabled', 'gcstats')
                                },
                                [ordered]@{
                                    id = 'report'
                                    operation = 'report'
                                    inputIds = @('capture')
                                    arguments = @('--kind', 'gc')
                                }) `
                            $false `
                            $snapshotIdentity.CanonicalExecutablePath `
                            $analysisRecords
                    }
                    $captureRecord.status = 'completed'
                }
                catch {
                    $captureRecord.status = 'failed'
                    $captureRecord['failure'] = $_.Exception.Message
                    throw
                }
            }
        }

        [object] $postProfileIdentity = Get-AnalyzerIdentity $InitialAnalyzerIdentity.CanonicalExecutablePath
        Assert-AnalyzerIdentity $InitialAnalyzerIdentity $postProfileIdentity 'after profiling' $true
        [object] $postSnapshotIdentity = Get-AnalyzerIdentity $snapshotIdentity.CanonicalExecutablePath
        Assert-AnalyzerIdentity $snapshotIdentity $postSnapshotIdentity 'inside the owned snapshot' $true
        $ProfileRecord.status = 'completed'
    }
    catch {
        $ProfileRecord.status = 'failed'
        $ProfileRecord['failure'] = $_.Exception.Message
        throw
    }
    finally {
        $ProfileRecord['completedUtc'] = [DateTimeOffset]::UtcNow.ToString('O')
        Write-JsonAtomic $recordPath $ProfileRecord
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
$profileRecord = $null
$initialAnalyzerIdentity = $null
$cpuRecorder = $null
$allocationRecorder = $null
$resolvedDotnetTrace = $null
$profileArmInputs = [System.Collections.Generic.List[object]]::new()
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

    if ($CaptureProfiles) {
        [System.Collections.Generic.List[object]] $profilePreflight = @()
        $profileRecord = [ordered]@{
            schemaVersion = 1
            status = 'preflight'
            metricSemantics = [ordered]@{
                cpu = 'sampled-cpu-stacks'
                allocation = 'sampled-allocation-ticks'
                gc = 'runtime-gc-events'
            }
            preflight = $profilePreflight
        }
        [string] $profileRecordPath = Join-Path $runDirectory 'profiles.json'
        Write-JsonAtomic $profileRecordPath $profileRecord
        try {
            [string] $resolvedAnalyzer = Resolve-LocalFile $AnalyzerPath 'AnalyzerPath'
            $initialAnalyzerIdentity = Get-AnalyzerIdentity $resolvedAnalyzer
            $resolvedDotnetTrace = Resolve-Executable $DotnetTracePath 'dotnet-trace'
            [scriptblock] $recorderInvoker = {
                param(
                    [string] $Executable,
                    [string[]] $Arguments,
                    [string] $Purpose)
                [string] $invocationId = [Guid]::NewGuid().ToString('N')
                [string] $preflightDirectory = Join-Path $runDirectory 'profile-artifacts/preflight'
                [string] $stdoutPath = Join-Path $preflightDirectory "$invocationId.stdout.txt"
                [string] $stderrPath = Join-Path $preflightDirectory "$invocationId.stderr.txt"
                [System.Collections.IDictionary] $record = [ordered]@{
                    purpose = $Purpose
                    status = 'running'
                    command = [ordered]@{
                        executable = $Executable
                        arguments = $Arguments
                        workingDirectory = $root
                    }
                    stdoutPath = $stdoutPath
                    stderrPath = $stderrPath
                }
                $profilePreflight.Add($record)
                try {
                    [string] $text = Invoke-NativeText `
                        $Executable `
                        $Arguments `
                        $root `
                        $Purpose `
                        $stdoutPath `
                        $stderrPath
                    $record.status = 'completed'
                    return $text
                }
                catch {
                    if (-not (Test-Path -LiteralPath $stdoutPath)) {
                        [System.IO.Directory]::CreateDirectory($preflightDirectory) | Out-Null
                        [System.IO.File]::WriteAllText($stdoutPath, '', $utf8)
                    }
                    if (-not (Test-Path -LiteralPath $stderrPath)) {
                        [System.IO.File]::WriteAllText(
                            $stderrPath,
                            "$($_.Exception.Message)$([Environment]::NewLine)",
                            $utf8)
                    }
                    $record.status = 'failed'
                    $record['failure'] = $_.Exception.Message
                    throw
                }
                finally {
                    Write-JsonAtomic $profileRecordPath $profileRecord
                }
            }
            $cpuRecorder = Get-DotnetTraceRecorder $resolvedDotnetTrace 'cpu' $recorderInvoker
            $allocationRecorder = Get-DotnetTraceRecorder `
                $resolvedDotnetTrace `
                'alloc' `
                $recorderInvoker
            $profileRecord['tools'] = [ordered]@{
                analyzer = $initialAnalyzerIdentity.Record
                recorder = [ordered]@{
                    path = $resolvedDotnetTrace
                    sha256 = (Get-FileHash -LiteralPath $resolvedDotnetTrace -Algorithm SHA256).Hash
                    version = $cpuRecorder.Version
                    cpuProfiles = @($cpuRecorder.Metadata.profiles)
                    allocationProfiles = @($allocationRecorder.Metadata.profiles)
                }
            }
            $profileRecord.status = 'ready'
            Write-JsonAtomic $profileRecordPath $profileRecord
        }
        catch {
            $profileRecord.status = 'failed'
            $profileRecord['failure'] = $_.Exception.Message
            Write-JsonAtomic $profileRecordPath $profileRecord
            throw
        }
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
            if ($CaptureProfiles) {
                [string] $subjectExecutable = Join-Path `
                    $arm.Checkout `
                    "src/Filtrace/bin/Release/net10.0/$(if ([OperatingSystem]::IsWindows()) { 'filtrace.exe' } else { 'filtrace' })"
                $profileArmInputs.Add([pscustomobject]@{
                    Name = $arm.Name
                    Directory = $armDirectory
                    Checkout = $arm.Checkout
                    Trace = $trace
                    SubjectExecutable = $subjectExecutable
                })
            }
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
        if ($CaptureProfiles) {
            $profileArmInputs.Add([pscustomobject]@{
                Name = $arm.Name
                Directory = $armDirectory
                Checkout = $arm.Checkout
                Trace = $trace
                SubjectExecutable = $subjectExecutable
            })
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
    [double] $cliCpuDelta = if ($baselineTelemetry.averageCpuMilliseconds -eq 0.0) {
        0.0
    }
    else {
        ($candidateTelemetry.averageCpuMilliseconds - $baselineTelemetry.averageCpuMilliseconds) `
            / $baselineTelemetry.averageCpuMilliseconds * 100.0
    }

    [System.Collections.Specialized.OrderedDictionary] $comparison = [ordered]@{
        benchmarkRows = $benchmarkRows
        cliTelemetry = [ordered]@{
            baseline = $baselineTelemetry
            candidate = $candidateTelemetry
            averageLaunchToExitDeltaMilliseconds = `
                $candidateTelemetry.averageLaunchToExitMilliseconds `
                - $baselineTelemetry.averageLaunchToExitMilliseconds
            averageCpuDeltaPercent = $cliCpuDelta
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
    if ($CaptureProfiles) {
        $run['profiles'] = [ordered]@{
            status = 'pending'
            path = 'profiles.json'
            sha256 = (Get-FileHash -LiteralPath (Join-Path $runDirectory 'profiles.json') -Algorithm SHA256).Hash
        }
        Write-JsonAtomic (Join-Path $runDirectory 'run.json') $run
        try {
            Invoke-TrackDProfiles `
                $runDirectory `
                @($profileArmInputs) `
                $initialAnalyzerIdentity `
                $cpuRecorder `
                $allocationRecorder `
                $resolvedDotnetTrace `
                $profileRecord
        }
        catch {
            $run.profiles.status = 'failed'
            $run.profiles.sha256 = (
                Get-FileHash -LiteralPath (Join-Path $runDirectory 'profiles.json') -Algorithm SHA256).Hash
            $run.commandCount = $commandLog.Count
            Write-JsonAtomic (Join-Path $runDirectory 'run.json') $run
            throw
        }
        $run.profiles.status = 'completed'
        $run.profiles.sha256 = (
            Get-FileHash -LiteralPath (Join-Path $runDirectory 'profiles.json') -Algorithm SHA256).Hash
        $run.commandCount = $commandLog.Count
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
