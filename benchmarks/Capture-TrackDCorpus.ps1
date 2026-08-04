#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Capture the initial CPU and activity inputs for a Track D performance investigation.

.DESCRIPTION
  Builds Filtrace.PerfWorkload and the filtrace CLI, records one CPU trace and one
  nested-activity trace with dotnet-trace, verifies both through filtrace, archives
    the raw trace bytes, and writes a SHA-256 manifest with portable capture arguments.

    With -Scale, captures the retained CPU sample-count/depth matrix and activity
    tiers, adapting each duration until its observed target count is within tolerance.

  The output directory must be empty. Derived ETLX caches are removed before the
  archive is created; they are reproducible caches, not corpus inputs.

.PARAMETER OutputDirectory
  Empty destination for traces, archive, and manifest. Defaults to a unique ignored
  directory under artifacts/perf-inputs.

.PARAMETER Workers
  Dedicated workload threads. Defaults to min(processor count, 8).

.PARAMETER CpuDurationMilliseconds
  CPU workload duration. Defaults to 15000 ms.

.PARAMETER ActivityDurationMilliseconds
  Activity workload duration. Defaults to 15000 ms.

.PARAMETER Depth
  Synthetic workload call depth. Defaults to 20.

.PARAMETER ActivityRounds
  Maximum nested activity rounds per worker. Defaults to 1000.

.PARAMETER Scale
    Capture the calibrated retained scale matrix instead of one CPU/activity pair.

.PARAMETER CpuSampleTargets
    CPU sample-count targets for -Scale. Defaults to 10k, 100k, and 1m.

.PARAMETER CpuDepths
    CPU workload depths for -Scale. Defaults to 5 and 20.

.PARAMETER ActivitySampleTargets
    Order-scoped CPU record targets for -Scale. Defaults to 10k and 100k.

.PARAMETER ActivityDepth
    Activity workload depth for -Scale. Defaults to 20.

.PARAMETER CalibrationTolerancePercent
    Allowed observed-count deviation from a scale target. Defaults to 10%.

.PARAMETER CalibrationMaximumAttempts
    Maximum captures used to calibrate one scale scenario. Defaults to 4.

.PARAMETER ScaleMaximumTotalDurationMilliseconds
    Maximum aggregate requested workload duration across scale calibration attempts.
    Defaults to 30 minutes.

.PARAMETER DotnetPath
  dotnet host path or command name. Defaults to dotnet from PATH.

.PARAMETER DotnetTracePath
  dotnet-trace path or command name. Defaults to dotnet-trace from PATH.

.PARAMETER FiltracePath
  filtrace executable or DLL. Defaults to the repository Release CLI DLL.

.PARAMETER NoBuild
  Reuse existing Release outputs instead of building the workload and CLI.

.PARAMETER NativeTimeoutSeconds
    Maximum time for one build, capture, or filtrace query. Defaults to 1800 seconds.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [ValidateRange(1, 256)][int] $Workers = [Math]::Min([Environment]::ProcessorCount, 8),
    [ValidateRange(100, 600000)][int] $CpuDurationMilliseconds = 15000,
    [ValidateRange(100, 600000)][int] $ActivityDurationMilliseconds = 15000,
    [ValidateRange(1, 128)][int] $Depth = 20,
    [ValidateRange(1, 10000000)][int] $ActivityRounds = 1000,
    [switch] $Scale,
    [int[]] $CpuSampleTargets = @(10000, 100000, 1000000),
    [int[]] $CpuDepths = @(5, 20),
    [int[]] $ActivitySampleTargets = @(10000, 100000),
    [ValidateRange(1, 128)][int] $ActivityDepth = 20,
    [ValidateRange(1.0, 50.0)][double] $CalibrationTolerancePercent = 10.0,
    [ValidateRange(1, 4)][int] $CalibrationMaximumAttempts = 4,
    [ValidateRange(60000, 7200000)][long] $ScaleMaximumTotalDurationMilliseconds = 1800000,
    [string] $DotnetPath = 'dotnet',
    [string] $DotnetTracePath = 'dotnet-trace',
    [string] $FiltracePath,
    [ValidateRange(1, 86400)][int] $NativeTimeoutSeconds = 1800,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workloadProject = Join-Path $root 'benchmarks/Filtrace.PerfWorkload/Filtrace.PerfWorkload.csproj'
