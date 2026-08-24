#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

#Requires -Version 7.0

<#
.SYNOPSIS
    Run a validated set of read-only Filtrace queries and retain a replayable analysis record.

.DESCRIPTION
    Reads a versioned JSON plan, validates every input and query before execution,
    inventories input bytes with SHA-256, and runs each query with compact JSON output.
    The output directory retains the unchanged plan, exact argument arrays, separate
    UTF-8 stdout/stderr files, exit codes, output hashes, Filtrace version, and a small
    allowlisted host fingerprint.

    Pass -ReplayFrom with a prior run.json to require the same plan and input bytes.
    A mismatch fails before the output directory is created or any query runs.

    Version 1 is intentionally read-only. Capture, cache mutation, and export are not
    accepted operations.

.PARAMETER Plan
    Version 1 JSON plan. Relative input and symbol paths resolve from the plan directory.

.PARAMETER OutputDirectory
    New directory to create for this run. An existing path is rejected.

.PARAMETER FiltracePath
    Filtrace executable path or command name. Defaults to filtrace.

.PARAMETER ReplayFrom
    Optional prior run.json whose plan and input hashes must match before execution.

.PARAMETER TimeoutSeconds
    Maximum duration of each Filtrace query. Default 1200 seconds.

.EXAMPLE
    ./Invoke-FiltraceAnalysis.ps1 -Plan analysis-plan.json -OutputDirectory artifacts/analysis/run-1

.EXAMPLE
    ./Invoke-FiltraceAnalysis.ps1 -Plan analysis-plan.json -OutputDirectory artifacts/analysis/run-2 -ReplayFrom artifacts/analysis/run-1/run.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Plan,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $FiltracePath = 'filtrace',
    [string] $ReplayFrom,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 1200
)

$ErrorActionPreference = 'Stop'

$maxPlanBytes = 1MB
$maxReplayBytes = 16MB
$maxManifestBytes = 16MB
$maxInputs = 32
$maxQueries = 64
$maxArgumentsPerQuery = 64
$maxArgumentLength = 8192
$maxStdoutBytes = 1MB
$maxStderrBytes = 256KB
$maxRunRecordBytes = 16MB
$utf8 = [System.Text.UTF8Encoding]::new($false)
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$fileInventoryCache = [System.Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::Ordinal)
$allowedOperations = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @(
        'info', 'rank', 'cpu', 'alloc', 'exceptions', 'threadtime', 'callers',
        'lines', 'heatmap', 'processes', 'lifecycle', 'tree', 'classify',
        'timeline', 'diff', 'batch', 'gcstats', 'jitstats', 'threadpool',
        'diskio', 'events'
    ),
    [StringComparer]::Ordinal)
$symbolOperations = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @('info', 'rank', 'cpu', 'callers', 'lines', 'heatmap', 'tree', 'classify', 'diff', 'batch'),
    [StringComparer]::Ordinal)

function Get-RequiredProperty([object] $Object, [string] $Name, [string] $Owner) {
    [System.Management.Automation.PSPropertyInfo] $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "$Owner requires property '$Name'."
    }

    return $property.Value
}

function Test-RecordId([string] $Value) {
    return $Value -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'
}

function Get-LocalFullPath([string] $Value, [string] $BaseDirectory, [string] $Description) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.IndexOfAny([char[]] @([char] 0, "`r", "`n")) -ge 0) {
        throw "$Description must be a nonempty path without control characters."
    }

    [string] $candidate = if ([System.IO.Path]::IsPathFullyQualified($Value)) {
        $Value
    }
    else {
        Join-Path $BaseDirectory $Value
    }
    [string] $fullPath = [System.IO.Path]::GetFullPath($candidate)
    if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal) -or
        $fullPath.StartsWith('//', [StringComparison]::Ordinal)) {
        throw "$Description must be local, not a UNC or network path: '$Value'."
    }

    return $fullPath
}

function Get-ByteSha256([byte[]] $Bytes) {
    [System.Security.Cryptography.SHA256] $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.Convert]::ToHexString($sha256.ComputeHash($Bytes))
    }
    finally {
        $sha256.Dispose()
    }
}

