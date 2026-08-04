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

.PARAMETER DotnetPath
  dotnet host path or command name. Defaults to dotnet from PATH.

.PARAMETER DotnetTracePath
  dotnet-trace path or command name. Defaults to dotnet-trace from PATH.

.PARAMETER FiltracePath
  filtrace executable or DLL. Defaults to the repository Release CLI DLL.

.PARAMETER NoBuild
  Reuse existing Release outputs instead of building the workload and CLI.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [ValidateRange(1, 256)][int] $Workers = [Math]::Min([Environment]::ProcessorCount, 8),
    [ValidateRange(100, 600000)][int] $CpuDurationMilliseconds = 15000,
    [ValidateRange(100, 600000)][int] $ActivityDurationMilliseconds = 15000,
    [ValidateRange(1, 128)][int] $Depth = 20,
    [ValidateRange(1, 10000000)][int] $ActivityRounds = 1000,
    [string] $DotnetPath = 'dotnet',
    [string] $DotnetTracePath = 'dotnet-trace',
    [string] $FiltracePath,
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

function Resolve-Executable([string] $Command, [string] $Purpose) {
    if (Test-Path -LiteralPath $Command -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Command).Path
    }

    [System.Management.Automation.CommandInfo] $resolved = Get-Command $Command -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $resolved) {
        throw "$Purpose was not found at '$Command' or on PATH."
    }

    return $resolved.Source
}

function Invoke-NativeChecked(
    [string] $Executable,
    [string[]] $Arguments,
    [string] $Purpose) {
    & $Executable @Arguments
    [int] $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Purpose exited with code $exitCode."
    }
}

function Invoke-FiltraceJson([string[]] $Arguments) {
    [object[]] $output = if ($script:filtraceCommand.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
        @(& $script:dotnet $script:filtraceCommand @Arguments)
    }
    else {
        @(& $script:filtraceCommand @Arguments)
    }

    [int] $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "filtrace $($Arguments[0]) exited with code $exitCode."
    }

    [string] $json = ($output -join [Environment]::NewLine).Trim()
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

function ConvertTo-PortablePath([string] $Path) {
    [string] $relative = [System.IO.Path]::GetRelativePath($root, $Path)
    return $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

function Invoke-Capture(
    [string] $Name,
    [string] $Mode,
    [int] $DurationMilliseconds,
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
    $arguments.Add($Depth.ToString([Globalization.CultureInfo]::InvariantCulture))
    if ($IncludeActivityProvider) {
        $arguments.Add('--activity-rounds')
        $arguments.Add($ActivityRounds.ToString([Globalization.CultureInfo]::InvariantCulture))
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
        capture = [ordered]@{
            executable = 'dotnet-trace'
            arguments = $Capture.ManifestArguments
        }
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
    [long] $ExpectedArchiveBytes) {
    [object] $readBack = try {
        Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Corpus manifest readback failed: $($_.Exception.Message)"
    }

    if (
        [int]$readBack.schemaVersion -ne 1 -or
        @($readBack.traces).Count -ne 2 -or
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
    if ($traceNames -cnotcontains 'cpu' -or $traceNames -cnotcontains 'activity') {
        throw "Corpus manifest contains unexpected trace names: $($traceNames -join ', ')."
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
    [object] $cpuCapture = Invoke-Capture 'cpu' 'cpu' $CpuDurationMilliseconds $false $inputsPath
    [object] $activityCapture = Invoke-Capture 'activity' 'activity' $ActivityDurationMilliseconds $true $inputsPath

    [object] $cpuInfo = Invoke-FiltraceJson @('info', $cpuCapture.Path, '--format', 'json')
    [object] $activityInfo = Invoke-FiltraceJson @('info', $activityCapture.Path, '--format', 'json')
    [object] $activityRank = Invoke-FiltraceJson @('rank', $activityCapture.Path, '--metric', 'activity', '--format', 'json')
    [object] $activityCpu = Invoke-FiltraceJson @(
        'rank', $activityCapture.Path, '--metric', 'cpu', '--activity', 'Order', '--format', 'json')

    if ([int]$cpuInfo.result.sampleCount -le 0) {
        throw 'CPU capture contains no normalized samples.'
    }

    if (
        [int]$activityInfo.result.sampleCount -le 0 -or
        [string]$activityInfo.result.analyses.activity.captureStatus -ne 'enabled' -or
        [int]$activityInfo.result.analyses.activity.eventCount -le 0
    ) {
        throw 'Activity capture does not contain enabled activity events and CPU samples.'
    }

    if (@($activityRank.result.rows).Count -eq 0) {
        throw 'Activity capture produced no completed activity ranking rows.'
    }

    if ([int]$activityCpu.result.contributingRecordCount -le 0) {
        throw "Activity capture produced no CPU records inside the 'Order' activity."
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
    [System.Collections.Specialized.OrderedDictionary] $manifest = [ordered]@{
        schemaVersion = 1
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        workload = [ordered]@{
            assembly = ConvertTo-PortablePath $script:workloadDll
            workers = $Workers
            depth = $Depth
            cpuDurationMilliseconds = $CpuDurationMilliseconds
            activityDurationMilliseconds = $ActivityDurationMilliseconds
            activityRounds = $ActivityRounds
        }
        traces = @(
            Get-TraceRecord $cpuCapture $cpuInfo
            Get-TraceRecord $activityCapture $activityInfo
        )
        evidence = [ordered]@{
            activityRows = @($activityRank.result.rows).Count
            activityScopeWeight = $activityRank.result.scopeWeight
            orderCpuRecords = $activityCpu.result.contributingRecordCount
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
    Test-CorpusManifest $manifestPath $archiveHash $archiveFile.Length

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