$workloadDll = Join-Path $root 'benchmarks/Filtrace.PerfWorkload/bin/Release/net10.0/Filtrace.PerfWorkload.dll'
$filtraceProject = Join-Path $root 'src/Filtrace/Filtrace.csproj'
$defaultFiltraceDll = Join-Path $root 'src/Filtrace/bin/Release/net10.0/filtrace.dll'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$calibrationRates = @{}
$maximumScaleScenarios = 16
$maximumScaleTargetRecords = 5000000
$maximumCapturedBytes = 10 * 1024 * 1024
$nativeCleanupTimeoutMilliseconds = 10000
$script:calibrationRequestedDurationMilliseconds = 0L

function Resolve-Executable([string] $Command, [string] $Purpose) {
    if (Test-Path -LiteralPath $Command -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Command).Path
    }

    [System.Management.Automation.CommandInfo[]] $resolved = @(
        Get-Command $Command -CommandType Application -ErrorAction SilentlyContinue)
    if ($resolved.Count -eq 0) {
        throw "$Purpose was not found at '$Command' or on PATH."
    }

    return $resolved[0].Source
}

function Invoke-NativeChecked(
    [string] $Executable,
    [string[]] $Arguments,
    [string] $Purpose) {
    [System.Diagnostics.Process] $process = Start-NativeProcess $Executable $Arguments $false
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

function Invoke-FiltraceJson([string[]] $Arguments) {
    [string] $executable = ''
    [string[]] $nativeArguments = @()
    if ($script:filtraceCommand.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
        $executable = $script:dotnet
        $nativeArguments = @($script:filtraceCommand) + $Arguments
    }
    else {
        $executable = $script:filtraceCommand
        $nativeArguments = $Arguments
    }

    [string] $json = Invoke-NativeText `
        $executable `
        $nativeArguments `
        "filtrace $($Arguments[0])"
    if ($json.Length -eq 0) {
        throw "filtrace $($Arguments[0]) returned empty output."
    }

    try {
        return $json | ConvertFrom-Json
    }
    catch {
        throw "filtrace $($Arguments[0]) returned malformed JSON: $($_.Exception.Message)"
    }
}

function Invoke-NativeText(
    [string] $Executable,
    [string[]] $Arguments,
    [string] $Purpose) {
    [string] $outputPath = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        "filtrace-corpus-output-$([Guid]::NewGuid().ToString('N')).tmp"
    [string] $errorPath = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        "filtrace-corpus-error-$([Guid]::NewGuid().ToString('N')).tmp"
    [System.Diagnostics.Process] $process = Start-NativeProcess $Executable $Arguments $true
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

        [string] $output = [System.IO.File]::ReadAllText($outputPath)
        [string] $error = [System.IO.File]::ReadAllText($errorPath)

        if ($process.ExitCode -ne 0) {
            [string] $detail = if ($error.Length -le 1000) { $error } else { $error.Substring(0, 1000) }
            throw "$Purpose exited with code $($process.ExitCode): $detail"
        }

        return $output.Trim()
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
    [bool] $CaptureOutput) {
    [System.Diagnostics.ProcessStartInfo] $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $root
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

function ConvertTo-PortablePath([string] $Path) {
    [string] $relative = [System.IO.Path]::GetRelativePath($root, $Path)
    return $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

function Invoke-Capture(
    [string] $Name,
    [string] $Mode,
    [int] $DurationMilliseconds,
    [int] $CaptureDepth,
    [int] $ActivityRoundLimit,
    [bool] $IncludeActivityProvider,
    [string] $InputsDirectory) {
    [string] $tracePath = Join-Path $InputsDirectory "$Name.nettrace"
    [System.Collections.Generic.List[string]] $arguments = @(
        'collect',
        '--profile', 'dotnet-common,dotnet-sampled-thread-time'
    )
    if ($IncludeActivityProvider) {
        $arguments.Add('--providers')
        $arguments.Add('Filtrace-TrackD:0xFFFFFFFFFFFFFFFF:5')
    }

    $arguments.Add('--output')
    $arguments.Add($tracePath)
    $arguments.Add('--')
    $arguments.Add($script:dotnet)
    $arguments.Add($script:workloadDll)
    $arguments.Add($Mode)
    $arguments.Add('--workers')
    $arguments.Add($Workers.ToString([Globalization.CultureInfo]::InvariantCulture))
    $arguments.Add('--duration-ms')
    $arguments.Add($DurationMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    $arguments.Add('--depth')
    $arguments.Add($CaptureDepth.ToString([Globalization.CultureInfo]::InvariantCulture))
    if ($IncludeActivityProvider) {
        $arguments.Add('--activity-rounds')
        $arguments.Add($ActivityRoundLimit.ToString([Globalization.CultureInfo]::InvariantCulture))
    }

    Write-Host "Capturing $Name -> $tracePath" -ForegroundColor Cyan
    Invoke-NativeChecked $script:dotnetTrace $arguments.ToArray() "$Name capture" | Out-Host
    if (-not (Test-Path -LiteralPath $tracePath -PathType Leaf)) {
        throw "$Name capture produced no trace at '$tracePath'."
    }

    [string[]] $manifestArguments = @($arguments.ToArray() | ForEach-Object {
        if ([string]::Equals($_, $tracePath, [StringComparison]::Ordinal)) {
            return "inputs/$Name.nettrace"
        }

        if ([string]::Equals($_, $script:dotnet, [StringComparison]::Ordinal)) {
            return 'dotnet'
        }

        if ([string]::Equals($_, $script:workloadDll, [StringComparison]::Ordinal)) {
            return ConvertTo-PortablePath $script:workloadDll
        }

        return $_
    })

    return [pscustomobject]@{
        Name = $Name
        Path = $tracePath
        Mode = $Mode
        Depth = $CaptureDepth
        DurationMilliseconds = $DurationMilliseconds
        ActivityRoundLimit = $ActivityRoundLimit
        TargetSampleCount = $null
        ObservedTargetSampleCount = $null
        ActivityRows = $null
        OrderCpuRecords = $null
        ManifestArguments = $manifestArguments
    }
}

function Get-TraceRecord([object] $Capture, [object] $Info) {
    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Capture.Path
    [string] $hash = (Get-FileHash -LiteralPath $Capture.Path -Algorithm SHA256).Hash
    return [ordered]@{
        name = $Capture.Name
        archivePath = "inputs/$($file.Name)"
        sha256 = $hash
        bytes = $file.Length
        sampleCount = $Info.result.sampleCount
        totalWeight = $Info.result.totalWeight
        mode = $Capture.Mode
        depth = $Capture.Depth
        durationMilliseconds = $Capture.DurationMilliseconds
        targetSampleCount = $Capture.TargetSampleCount
        observedTargetSampleCount = $Capture.ObservedTargetSampleCount
        activityRows = $Capture.ActivityRows
        orderCpuRecords = $Capture.OrderCpuRecords
        capture = [ordered]@{
            executable = 'dotnet-trace'
            arguments = $Capture.ManifestArguments
        }
    }
}

function Assert-BoundedUniqueValues(
    [int[]] $Values,
    [string] $Name,
    [int] $Minimum,
    [int] $Maximum,
    [int] $MaximumCount) {
    if ($Values.Count -eq 0) {
        throw "$Name must contain at least one value."
    }

    if (@($Values | Sort-Object -Unique).Count -ne $Values.Count) {
        throw "$Name must not contain duplicate values."
    }

    if ($Values.Count -gt $MaximumCount) {
        throw "$Name contains $($Values.Count) values; the maximum is $MaximumCount."
    }

    foreach ($value in $Values) {
        if ($value -lt $Minimum -or $value -gt $Maximum) {
            throw "$Name value $value must be in [$Minimum, $Maximum]."
        }
    }
}

function Get-CaptureEvidence([object] $Capture) {
    [object] $info = Invoke-FiltraceJson @('info', $Capture.Path, '--format', 'json')
    if ([int]$info.result.sampleCount -le 0) {
        throw "Capture '$($Capture.Name)' contains no normalized samples."
    }

    [object] $activityRank = $null
    [object] $activityCpu = $null
    if ([string]$Capture.Mode -eq 'activity') {
        if (
            [string]$info.result.analyses.activity.captureStatus -ne 'enabled' -or
            [int]$info.result.analyses.activity.eventCount -le 0
        ) {
            throw "Activity capture '$($Capture.Name)' does not contain enabled activity events."
        }

        $activityRank = Invoke-FiltraceJson @(
            'rank', $Capture.Path, '--metric', 'activity', '--format', 'json')
        $activityCpu = Invoke-FiltraceJson @(
            'rank', $Capture.Path, '--metric', 'cpu', '--activity', 'Order', '--format', 'json')
        $Capture.ActivityRows = @($activityRank.result.rows).Count
        $Capture.OrderCpuRecords = [int]$activityCpu.result.contributingRecordCount
        $Capture.ObservedTargetSampleCount = $Capture.OrderCpuRecords
        if ($Capture.ActivityRows -le 0 -or $Capture.OrderCpuRecords -le 0) {
            throw "Activity capture '$($Capture.Name)' produced no completed activities or Order-scoped CPU records."
        }
    }
    else {
        $Capture.ObservedTargetSampleCount = [int]$info.result.sampleCount
    }

    return [pscustomobject]@{
        Capture = $Capture
        Info = $info
        ActivityRank = $activityRank
        ActivityCpu = $activityCpu
    }
}

function Remove-CaptureArtifacts([string] $TracePath) {
    foreach ($path in @($TracePath, "$TracePath.etlx")) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        }
    }
}

function Format-SampleTarget([int] $Target) {
    if ($Target % 1000000 -eq 0) {
        return "$($Target / 1000000)m"
    }

    if ($Target % 1000 -eq 0) {
        return "$($Target / 1000)k"
    }

    return $Target.ToString([Globalization.CultureInfo]::InvariantCulture)
}

function Limit-Duration([long] $DurationMilliseconds) {
    return [int][Math]::Min(600000, [Math]::Max(100, $DurationMilliseconds))
}

function Invoke-CalibratedCapture(
    [string] $Name,
    [string] $Mode,
    [int] $TargetSampleCount,
    [int] $CaptureDepth,
    [bool] $IncludeActivityProvider,
    [int] $ActivityRoundLimit,
    [string] $InputsDirectory) {
    [string] $calibrationKey = "$Mode|$CaptureDepth|$Workers"
    [double] $samplesPerMillisecond = if ($calibrationRates.ContainsKey($calibrationKey)) {
        [double]$calibrationRates[$calibrationKey]
    }
    elseif ($IncludeActivityProvider) {
        [Math]::Max(0.1, $Workers * 0.5)
    }
    else {
        [Math]::Max(0.1, $Workers)
    }
    [int] $duration = Limit-Duration ([long][Math]::Round(
        $TargetSampleCount / $samplesPerMillisecond))
    [double] $tolerance = $CalibrationTolerancePercent / 100.0

    for ([int] $attempt = 1; $attempt -le $CalibrationMaximumAttempts; $attempt++) {
        if ($duration -gt $ScaleMaximumTotalDurationMilliseconds `
            - $script:calibrationRequestedDurationMilliseconds) {
            throw "Scale calibration would exceed the $ScaleMaximumTotalDurationMilliseconds ms aggregate workload budget."
        }

        $script:calibrationRequestedDurationMilliseconds += $duration
        Remove-CaptureArtifacts (Join-Path $InputsDirectory "$Name.nettrace")
        [object] $capture = Invoke-Capture `
            $Name `
            $Mode `
            $duration `
            $CaptureDepth `
            $ActivityRoundLimit `
            $IncludeActivityProvider `
            $InputsDirectory
        $capture.TargetSampleCount = $TargetSampleCount
        [object] $evidence = Get-CaptureEvidence $capture
        [int] $observed = $capture.ObservedTargetSampleCount
        if ($observed -gt 0) {
            $calibrationRates[$calibrationKey] = $observed / [double]$duration
        }

        [double] $minimum = $TargetSampleCount * (1.0 - $tolerance)
        [double] $maximum = $TargetSampleCount * (1.0 + $tolerance)
        Write-Host (
            "Calibration $Name attempt $attempt`: target=$TargetSampleCount " +
            "observed=$observed duration=$duration ms") -ForegroundColor Cyan
        if ($observed -ge $minimum -and $observed -le $maximum) {
            return $evidence
        }

        if ($attempt -eq $CalibrationMaximumAttempts) {
            throw "Capture '$Name' observed $observed records; target $TargetSampleCount +/- $CalibrationTolerancePercent% was not reached."
        }

        if ($observed -le 0) {
            $nextDuration = [long]$duration * 2
        }
        else {
            $nextDuration = [long][Math]::Round(
                $duration * ([double]$TargetSampleCount / $observed))
        }

        [int] $limited = Limit-Duration $nextDuration
        if ($limited -eq $duration) {
            [int] $adjustment = if ($observed -lt $TargetSampleCount) { 100 } else { -100 }
            $limited = Limit-Duration ($duration + $adjustment)
        }

        $duration = $limited
    }
}