function Read-BoundedBytes([string] $Path, [int] $MaximumBytes, [string] $Description) {
    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    [System.IO.FileStream] $stream = [System.IO.File]::Open(
        $file.FullName,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        if ($stream.Length -gt $MaximumBytes) {
            throw "$Description '$Path' is $($stream.Length) bytes; the maximum is $MaximumBytes."
        }

        [byte[]] $bytes = [byte[]]::new([int] $stream.Length)
        [int] $read = 0
        while ($read -lt $bytes.Length) {
            [int] $count = $stream.Read($bytes, $read, $bytes.Length - $read)
            if ($count -eq 0) {
                throw "$Description '$Path' ended before its recorded length."
            }
            $read += $count
        }
        if ($stream.ReadByte() -ne -1) {
            throw "$Description '$Path' changed while it was being read."
        }

        return ,$bytes
    }
    finally {
        $stream.Dispose()
    }
}

function ConvertFrom-BoundedJsonBytes([byte[]] $Bytes, [string] $Path, [string] $Description) {
    try {
        [string] $json = $strictUtf8.GetString($Bytes)
        return $json | ConvertFrom-Json -Depth 32
    }
    catch {
        throw "$Description '$Path' is not valid bounded JSON: $($_.Exception.Message)"
    }
}

function Read-BoundedJson([string] $Path, [int] $MaximumBytes, [string] $Description) {
    [byte[]] $bytes = Read-BoundedBytes $Path $MaximumBytes $Description
    return ConvertFrom-BoundedJsonBytes $bytes $Path $Description
}

function Get-FileInventory([string] $Path, [switch] $Fresh) {
    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if (-not $file.Exists) {
        throw "Input file does not exist: '$Path'."
    }

    [object] $cached = $null
    if (-not $Fresh -and $fileInventoryCache.TryGetValue($file.FullName, [ref] $cached)) {
        return [ordered] @{
            path = $file.FullName
            byteLength = $cached.ByteLength
            sha256 = $cached.Sha256
        }
    }

    [System.IO.FileStream] $stream = [System.IO.File]::Open(
        $file.FullName,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    [System.Security.Cryptography.SHA256] $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        [long] $byteLength = $stream.Length
        [string] $hash = [System.Convert]::ToHexString($sha256.ComputeHash($stream))
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }

    if (-not $Fresh) {
        $fileInventoryCache[$file.FullName] = [pscustomobject] @{
            ByteLength = $byteLength
            Sha256 = $hash
        }
    }

    return [ordered] @{
        path = $file.FullName
        byteLength = $byteLength
        sha256 = $hash
    }
}

function Get-ManifestDependencies(
    [string] $ManifestPath,
    [string] $CaseId) {
    [object] $manifest = Read-BoundedJson $ManifestPath $maxManifestBytes 'Capture manifest'
    [object[]] $cases = @(Get-RequiredProperty $manifest 'cases' 'Capture manifest')
    if ($cases.Count -gt 128) {
        throw "Capture manifest has $($cases.Count) cases; analysis records support at most 128."
    }

    [System.Collections.Generic.List[object]] $dependencies = @()
    [bool] $selectedCaseFound = [string]::IsNullOrEmpty($CaseId)
    [System.Collections.Generic.HashSet[string]] $caseIds = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    [string] $manifestDirectory = Split-Path -Parent $ManifestPath
    foreach ($case in $cases) {
        [string] $id = [string] (Get-RequiredProperty $case 'id' 'Capture manifest case')
        if (-not (Test-RecordId $id) -or -not $caseIds.Add($id)) {
            throw "Capture manifest case id '$id' is invalid or duplicated."
        }

        if ($CaseId -and -not [string]::Equals($id, $CaseId, [StringComparison]::Ordinal)) {
            continue
        }

        $selectedCaseFound = $true
        [string] $trace = [string] $case.trace
        [string] $speedscope = [string] $case.speedscope
        [string] $selected = if (-not [string]::IsNullOrWhiteSpace($trace)) { $trace } else { $speedscope }
        if ([string]::IsNullOrWhiteSpace($selected)) {
            throw "Capture manifest case '$id' has no trace or speedscope path."
        }

        [string] $dependencyPath = Get-LocalFullPath $selected $manifestDirectory "Capture manifest case '$id' trace"
        [System.Collections.IDictionary] $inventory = Get-FileInventory $dependencyPath
        $inventory.Insert(0, 'caseId', $id)
        [string] $symbolsValue = [string] $case.symbolsDirectory
        if ($symbolsValue) {
            [string] $symbols = Get-LocalFullPath $symbolsValue $manifestDirectory "Capture manifest case '$id' symbolsDirectory"
            if (-not (Test-Path -LiteralPath $symbols -PathType Container)) {
                throw "Capture manifest case '$id' symbolsDirectory does not exist: '$symbols'."
            }
            $inventory['symbolsDirectory'] = $symbols
        }
        $dependencies.Add($inventory)
    }

    if (-not $selectedCaseFound) {
        throw "Capture manifest has no case with id '$CaseId'."
    }

    return $dependencies
}

function Resolve-Input([object] $InputObject, [string] $PlanDirectory) {
    [string] $id = [string] (Get-RequiredProperty $InputObject 'id' 'Plan input')
    if (-not (Test-RecordId $id)) {
        throw "Plan input id '$id' must match [A-Za-z0-9][A-Za-z0-9._-]{0,63}."
    }

    [string] $kind = [string] $InputObject.kind
    if ([string]::IsNullOrWhiteSpace($kind)) { $kind = 'trace' }
    if ($kind -notin @('trace', 'manifest')) {
        throw "Plan input '$id' kind '$kind' is invalid; use trace or manifest."
    }

    [string] $pathValue = [string] (Get-RequiredProperty $InputObject 'path' "Plan input '$id'")
    [string] $path = Get-LocalFullPath $pathValue $PlanDirectory "Plan input '$id'"
    [System.Collections.IDictionary] $inventory = Get-FileInventory $path
    $inventory.Insert(0, 'kind', $kind)
    $inventory.Insert(0, 'id', $id)

    [string] $caseId = [string] $InputObject.caseId
    if ($caseId -and (-not (Test-RecordId $caseId) -or $kind -ne 'manifest')) {
        throw "Plan input '$id' caseId requires kind manifest and a valid record id."
    }
    if ($caseId) { $inventory['caseId'] = $caseId }

    [string] $symbolsValue = [string] $InputObject.symbolsDirectory
    if ($symbolsValue) {
        [string] $symbols = Get-LocalFullPath $symbolsValue $PlanDirectory "Plan input '$id' symbolsDirectory"
        if (-not (Test-Path -LiteralPath $symbols -PathType Container)) {
            throw "Plan input '$id' symbolsDirectory does not exist: '$symbols'."
        }
        $inventory['symbolsDirectory'] = $symbols
    }

    $inventory['dependencies'] = if ($kind -eq 'manifest') {
        @(Get-ManifestDependencies $path $caseId)
    }
    else {
        @()
    }

    return [pscustomobject] @{
        Id = $id
        Kind = $kind
        Path = $path
        CaseId = $caseId
        Inventory = $inventory
    }
}