function Test-CorpusArchive(
    [string] $ArchivePath,
    [System.Collections.Specialized.OrderedDictionary] $Manifest) {
    [System.IO.Compression.ZipArchive] $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -ne $Manifest.traces.Count) {
            throw "Corpus archive has $($archive.Entries.Count) entries; expected $($Manifest.traces.Count)."
        }

        foreach ($trace in $Manifest.traces) {
            if ([string]$trace.archivePath -match '\.etlx$') {
                throw "Corpus manifest contains derived ETLX '$($trace.archivePath)'."
            }

            [System.IO.Compression.ZipArchiveEntry] $entry = $archive.GetEntry([string]$trace.archivePath)
            if ($null -eq $entry) {
                throw "Corpus archive is missing '$($trace.archivePath)'."
            }

            if ($entry.Length -ne [long]$trace.bytes) {
                throw "Corpus archive length for '$($trace.archivePath)' does not match the manifest."
            }

            [System.IO.Stream] $entryStream = $entry.Open()
            [System.Security.Cryptography.SHA256] $sha256 = [System.Security.Cryptography.SHA256]::Create()
            try {
                [string] $entryHash = [Convert]::ToHexString($sha256.ComputeHash($entryStream))
            }
            finally {
                $sha256.Dispose()
                $entryStream.Dispose()
            }

            if ($entryHash -cne [string]$trace.sha256) {
                throw "Corpus archive hash for '$($trace.archivePath)' does not match the manifest."
            }
        }

        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('.etlx', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Corpus archive contains derived ETLX '$($entry.FullName)'."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-CorpusManifest(
    [string] $ManifestPath,
    [string] $ExpectedArchiveHash,
    [long] $ExpectedArchiveBytes,
    [string[]] $ExpectedTraceNames) {
    [object] $readBack = try {
        Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Corpus manifest readback failed: $($_.Exception.Message)"
    }

    if (
        [int]$readBack.schemaVersion -ne 1 -or
        @($readBack.traces).Count -ne $ExpectedTraceNames.Count -or
        [string]$readBack.archive.path -cne 'input-corpus.zip' -or
        [string]$readBack.archive.sha256 -cne $ExpectedArchiveHash -or
        [long]$readBack.archive.bytes -ne $ExpectedArchiveBytes
    ) {
        throw 'Corpus manifest readback did not preserve its schema, traces, and archive hash.'
    }

    [string] $expectedWorkloadAssembly = ConvertTo-PortablePath $script:workloadDll
    if (
        [string]$readBack.workload.assembly -cne $expectedWorkloadAssembly -or
        [System.IO.Path]::IsPathRooted([string]$readBack.archive.path)
    ) {
        throw 'Corpus manifest contains an unexpected workload or archive path.'
    }

    if (
        [int]$readBack.evidence.activityRows -le 0 -or
        [int]$readBack.evidence.orderCpuRecords -le 0
    ) {
        throw 'Corpus manifest readback did not preserve the required activity evidence.'
    }

    [string[]] $traceNames = @($readBack.traces | ForEach-Object { [string]$_.name })
    if (@($traceNames | Sort-Object -Unique).Count -ne $ExpectedTraceNames.Count) {
        throw "Corpus manifest contains unexpected trace names: $($traceNames -join ', ')."
    }

    foreach ($expectedTraceName in $ExpectedTraceNames) {
        if ($traceNames -cnotcontains $expectedTraceName) {
            throw "Corpus manifest is missing trace '$expectedTraceName'."
        }
    }

    foreach ($trace in $readBack.traces) {
        [string] $expectedArchivePath = "inputs/$($trace.name).nettrace"
        [string] $expectedOutputPath = "inputs/$($trace.name).nettrace"
        if (
            [string]$trace.archivePath -cne $expectedArchivePath -or
            [long]$trace.bytes -le 0 -or
            [int]$trace.sampleCount -le 0 -or
            [string]$trace.sha256 -notmatch '\A[0-9A-F]{64}\z' -or
            [string]$trace.capture.executable -cne 'dotnet-trace'
        ) {
            throw "Corpus trace '$($trace.name)' contains incomplete artifact or capture metadata."
        }

        if (
            $null -ne $trace.targetSampleCount -and
            ([int]$trace.targetSampleCount -le 0 -or
                [int]$trace.observedTargetSampleCount -le 0)
        ) {
            throw "Corpus trace '$($trace.name)' contains incomplete calibration metadata."
        }

        if (
            [string]$trace.mode -eq 'activity' -and
            ([int]$trace.activityRows -le 0 -or [int]$trace.orderCpuRecords -le 0)
        ) {
            throw "Corpus activity trace '$($trace.name)' contains incomplete activity evidence."
        }

        [string[]] $captureArguments = @($trace.capture.arguments)
        if (
            $captureArguments -cnotcontains $expectedOutputPath -or
            $captureArguments -cnotcontains $expectedWorkloadAssembly
        ) {
            throw "Corpus trace '$($trace.name)' does not contain its portable output and workload arguments."
        }

        foreach ($argument in $captureArguments) {
            if ([System.IO.Path]::IsPathRooted($argument)) {
                throw "Corpus trace '$($trace.name)' contains rooted capture argument '$argument'."
            }
        }
    }
}

$script:dotnet = Resolve-Executable $DotnetPath 'dotnet'
$script:dotnetTrace = Resolve-Executable $DotnetTracePath 'dotnet-trace'
if (-not $NoBuild) {
    Invoke-NativeChecked $script:dotnet @('build', $workloadProject, '-c', 'Release') 'workload build'
    Invoke-NativeChecked $script:dotnet @('build', $filtraceProject, '-c', 'Release') 'filtrace build'
}

if (-not (Test-Path -LiteralPath $workloadDll -PathType Leaf)) {
    throw "Workload output was not found at '$workloadDll'."
}

$script:workloadDll = (Resolve-Path -LiteralPath $workloadDll).Path
if ([string]::IsNullOrEmpty($FiltracePath)) {
    $script:filtraceCommand = if (Test-Path -LiteralPath $defaultFiltraceDll -PathType Leaf) {
        (Resolve-Path -LiteralPath $defaultFiltraceDll).Path
    }
    else {
        throw "Filtrace output was not found at '$defaultFiltraceDll'."
    }
}
elseif ($FiltracePath.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
    if (-not (Test-Path -LiteralPath $FiltracePath -PathType Leaf)) {
        throw "Filtrace DLL was not found at '$FiltracePath'."
    }

    $script:filtraceCommand = (Resolve-Path -LiteralPath $FiltracePath).Path
}
else {
    $script:filtraceCommand = Resolve-Executable $FiltracePath 'filtrace'
}