function Resolve-Query(
    [object] $Query,
    [System.Collections.Generic.Dictionary[string, object]] $Inputs) {
    [string] $id = [string] (Get-RequiredProperty $Query 'id' 'Plan query')
    if (-not (Test-RecordId $id)) {
        throw "Plan query id '$id' must match [A-Za-z0-9][A-Za-z0-9._-]{0,63}."
    }

    [string] $operation = [string] (Get-RequiredProperty $Query 'operation' "Plan query '$id'")
    if (-not $allowedOperations.Contains($operation)) {
        throw "Plan query '$id' operation '$operation' is not an allowed read-only JSON analysis."
    }

    [System.Collections.Generic.List[string]] $inputIds = @()
    [System.Collections.Generic.List[object]] $resolvedInputs = @()
    foreach ($inputIdValue in @(Get-RequiredProperty $Query 'inputIds' "Plan query '$id'")) {
        [string] $inputId = [string] $inputIdValue
        if (-not $Inputs.ContainsKey($inputId)) {
            throw "Plan query '$id' references unknown input '$inputId'."
        }
        $inputIds.Add($inputId)
        $resolvedInputs.Add($Inputs[$inputId])
    }

    [int] $requiredInputs = if ($operation -eq 'diff') { 2 } else { 1 }
    if ($inputIds.Count -ne $requiredInputs) {
        throw "Plan query '$id' operation '$operation' requires $requiredInputs input(s), not $($inputIds.Count)."
    }

    if ($operation -eq 'diff') {
        if ($resolvedInputs[0].Kind -cne $resolvedInputs[1].Kind) {
            throw "Plan query '$id' diff inputs must both be traces or both be manifests."
        }
        if ($resolvedInputs.Where({ $_.CaseId }).Count -gt 0) {
            throw "Plan query '$id' diff inputs cannot select individual manifest cases."
        }
    }
    elseif ($operation -eq 'batch') {
        if ($resolvedInputs[0].Kind -ne 'manifest' -or $resolvedInputs[0].CaseId) {
            throw "Plan query '$id' batch input must be a complete manifest without caseId."
        }
    }
    elseif ($operation -eq 'rank') {
        if ($resolvedInputs[0].Kind -eq 'manifest' -and -not $resolvedInputs[0].CaseId) {
            throw "Plan query '$id' rank input requires caseId when kind is manifest."
        }
    }
    elseif ($resolvedInputs[0].Kind -ne 'trace') {
        throw "Plan query '$id' operation '$operation' requires a trace input, not a manifest."
    }

    [System.Collections.Generic.List[string]] $arguments = @($operation)
    foreach ($inputIdValue in $inputIds) {
        [string] $inputId = [string] $inputIdValue
        [pscustomobject] $resolvedInput = $Inputs[$inputId]
        $arguments.Add($resolvedInput.Path)
    }

    [object[]] $suppliedArguments = @($Query.arguments)
    if ($suppliedArguments.Count -gt $maxArgumentsPerQuery) {
        throw "Plan query '$id' has $($suppliedArguments.Count) arguments; the maximum is $maxArgumentsPerQuery."
    }
    foreach ($argumentValue in $suppliedArguments) {
        if ($null -eq $argumentValue) {
            throw "Plan query '$id' has a null argument."
        }
        [string] $argument = [string] $argumentValue
        if ($argument.Length -gt $maxArgumentLength -or $argument.IndexOf([char] 0) -ge 0) {
            throw "Plan query '$id' has an invalid or oversized argument."
        }
        if ($argument -in @(
            '--format', '--case-id', '--output', '-o', '--help', '-h', '--version',
            '--symbols', '-s', '--native-symbols', '--symbol-cache') -or
            $argument.StartsWith('--format=', [StringComparison]::Ordinal) -or
            $argument.StartsWith('--case-id=', [StringComparison]::Ordinal) -or
            $argument.StartsWith('--output=', [StringComparison]::Ordinal) -or
            $argument.StartsWith('--symbols=', [StringComparison]::Ordinal) -or
            $argument.StartsWith('--symbol-cache=', [StringComparison]::Ordinal)) {
            throw "Plan query '$id' cannot supply reserved argument '$argument'."
        }
        $arguments.Add($argument)
    }

    if ($symbolOperations.Contains($operation)) {
        [string[]] $symbolDirectories = @(
            $resolvedInputs |
                ForEach-Object { [string] $_.Inventory.symbolsDirectory } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Sort-Object -Unique)
        if ($symbolDirectories.Count -gt 1) {
            throw "Plan query '$id' inputs resolve to different symbols directories; the operation accepts one."
        }
        if ($symbolDirectories.Count -eq 1) {
            $arguments.Add('--symbols')
            $arguments.Add($symbolDirectories[0])
        }
    }

    foreach ($inputIdValue in $inputIds) {
        [string] $inputId = [string] $inputIdValue
        [pscustomobject] $resolvedInput = $Inputs[$inputId]
        if ($resolvedInput.CaseId) {
            if ($operation -ne 'rank') {
                throw "Plan input '$inputId' caseId can only be used by the rank operation."
            }
            $arguments.Add('--case-id')
            $arguments.Add($resolvedInput.CaseId)
        }
    }
    $arguments.Add('--format')
    $arguments.Add('json')

    return [pscustomobject] @{
        Id = $id
        Operation = $operation
        InputIds = @($inputIds)
        Arguments = @($arguments)
    }
}

function Invoke-NativeProcess(
    [string] $Executable,
    [string[]] $Arguments,
    [int] $Timeout) {
    [System.Diagnostics.ProcessStartInfo] $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argumentValue in $Arguments) {
        $startInfo.ArgumentList.Add([string] $argumentValue)
    }

    [System.Diagnostics.Process] $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Failed to start '$Executable'."
        }

        [System.Threading.Tasks.Task[string]] $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        [System.Threading.Tasks.Task[string]] $stderrTask = $process.StandardError.ReadToEndAsync()
        [bool] $completed = $process.WaitForExit([int] [Math]::Min([long] $Timeout * 1000, [int]::MaxValue))
        if (-not $completed) {
            try { $process.Kill($true) } catch { }
            if (-not $process.WaitForExit(5000)) {
                throw "Timed-out process '$Executable' did not exit within 5 seconds after termination."
            }
        }

        return [pscustomobject] @{
            ExitCode = if ($completed) { $process.ExitCode } else { 124 }
            TimedOut = -not $completed
            Stdout = $stdoutTask.GetAwaiter().GetResult()
            Stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Limit-Utf8Text([string] $Text, [int] $MaximumBytes, [ref] $Truncated) {
    $Truncated.Value = $false
    if ($utf8.GetByteCount($Text) -le $MaximumBytes) { return $Text }

    [string] $marker = "`n[filtrace analysis record truncated stderr]`n"
    [int] $budget = $MaximumBytes - $utf8.GetByteCount($marker)
    if ($budget -le 0) {
        throw "Stderr limit $MaximumBytes is too small for the truncation marker."
    }
    [int] $low = 0
    [int] $high = $Text.Length
    while ($low -lt $high) {
        [int] $mid = [int] [Math]::Ceiling(($low + $high) / 2.0)
        if ($utf8.GetByteCount($Text.Substring(0, $mid)) -le $budget) { $low = $mid } else { $high = $mid - 1 }
    }

    $Truncated.Value = $true
    return $Text.Substring(0, $low) + $marker
}

function New-CaptureErrorJson([string] $Message) {
    [string] $json = ConvertTo-Json -Compress -InputObject ([ordered] @{
        captureError = $Message
    })
    if ($utf8.GetByteCount($json) -gt $maxStdoutBytes) {
        $json = '{"captureError":"Filtrace query failed; see stderr."}'
    }
    if ($utf8.GetByteCount($json) -gt $maxStdoutBytes) {
        throw "Stdout limit $maxStdoutBytes is too small for the capture-error envelope."
    }

    return $json
}

function Write-Utf8File([string] $Path, [string] $Text) {
    [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Write-RunRecord([string] $Path, [System.Collections.IDictionary] $Record) {
    [string] $json = ConvertTo-Json -InputObject $Record -Depth 16 -Compress
    if ($utf8.GetByteCount($json) -gt $maxRunRecordBytes) {
        throw "Run record exceeds the $maxRunRecordBytes-byte limit."
    }

    [string] $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Write-Utf8File $temporary $json
        [System.IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

function ConvertTo-ReplayFingerprint([object[]] $Inventories) {
    return @(
        foreach ($inventory in $Inventories) {
            [ordered] @{
                id = [string] $inventory.id
                kind = [string] $inventory.kind
                caseId = [string] $inventory.caseId
                byteLength = [long] $inventory.byteLength
                sha256 = [string] $inventory.sha256
                dependencies = @(
                    foreach ($dependency in @($inventory.dependencies)) {
                        [ordered] @{
                            caseId = [string] $dependency.caseId
                            byteLength = [long] $dependency.byteLength
                            sha256 = [string] $dependency.sha256
                        }
                    }
                )
            }
        }
    )
}

function Get-InputValidationError([object[]] $Inventories) {
    foreach ($inventory in $Inventories) {
        [System.Collections.IDictionary] $current = Get-FileInventory ([string] $inventory.path) -Fresh
        if ([long] $inventory.byteLength -ne [long] $current.byteLength -or
            [string] $inventory.sha256 -cne [string] $current.sha256) {
            return "Input changed during analysis: '$($inventory.path)'."
        }

        foreach ($dependency in @($inventory.dependencies)) {
            [System.Collections.IDictionary] $currentDependency = Get-FileInventory ([string] $dependency.path) -Fresh
            if ([long] $dependency.byteLength -ne [long] $currentDependency.byteLength -or
                [string] $dependency.sha256 -cne [string] $currentDependency.sha256) {
                return "Manifest dependency changed during analysis: '$($dependency.path)'."
            }
        }
    }

    return $null
}

[string] $planPath = (Get-Item -LiteralPath $Plan -ErrorAction Stop).FullName
[byte[]] $planBytes = Read-BoundedBytes $planPath $maxPlanBytes 'Analysis plan'
[string] $planSha256 = Get-ByteSha256 $planBytes
[object] $planObject = ConvertFrom-BoundedJsonBytes $planBytes $planPath 'Analysis plan'
if ([int] (Get-RequiredProperty $planObject 'schemaVersion' 'Analysis plan') -ne 1) {
    throw 'Analysis plan schemaVersion must be 1.'
}

[object[]] $planInputs = @(Get-RequiredProperty $planObject 'inputs' 'Analysis plan')
[object[]] $planQueries = @(Get-RequiredProperty $planObject 'queries' 'Analysis plan')
if ($planInputs.Count -lt 1 -or $planInputs.Count -gt $maxInputs) {
    throw "Analysis plan must contain 1-$maxInputs inputs."
}
if ($planQueries.Count -lt 1 -or $planQueries.Count -gt $maxQueries) {
    throw "Analysis plan must contain 1-$maxQueries queries."
}

[string] $planDirectory = Split-Path -Parent $planPath
[System.Collections.Generic.Dictionary[string, object]] $inputs = [System.Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::Ordinal)
[System.Collections.Generic.List[object]] $inputInventory = @()
foreach ($inputObject in $planInputs) {
    [pscustomobject] $resolvedInput = Resolve-Input $inputObject $planDirectory
    if (-not $inputs.TryAdd($resolvedInput.Id, $resolvedInput)) {
        throw "Analysis plan input id '$($resolvedInput.Id)' is duplicated."
    }
    $inputInventory.Add($resolvedInput.Inventory)
}

[System.Collections.Generic.List[object]] $queries = @()
[System.Collections.Generic.HashSet[string]] $queryIds = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($queryObject in $planQueries) {
    [pscustomobject] $query = Resolve-Query $queryObject $inputs
    if (-not $queryIds.Add($query.Id)) {
        throw "Analysis plan query id '$($query.Id)' is duplicated."
    }
    $queries.Add($query)
}

if ($ReplayFrom) {
    [string] $replayPath = Get-LocalFullPath $ReplayFrom (Get-Location).Path 'Replay record'
    [object] $previous = Read-BoundedJson $replayPath $maxReplayBytes 'Replay record'
    if ([string] $previous.planSha256 -cne $planSha256) {
        throw 'Replay plan hash differs from the prior analysis record.'
    }
    [string] $expectedInventory = ConvertTo-Json -InputObject @(ConvertTo-ReplayFingerprint @($previous.inputs)) -Depth 12 -Compress
    [string] $actualInventory = ConvertTo-Json -InputObject @(ConvertTo-ReplayFingerprint @($inputInventory)) -Depth 12 -Compress
    if ($actualInventory -cne $expectedInventory) {
        throw 'Replay input inventory differs from the prior analysis record; no query was run.'
    }
}

[System.Management.Automation.CommandInfo] $filtraceCommand = Get-Command $FiltracePath -CommandType Application -ErrorAction Stop
[string] $filtraceExecutable = $filtraceCommand.Source
[pscustomobject] $versionResult = Invoke-NativeProcess $filtraceExecutable @('--version') 30
[System.Text.RegularExpressions.Match] $versionMatch = [regex]::Match(
    $versionResult.Stdout,
    '(?<!\d)\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?')
if ($versionResult.ExitCode -ne 0 -or -not $versionMatch.Success) {
    throw "Filtrace --version failed or returned no semantic version (exit $($versionResult.ExitCode))."
}

[string] $outputPath = Get-LocalFullPath $OutputDirectory (Get-Location).Path 'OutputDirectory'
if (Test-Path -LiteralPath $outputPath) {
    throw "OutputDirectory already exists: '$outputPath'."
}
[void] [System.IO.Directory]::CreateDirectory($outputPath)
[System.IO.File]::WriteAllBytes((Join-Path $outputPath 'plan.json'), $planBytes)

[DateTimeOffset] $runStarted = [DateTimeOffset]::UtcNow
[System.Collections.Generic.List[object]] $queryRecords = @()
[System.Collections.IDictionary] $runRecord = [ordered] @{
    schemaVersion = 1
    status = 'running'
    planSha256 = $planSha256
    startedUtc = $runStarted.ToString('O')
    completedUtc = $null
    filtrace = [ordered] @{
        path = $filtraceExecutable
        version = $versionMatch.Value
    }
    host = [ordered] @{
        osDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        frameworkDescription = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        powerShellVersion = $PSVersionTable.PSVersion.ToString()
    }
    inputs = @($inputInventory)
    queries = $queryRecords
}
[string] $runRecordPath = Join-Path $outputPath 'run.json'
Write-RunRecord $runRecordPath $runRecord

[int] $finalExitCode = 0
for ([int] $index = 0; $index -lt $queries.Count; $index++) {
    [pscustomobject] $query = $queries[$index]
    [string] $prefix = '{0:D2}-{1}' -f ($index + 1), $query.Id
    [string] $stdoutName = "$prefix.stdout.json"
    [string] $stderrName = "$prefix.stderr.txt"
    [string] $stdoutPath = Join-Path $outputPath $stdoutName
    [string] $stderrPath = Join-Path $outputPath $stderrName
    [DateTimeOffset] $started = [DateTimeOffset]::UtcNow
    try {
        [pscustomobject] $execution = Invoke-NativeProcess $filtraceExecutable $query.Arguments $TimeoutSeconds
    }
    catch {
        $execution = [pscustomobject] @{
            ExitCode = 1
            TimedOut = $false
            Stdout = New-CaptureErrorJson (
                "Query '$($query.Id)' could not start '$filtraceExecutable': $($_.Exception.Message)")
            Stderr = $_.Exception.ToString()
        }
    }
    [DateTimeOffset] $completed = [DateTimeOffset]::UtcNow

    [string] $stdout = $execution.Stdout
    if ($utf8.GetByteCount($stdout) -gt $maxStdoutBytes) {
        $stdout = New-CaptureErrorJson (
            "Filtrace stdout exceeded the $maxStdoutBytes-byte analysis-record limit.")
        if ($execution.ExitCode -eq 0) { $execution.ExitCode = 1 }
    }
    else {
        try { $null = $stdout | ConvertFrom-Json -Depth 32 }
        catch {
            $stdout = New-CaptureErrorJson (
                "Filtrace stdout was not valid JSON: $($_.Exception.Message)")
            if ($execution.ExitCode -eq 0) { $execution.ExitCode = 1 }
        }
    }

    [bool] $stderrTruncated = $false
    [string] $stderr = Limit-Utf8Text $execution.Stderr $maxStderrBytes ([ref] $stderrTruncated)
    Write-Utf8File $stdoutPath $stdout
    Write-Utf8File $stderrPath $stderr

    [string] $queryStatus = if ($execution.TimedOut) {
        'timeout'
    }
    elseif ($execution.ExitCode -eq 0) {
        'completed'
    }
    elseif ($execution.ExitCode -eq 3) {
        'rejected'
    }
    else {
        'failed'
    }
    $queryRecords.Add([ordered] @{
        id = $query.Id
        operation = $query.Operation
        inputIds = @($query.InputIds)
        arguments = @($query.Arguments)
        status = $queryStatus
        startedUtc = $started.ToString('O')
        completedUtc = $completed.ToString('O')
        exitCode = $execution.ExitCode
        stdout = $stdoutName
        stdoutByteLength = (Get-Item -LiteralPath $stdoutPath).Length
        stdoutSha256 = (Get-FileHash -LiteralPath $stdoutPath -Algorithm SHA256).Hash
        stderr = $stderrName
        stderrByteLength = (Get-Item -LiteralPath $stderrPath).Length
        stderrSha256 = (Get-FileHash -LiteralPath $stderrPath -Algorithm SHA256).Hash
        stderrTruncated = $stderrTruncated
    })
    Write-RunRecord $runRecordPath $runRecord

    if ($execution.ExitCode -ne 0) {
        $finalExitCode = $execution.ExitCode
        break
    }
}

$inputValidationError = Get-InputValidationError @($inputInventory)
if ($inputValidationError) {
    $finalExitCode = 2
    $runRecord['inputValidationError'] = $inputValidationError
}
$runRecord.status = if ($finalExitCode -eq 0) {
    'completed'
}
elseif ($finalExitCode -eq 3) {
    'rejected'
}
else {
    'failed'
}
$runRecord.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
Write-RunRecord $runRecordPath $runRecord

Write-Host "Filtrace analysis record: $runRecordPath"
exit $finalExitCode