if ([string]::IsNullOrEmpty($OutputDirectory)) {
    [string] $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    [string] $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $root "artifacts/perf-inputs/trackd-$stamp-$suffix"
}

[string] $outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) {
        throw "Output path '$outputPath' is not a directory."
    }

    if (@(Get-ChildItem -LiteralPath $outputPath -Force).Count -ne 0) {
        throw "Output directory '$outputPath' is not empty."
    }
}

[string] $outputParent = [System.IO.Path]::GetDirectoryName($outputPath)
[string] $outputName = [System.IO.Path]::GetFileName(
    [System.IO.Path]::TrimEndingDirectorySeparator($outputPath))
if ([string]::IsNullOrEmpty($outputParent) -or [string]::IsNullOrEmpty($outputName)) {
    throw "Output directory '$outputPath' must have a parent and a directory name."
}

[System.IO.Directory]::CreateDirectory($outputParent) | Out-Null
[string] $stagingPath = Join-Path $outputParent ".$outputName.partial-$([Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null
[bool] $published = $false
[System.Management.Automation.ErrorRecord] $operationError = $null
try {
    [string] $inputsPath = Join-Path $stagingPath 'inputs'
    [System.IO.Directory]::CreateDirectory($inputsPath) | Out-Null
    [System.Collections.Generic.List[object]] $evidenceRecords = [System.Collections.Generic.List[object]]::new()
    [int] $effectiveActivityRounds = if ($Scale) { 10000000 } else { $ActivityRounds }
    if ($Scale) {
        Assert-BoundedUniqueValues $CpuSampleTargets 'CpuSampleTargets' 100 10000000 8
        Assert-BoundedUniqueValues $CpuDepths 'CpuDepths' 1 128 4
        Assert-BoundedUniqueValues $ActivitySampleTargets 'ActivitySampleTargets' 100 10000000 4
        [long] $scenarioCount = [long]$CpuSampleTargets.Count * $CpuDepths.Count `
            + $ActivitySampleTargets.Count
        [long] $targetRecordCount = `
            [long](($CpuSampleTargets | Measure-Object -Sum).Sum) * $CpuDepths.Count `
            + [long](($ActivitySampleTargets | Measure-Object -Sum).Sum)
        if (
            $scenarioCount -gt $maximumScaleScenarios -or
            $targetRecordCount -gt $maximumScaleTargetRecords
        ) {
            throw "Scale matrix requests $scenarioCount scenarios and $targetRecordCount target records; limits are $maximumScaleScenarios and $maximumScaleTargetRecords."
        }

        foreach ($captureDepth in $CpuDepths) {
            foreach ($target in $CpuSampleTargets) {
                [string] $name = "cpu-$(Format-SampleTarget $target)-d$captureDepth"
                $evidenceRecords.Add((Invoke-CalibratedCapture `
                    $name `
                    'cpu' `
                    $target `
                    $captureDepth `
                    $false `
                    $effectiveActivityRounds `
                    $inputsPath))
            }
        }

        foreach ($target in $ActivitySampleTargets) {
            [string] $name = "activity-$(Format-SampleTarget $target)-d$ActivityDepth"
            $evidenceRecords.Add((Invoke-CalibratedCapture `
                $name `
                'activity' `
                $target `
                $ActivityDepth `
                $true `
                $effectiveActivityRounds `
                $inputsPath))
        }
    }
    else {
        [object] $cpuCapture = Invoke-Capture `
            'cpu' `
            'cpu' `
            $CpuDurationMilliseconds `
            $Depth `
            $effectiveActivityRounds `
            $false `
            $inputsPath
        $evidenceRecords.Add((Get-CaptureEvidence $cpuCapture))

        [object] $activityCapture = Invoke-Capture `
            'activity' `
            'activity' `
            $ActivityDurationMilliseconds `
            $Depth `
            $effectiveActivityRounds `
            $true `
            $inputsPath
        $evidenceRecords.Add((Get-CaptureEvidence $activityCapture))
    }

    [object[]] $activityEvidence = @(
        $evidenceRecords | Where-Object { [string]$_.Capture.Mode -eq 'activity' })
    if ($activityEvidence.Count -eq 0) {
        throw 'Corpus contains no activity capture evidence.'
    }

    [System.IO.FileInfo[]] $etlxFiles = @(
        Get-ChildItem -LiteralPath $inputsPath -Filter '*.etlx' -File -Recurse)
    foreach ($etlxFile in $etlxFiles) {
        Remove-Item -LiteralPath $etlxFile.FullName -Force -ErrorAction Stop
    }

    [System.IO.FileInfo[]] $remainingEtlx = @(
        Get-ChildItem -LiteralPath $inputsPath -Filter '*.etlx' -File -Recurse)
    if ($remainingEtlx.Count -ne 0) {
        throw "Derived ETLX files remain before archive publication: $($remainingEtlx.FullName -join ', ')."
    }

    [string] $archivePath = Join-Path $stagingPath 'input-corpus.zip'
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $inputsPath,
        $archivePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)

    [System.IO.FileInfo] $archiveFile = Get-Item -LiteralPath $archivePath
    [string] $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    [System.Collections.Specialized.OrderedDictionary] $workloadRecord = [ordered]@{
        assembly = ConvertTo-PortablePath $script:workloadDll
        workers = $Workers
        scale = $Scale.IsPresent
        activityRounds = $effectiveActivityRounds
    }
    if ($Scale) {
        $workloadRecord.cpuSampleTargets = $CpuSampleTargets
        $workloadRecord.cpuDepths = $CpuDepths
        $workloadRecord.activitySampleTargets = $ActivitySampleTargets
        $workloadRecord.activityDepth = $ActivityDepth
        $workloadRecord.calibrationTolerancePercent = $CalibrationTolerancePercent
        $workloadRecord.calibrationMaximumAttempts = $CalibrationMaximumAttempts
        $workloadRecord.scaleMaximumTotalDurationMilliseconds = $ScaleMaximumTotalDurationMilliseconds
        $workloadRecord.requestedCalibrationDurationMilliseconds = `
            $script:calibrationRequestedDurationMilliseconds
    }
    else {
        $workloadRecord.depth = $Depth
        $workloadRecord.cpuDurationMilliseconds = $CpuDurationMilliseconds
        $workloadRecord.activityDurationMilliseconds = $ActivityDurationMilliseconds
    }

    [object[]] $traceRecords = @(
        $evidenceRecords | ForEach-Object { Get-TraceRecord $_.Capture $_.Info })
    [string[]] $traceNames = @(
        $evidenceRecords | ForEach-Object { [string]$_.Capture.Name })
    [System.Collections.Specialized.OrderedDictionary] $manifest = [ordered]@{
        schemaVersion = 1
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        workload = $workloadRecord
        traces = $traceRecords
        evidence = [ordered]@{
            activityRows = [int](
                $activityEvidence.Capture.ActivityRows | Measure-Object -Sum).Sum
            activityScopeWeight = [double](
                $activityEvidence.ActivityRank.result.scopeWeight | Measure-Object -Sum).Sum
            orderCpuRecords = [int](
                $activityEvidence.Capture.OrderCpuRecords | Measure-Object -Sum).Sum
        }
        archive = [ordered]@{
            path = [System.IO.Path]::GetFileName($archivePath)
            sha256 = $archiveHash
            bytes = $archiveFile.Length
        }
    }

    Test-CorpusArchive $archivePath $manifest

    [string] $manifestPath = Join-Path $stagingPath 'input-corpus.manifest.json'
    [string] $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($manifestPath, "$manifestJson`n", $utf8)
    Test-CorpusManifest $manifestPath $archiveHash $archiveFile.Length $traceNames

    if (Test-Path -LiteralPath $outputPath) {
        if (
            -not (Test-Path -LiteralPath $outputPath -PathType Container) -or
            @(Get-ChildItem -LiteralPath $outputPath -Force).Count -ne 0
        ) {
            throw "Output directory '$outputPath' changed while the corpus was being prepared."
        }

        Remove-Item -LiteralPath $outputPath -Force -ErrorAction Stop
    }

    Move-Item -LiteralPath $stagingPath -Destination $outputPath -ErrorAction Stop
    $published = $true
}
catch {
    $operationError = $_
    throw
}
finally {
    if (-not $published -and (Test-Path -LiteralPath $stagingPath)) {
        try {
            Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction Stop
        }
        catch {
            if ($null -ne $operationError) {
                [string] $cleanupMessage = "Corpus preparation failed: $($operationError.Exception.Message) Partial cleanup also failed for '$stagingPath': $($_.Exception.Message)"
                Write-Error $cleanupMessage -ErrorAction Continue
            }
            else {
                throw
            }
        }
    }
}

Write-Host "Track D corpus: $(Join-Path $outputPath 'input-corpus.zip')" -ForegroundColor Green
Write-Host "Manifest: $(Join-Path $outputPath 'input-corpus.manifest.json')" -ForegroundColor Green
