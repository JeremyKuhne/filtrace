<#
.SYNOPSIS
    Capture a .NET CPU trace (EventPipe or ETW) of a BenchmarkDotNet benchmark, then
    print the filtrace commands to analyze it. This helper drives the capture step via
    BenchmarkDotNet's EventPipe or ETW profiler.

.DESCRIPTION
    Wraps the "record a trace, then analyze it" loop for a BenchmarkDotNet perf
    project. Run it from the repository root. Each invocation passes a run-specific
    `--artifacts` directory and `--keepFiles`, enumerates every profiler output in
    that run, and emits a durable manifest with parameterized benchmark identity,
    trace pairs, runtime/source identity, and exact symbols when verified:

      - EventPipe (-Profiler EP, the default): cross-platform, no elevation, single
        process. BenchmarkDotNet normally writes a raw .nettrace and a paired derived
        .speedscope.json. The manifest retains both; analysis commands use the raw
        trace when present, or limit a speedscope-only case to CPU/export commands.
      - ETW (-Profiler ETW): Windows only, self-elevates (one UAC prompt), machine
        wide. Uses `-p ETW --keepFiles`, which writes a .etl. Only an
        .etl carries wall-clock (threadtime), the native GC / JIT / memcpy split
        (classify --native-symbols), and multi-process scoping.

    Full BenchmarkDotNet output is written only to capture.log. The final Text or Json
    handoff contains the manifest path, warnings, and commands only for analyses whose
    captureStatus is known-enabled. Enabled-zero remains actionable; disabled and
    unknown analyses are explained instead of receiving commands. Quiet Text mode
    emits warnings only.

    Every invocation writes BenchmarkDotNet output and capture.log under a unique
    <OutputDirectory>/<RunId> directory, then emits manifest.json with every
    parameterized capture case. OutputDirectory defaults to
    BenchmarkDotNet.Artifacts/filtrace-runs under the current directory. A
    same-project/same-TFM handle lock rejects overlap before any build starts. Logged
    child OutDir paths are verified with filtrace info; source commands are printed
    only when an exact PDB maps sampled frames. No globally newest artifact is selected.

    filtrace: https://github.com/JeremyKuhne/filtrace - install once with
    `dotnet tool install -g KlutzyNinja.Filtrace`, or drive the MCP trace_* tools.

.PARAMETER Project
    Path to the perf project - a .csproj or the directory holding one. Required.

.PARAMETER Filter
    BenchmarkDotNet --filter glob selecting the benchmark(s), e.g. '*GlobMatchBench*'.
    Profile one at a time for a clean trace. Required.

.PARAMETER Profiler
    'EP' (EventPipe, default) or 'ETW' (Windows, self-elevating).

.PARAMETER Tfm
    Target-framework moniker to run. Default net10.0.

.PARAMETER Process
    Process-name substring the printed ETW commands scope to with --process.
    Defaults to the project file's base name (the benchmark host).

.PARAMETER Top
    Rows per ranking in the printed commands. Default 25.

.PARAMETER OperationCount
    Optional positive operation count represented by each captured case. Specify
    together with OperationUnit to enable per-operation manifest comparison.

.PARAMETER OperationUnit
    Optional operation unit (for example items or requests). Specify together with
    OperationCount. Both fields are omitted when neither parameter is supplied.

.PARAMETER ElevatedTimeoutSeconds
    How long the non-elevated parent waits for the self-elevated ETW capture to finish
    before it stops blocking and reports the capture.log path. Default 1200 (20 minutes). Only
    the ETW self-elevation path uses it - it is the backstop that keeps a never-signaled
    elevated child from hanging the parent indefinitely.

.PARAMETER OutputDirectory
    Parent directory for run-specific artifacts, logs, launchers, traces, and
    manifests. Relative paths are resolved against the current directory. The
    directory is created during preflight. Defaults to
    BenchmarkDotNet.Artifacts/filtrace-runs under the current directory.

.PARAMETER Prepare
    Perform non-elevated ETW preflight and write a syntax-validated launcher beneath
    OutputDirectory without starting elevation. The result includes the launcher path
    and expected manifest path. Run the launcher as one explicit user action in a host
    that permits UAC.

.PARAMETER RunId
    Optional stable identifier for this capture run. Defaults to a UTC timestamp plus
    a random suffix. The run's BenchmarkDotNet artifacts and capture log are written
    under <OutputDirectory>/<RunId>.

.PARAMETER DotnetPath
    Path or command name for the dotnet host. Defaults to dotnet from PATH.

.PARAMETER FiltracePath
    Path or command name for filtrace. When it resolves, the helper verifies version
    0.6.0 or newer and the fields introduced by info JSON schema 8 before capture,
    then uses it to verify which logged BenchmarkDotNet child output has an exact PDB
    match for each trace. Newer envelope schemas are accepted when those fields remain
    present. When filtrace does not resolve, recorder-established analysis statuses
    remain the fallback.

.PARAMETER Format
    Final result format: Text (default) or Json. BenchmarkDotNet output always stays
    in capture.log.

.PARAMETER Quiet
    Suppress informational progress in Text mode. Warnings and errors still surface.

.PARAMETER ElevatedChild
    Internal switch reserved for the self-elevated ETW child process. Do not pass it
    directly; non-ETW or non-elevated use is rejected.

.PARAMETER PreparedLauncher
    Internal path identifying the validated launcher that initiated this capture. Do
    not pass it directly; it must match the canonical launcher for RunId.

.PARAMETER ReservationToken
    Internal token identifying the atomic run reservation shared by the parent,
    prepared launcher, and elevated child. Do not pass it directly.

.EXAMPLE
    ./Capture-BenchmarkTrace.ps1 -Project src/App.Perf -Filter '*GlobMatchBench*'

.EXAMPLE
    ./Capture-BenchmarkTrace.ps1 -Project src/App.Perf -Filter '*GlobMatchBench*' -Profiler ETW

.EXAMPLE
    ./Capture-BenchmarkTrace.ps1 -Project src/App.Perf -Filter '*GlobMatchBench*' -Profiler ETW -OutputDirectory artifacts/filtrace -Prepare
#>
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })][string]$Filter,
    [ValidateSet('EP', 'ETW')][string]$Profiler = 'EP',
    [string]$Tfm = 'net10.0',
    [string]$Process,
    [int]$Top = 25,
    [ValidateScript({ $_ -gt 0 -and -not [double]::IsNaN($_) -and -not [double]::IsInfinity($_) })]
    [double]$OperationCount,
    [ValidateLength(1, 64)][string]$OperationUnit,
    [ValidateRange(1, 2147483647)][int]$ElevatedTimeoutSeconds = 1200,
    [string]$OutputDirectory,
    [switch]$Prepare,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')][string]$RunId,
    [string]$DotnetPath = 'dotnet',
    [string]$FiltracePath = 'filtrace',
    [ValidateSet('Text', 'Json')][string]$Format = 'Text',
    [switch]$Quiet,
    [switch]$ElevatedChild,
    [string]$PreparedLauncher,
    [ValidatePattern('^[0-9a-f]{32}$')][string]$ReservationToken
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$showProgress = $Format -eq 'Text' -and -not $Quiet

function Write-CaptureMetadata([string]$TracePath, [System.Collections.IDictionary]$Analyses) {
    $metadata = [ordered]@{
        schemaVersion = 1
        analyses = $Analyses
    } | ConvertTo-Json -Depth 3 -Compress
    $encoding = New-Object System.Text.UTF8Encoding($false)
    try {
        [System.IO.File]::WriteAllText("$TracePath.filtrace.json", $metadata, $encoding)
    }
    catch {
        Write-Warning "Capture succeeded, but metadata could not be written: $($_.Exception.Message). Provider enablement will be unknown during analysis."
    }
}

function Write-RunManifest([string]$Path, [System.Collections.IDictionary]$Manifest) {
    $maxManifestBytes = 16MB
    try {
        $json = $Manifest | ConvertTo-Json -Depth 8 -Compress
    }
    catch {
        throw "Capture manifest could not be serialized at '$Path': $($_.Exception.Message)"
    }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $manifestBytes = $encoding.GetByteCount($json)
    if ($manifestBytes -ge $maxManifestBytes) {
        throw "Capture manifest is $manifestBytes UTF-8 bytes; the durable manifest safety limit is 16 MiB. Narrow the benchmark filter or split the capture into fewer cases."
    }

    [System.IO.File]::WriteAllText($Path, $json, $encoding)
}

function Write-RunManifestAtomically([string]$Path, [System.Collections.IDictionary]$Manifest) {
    $directory = Split-Path -Parent $Path
    $temporaryPath = Join-Path $directory ".$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Write-RunManifest $temporaryPath $Manifest
        [System.IO.File]::Move($temporaryPath, $Path)
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Write-FailureResult(
    [string]$Path,
    [string]$LogPath,
    [string]$CaptureRunId,
    [string]$Message,
    [int]$ExitCode) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::AppendAllText(
        $LogPath,
        "ERROR: $Message$([Environment]::NewLine)",
        $encoding)
    Write-RunManifest $Path ([ordered]@{
        schemaVersion = 1
        status = 'failure'
        runId = $CaptureRunId
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        exitCode = $ExitCode
        message = $Message
        log = $LogPath
    })
}

function New-RunReservation([string]$Path, [string]$CaptureRunId) {
    $token = [Guid]::NewGuid().ToString('N')
    $json = [ordered]@{
        schemaVersion = 1
        runId = $CaptureRunId
        state = 'reserved'
        token = $token
        processId = $PID
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Compress
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $bytes = $encoding.GetBytes($json)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }
    finally {
        $stream.Dispose()
    }
    return $token
}

function Test-RunReservation(
    [string]$Path,
    [string]$CaptureRunId,
    [string]$Token) {
    try {
        $reservation = Read-BoundedUtf8File $Path 16KB | ConvertFrom-Json
        return $reservation.schemaVersion -eq 1 -and
            $reservation.state -eq 'reserved' -and
            [string]$reservation.runId -ceq $CaptureRunId -and
            [string]$reservation.token -ceq $Token
    }
    catch {
        return $false
    }
}

function ConvertTo-RuntimeSummary([string]$LogLine) {
    # Strip Get-Content's provider ETS properties; the 5.1 JSON serializer
    # otherwise recurses through PSProvider and can exhaust memory.
    $line = [string]::new($LogLine.ToCharArray()).Trim()
    if ($line -match '^(?://\s*)?Runtime\s*=\s*(.+)$') {
        return "Runtime = $($Matches[1].Trim())"
    }

    return $null
}

function Get-RuntimeSummaries([string]$LogPath) {
    $finalSummaries = New-Object 'System.Collections.Generic.List[string]'
    $caseSummaries = New-Object 'System.Collections.Generic.List[string]'
    foreach ($logLine in Get-Content -LiteralPath $LogPath) {
        if ($logLine -match '^\s*Runtime\s+=\s*(.+)$') {
            $finalSummaries.Add((ConvertTo-RuntimeSummary $logLine))
        }
        elseif ($logLine -match '^\s*//\s*Runtime\s*=\s*(.+)$') {
            $caseSummaries.Add((ConvertTo-RuntimeSummary $logLine))
        }
    }

    # Final report rows carry configuration details such as GC mode. Replace only
    # the per-case identity they enrich; preserve unmatched per-case rows when a
    # failed or partial multi-runtime run omitted its final report row.
    $summaries = New-Object 'System.Collections.Generic.List[string]'
    foreach ($finalSummary in $finalSummaries | Sort-Object -Unique) {
        $summaries.Add($finalSummary)
    }
    foreach ($caseSummary in $caseSummaries | Sort-Object -Unique) {
        $coveredByFinalSummary = $false
        foreach ($finalSummary in $finalSummaries) {
            if ($finalSummary -eq $caseSummary -or
                $finalSummary.StartsWith("$caseSummary; ", [StringComparison]::Ordinal)) {
                $coveredByFinalSummary = $true
                break
            }
        }
        if (-not $coveredByFinalSummary) {
            $summaries.Add($caseSummary)
        }
    }

    return @($summaries | Sort-Object -Unique)
}

function Get-CaptureCases([string]$ArtifactsDirectory, [string]$CaptureProfiler) {
    $maxCases = 256
    $casesByStem = @{}
    foreach ($file in Get-ChildItem -LiteralPath $ArtifactsDirectory -Recurse -File -ErrorAction SilentlyContinue) {
        $kind = $null
        $stem = $null
        if ($file.Name -like '*.speedscope.json') {
            $kind = 'speedscope'
            $stem = $file.Name -replace '\.speedscope\.json$', ''
        }
        elseif ($CaptureProfiler -eq 'ETW' -and $file.Extension -eq '.etl') {
            $kind = 'trace'
            $stem = $file.BaseName
        }
        elseif ($CaptureProfiler -eq 'EP' -and $file.Extension -eq '.nettrace') {
            $kind = 'trace'
            $stem = $file.BaseName
        }
        else {
            continue
        }

        if (-not $casesByStem.ContainsKey($stem)) {
            if ($casesByStem.Count -ge $maxCases) {
                throw "Capture produced more than $maxCases cases; narrow the benchmark filter."
            }

            $casesByStem[$stem] = [ordered]@{
                id = $stem
                benchmarkId = $null
                benchmark = $null
                parameters = $null
                benchmarkDisplay = $null
                runtime = $null
                capturedUtc = $file.LastWriteTimeUtc.ToString('O')
                trace = $null
                speedscope = $null
                symbolsDirectory = $null
                operationCount = $null
                operationUnit = $null
                symbolCandidates = @()
                analyses = [ordered]@{}
                commands = @()
                warnings = @()
            }
        }

        $casesByStem[$stem][$kind] = $file.FullName
        if ($kind -eq 'trace') {
            $casesByStem[$stem].capturedUtc = $file.LastWriteTimeUtc.ToString('O')
        }
    }

    return @($casesByStem.Values | Sort-Object { $_.capturedUtc }, { $_.id })
}

function Get-SymbolCandidates([string]$CaptureLog, [string]$OuterSymbolsDirectory) {
    $maxCandidates = 32
    $candidates = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $outerCandidate = Get-LocalDirectoryCandidate $OuterSymbolsDirectory
    if ($outerCandidate) {
        [void]$candidates.Add($outerCandidate)
    }

    :captureLog foreach ($line in Get-Content -LiteralPath $CaptureLog) {
        foreach ($match in [regex]::Matches($line, '/p:OutDir="([^"]+)"')) {
            $directory = Get-LocalDirectoryCandidate $match.Groups[1].Value
            if ($directory) {
                [void]$candidates.Add($directory)
                if ($candidates.Count -ge $maxCandidates) { break captureLog }
            }
        }
    }

    return @($candidates | Sort-Object)
}

function Get-LocalDirectoryCandidate([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.StartsWith('\\', [StringComparison]::Ordinal) -or
        $Path.StartsWith('//', [StringComparison]::Ordinal)) {
        return $null
    }

    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal) -or
            $fullPath.StartsWith('//', [StringComparison]::Ordinal) -or
            -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
            return $null
        }

        return $fullPath
    }
    catch {
        return $null
    }
}

function Set-BenchmarkIdentities(
    [System.Collections.IDictionary[]]$CaptureCases,
    [string]$CaptureLog,
    [string]$FiltraceCommand,
    [bool]$CanInspectTraces) {
    $benchmarksInExecutionOrder = New-Object 'System.Collections.Generic.List[System.Collections.IDictionary]'
    $currentDisplay = $null
    $pendingBenchmark = $null
    foreach ($line in Get-Content -LiteralPath $CaptureLog) {
        if ($line -match '^// Benchmark: (.+)$') {
            $currentDisplay = $Matches[1]
            $pendingBenchmark = $null
            continue
        }

        # Comment-prefixed runtime rows belong to the active Execute block. Spaced
        # unprefixed `Runtime =` rows are final summaries handled manifest-wide;
        # compact `Runtime=` rows are job characteristics and are not summaries.
        if ($null -ne $pendingBenchmark -and $line -match '^\s*//\s*Runtime\s*=') {
            $pendingBenchmark.runtime = ConvertTo-RuntimeSummary $line
            $pendingBenchmark = $null
            continue
        }

        if ($null -ne $currentDisplay -and
            $line -match '--benchmarkName\s+(.+?)(?=\s+--[A-Za-z])') {
            $benchmarkNameArgument = $Matches[1]
            if ($line -notmatch '--benchmarkId\s+(\d+)') { continue }
            $benchmarkId = [int]$Matches[1]
            $fullBenchmarkName = ConvertFrom-BenchmarkNameArgumentFull $benchmarkNameArgument
            $benchmark = [ordered]@{
                benchmarkId = $benchmarkId
                benchmark = Get-BenchmarkName $fullBenchmarkName
                fullBenchmarkName = $fullBenchmarkName
                parameters = Get-BenchmarkParameters $currentDisplay
                benchmarkDisplay = $currentDisplay
                runtime = $null
                assigned = $false
            }
            $benchmarksInExecutionOrder.Add($benchmark)
            $pendingBenchmark = $benchmark
        }
    }

    if ($CanInspectTraces) {
        foreach ($captureCase in $CaptureCases) {
            if (-not $captureCase.trace) { continue }
            $traceBenchmarkName = Get-TraceBenchmarkName $captureCase.trace $FiltraceCommand
            if ([string]::IsNullOrWhiteSpace($traceBenchmarkName)) { continue }
            $matches = @(
                $benchmarksInExecutionOrder |
                    Where-Object {
                        -not $_.assigned -and
                        $_.fullBenchmarkName -ceq $traceBenchmarkName
                    }
            )
            if ($matches.Count -ne 1) { continue }
            Set-CaptureCaseIdentity $captureCase $matches[0]
            $matches[0].assigned = $true
        }
    }

    $benchmarkNames = @(
        $benchmarksInExecutionOrder.benchmark |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
    )
    foreach ($benchmarkName in $benchmarkNames) {
        $benchmarks = @(
            $benchmarksInExecutionOrder |
                Where-Object { -not $_.assigned -and $_.benchmark -eq $benchmarkName }
        )
        $cases = @(
            $CaptureCases |
                Where-Object {
                    $null -eq $_.benchmarkId -and
                    $_.id -notmatch '-hash\d+(?:-|$)' -and
                    ($_.id.StartsWith("$benchmarkName-", [StringComparison]::Ordinal) -or
                        $_.id.StartsWith("$benchmarkName(", [StringComparison]::Ordinal))
                } |
                Sort-Object { $_.capturedUtc }, { $_.id }
        )
        if ($benchmarks.Count -ne $cases.Count) { continue }

        # Parameter values are not encoded in profiler filenames. BenchmarkDotNet
        # executes cases sequentially, so within one exact benchmark name use logged
        # execution order only when each completed trace has a distinct timestamp.
        # Otherwise leave identity null rather than silently mis-pair parameters.
        if ($cases.Count -gt 1 -and @($cases.capturedUtc | Select-Object -Unique).Count -ne $cases.Count) {
            continue
        }

        for ($index = 0; $index -lt $cases.Count; $index++) {
            Set-CaptureCaseIdentity $cases[$index] $benchmarks[$index]
            $benchmarks[$index].assigned = $true
        }
    }
}

function Set-CaptureCaseIdentity(
    [System.Collections.IDictionary]$CaptureCase,
    [System.Collections.IDictionary]$Benchmark) {
    $CaptureCase.benchmarkId = $Benchmark.benchmarkId
    $CaptureCase.benchmark = $Benchmark.benchmark
    $CaptureCase.parameters = $Benchmark.parameters
    $CaptureCase.benchmarkDisplay = $Benchmark.benchmarkDisplay
    $CaptureCase.runtime = $Benchmark.runtime
}

function Get-TraceBenchmarkName([string]$TracePath, [string]$FiltraceCommand) {
    try {
        $global:LASTEXITCODE = 0
        $json = & $FiltraceCommand events $TracePath `
            --name 'BenchmarkDotNet.EngineEventSource/Benchmark/Start' `
            --take 2 --max-payload 4096 --format json 2>$null | Out-String
        if ($LASTEXITCODE -ne 0) { return $null }
        $result = ($json | ConvertFrom-Json).result
        $events = @(
            $result.events |
                Where-Object {
                    $_.provider -ceq 'BenchmarkDotNet.EngineEventSource' -and
                    $_.eventName -ceq 'Benchmark/Start'
                }
        )
            if ([int]$result.totalMatched -ne 1 -or $events.Count -ne 1) { return $null }
        $payload = [string]$events[0].payload
        $prefix = 'benchmarkName='
        if (-not $payload.StartsWith($prefix, [StringComparison]::Ordinal)) { return $null }
        return $payload.Substring($prefix.Length)
    }
    catch {
        return $null
    }
}

function ConvertFrom-BenchmarkNameArgumentFull([string]$BenchmarkNameArgument) {
    if ([string]::IsNullOrWhiteSpace($BenchmarkNameArgument)) { return $null }

    $decoded = [regex]::Replace(
        $BenchmarkNameArgument.Trim(),
        '(?:\\+u0026#34;|&#34;|&quot;)',
        '"').Trim('"').Replace('\"', '"')
    if ([string]::IsNullOrWhiteSpace($decoded)) { return $null }
    return $decoded
}

function ConvertFrom-BenchmarkNameArgument([string]$BenchmarkNameArgument) {
    return Get-BenchmarkName (ConvertFrom-BenchmarkNameArgumentFull $BenchmarkNameArgument)
}

function Get-BenchmarkName([string]$FullBenchmarkName) {
    if ([string]::IsNullOrWhiteSpace($FullBenchmarkName)) { return $null }
    $parameters = $FullBenchmarkName.IndexOf('(')
    if ($parameters -ge 0) {
        return $FullBenchmarkName.Substring(0, $parameters)
    }
    return $FullBenchmarkName
}

function Get-BenchmarkParameters([string]$BenchmarkDisplay) {
    $trimmedDisplay = $BenchmarkDisplay.TrimEnd()
    if ($trimmedDisplay.EndsWith(']', [StringComparison]::Ordinal)) {
        $openBracket = $trimmedDisplay.LastIndexOf('[')
        if ($openBracket -ge 0) {
            return $trimmedDisplay.Substring(
                $openBracket + 1,
                $trimmedDisplay.Length - $openBracket - 2)
        }
    }

    $close = $BenchmarkDisplay.LastIndexOf('): ', [StringComparison]::Ordinal)
    if ($close -lt 0) { return '' }
    $open = $BenchmarkDisplay.IndexOf('(')
    if ($open -ge 0 -and $open -lt $close) {
        return $BenchmarkDisplay.Substring($open + 1, $close - $open - 1)
    }

    return ''
}

function Get-SourceIdentity([string]$ProjectDirectory) {
    if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    try {
        $repository = & git -C $ProjectDirectory rev-parse --show-toplevel 2>$null | Select-Object -First 1
        $commit = & git -C $ProjectDirectory rev-parse HEAD 2>$null | Select-Object -First 1
        if ($LASTEXITCODE -ne 0 -or -not $repository -or -not $commit) { return $null }
        return [ordered]@{
            repository = [System.IO.Path]::GetFullPath($repository)
            commit = $commit
        }
    }
    catch {
        return $null
    }
}

function Get-DefaultCaptureStatuses([string]$CaptureProfiler, [bool]$HasRawTrace) {
    if (-not $HasRawTrace) {
        return [ordered]@{ cpu = 'enabled' }
    }

    if ($CaptureProfiler -eq 'ETW') {
        return [ordered]@{
            cpu = 'enabled'; threadtime = 'enabled'; classify = 'enabled';
            processes = 'enabled'; diskio = 'disabled'; events = 'enabled'
        }
    }

    return [ordered]@{
        cpu = 'enabled'; alloc = 'disabled'; exceptions = 'enabled';
        contention = 'enabled'; wait = 'disabled'; activity = 'unknown';
        gcstats = 'enabled'; jitstats = 'enabled'; threadpool = 'enabled';
        events = 'enabled'
    }
}

function ConvertTo-CaptureStatus($Status) {
    switch ([string]$Status) {
        'enabled' { return 'enabled' }
        'disabled' { return 'disabled' }
        'unknown' { return 'unknown' }
        default { return 'unknown' }
    }
}

function Test-HasAnalysisInfo($TraceInfo) {
    return $null -ne $TraceInfo -and
        $null -ne $TraceInfo.analyses -and
        @($TraceInfo.analyses.PSObject.Properties).Count -gt 0
}

function ConvertTo-AnalysisMap(
    $TraceInfo,
    [System.Collections.IDictionary]$CaptureStatuses,
    [bool]$AllowRecorderFallback) {
    $analyses = [ordered]@{}
    if (Test-HasAnalysisInfo $TraceInfo) {
        foreach ($property in $TraceInfo.analyses.PSObject.Properties) {
            $analyses[$property.Name] = [ordered]@{
                captureStatus = ConvertTo-CaptureStatus $property.Value.captureStatus
                eventCount = $property.Value.eventCount
            }
        }
        return $analyses
    }

    foreach ($name in $CaptureStatuses.Keys) {
        $status = if ($AllowRecorderFallback) {
            ConvertTo-CaptureStatus $CaptureStatuses[$name]
        }
        else {
            'unknown'
        }
        $analyses[$name] = [ordered]@{
            captureStatus = $status
            eventCount = $null
        }
    }
    return $analyses
}

function Get-TraceInfoResult(
    [string]$TracePath,
    [string]$SymbolsDirectory,
    [string]$FiltraceCommand) {
    try {
        $arguments = @('info', $TracePath, '--format', 'json')
        if ($SymbolsDirectory) { $arguments += @('--symbols', $SymbolsDirectory) }
        $json = & $FiltraceCommand @arguments 2>$null | Out-String
        if ($LASTEXITCODE -ne 0) { return $null }
        return ($json | ConvertFrom-Json).result
    }
    catch {
        return $null
    }
}

function Get-ObjectPropertyInfo($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    return $Object.PSObject.Properties[$Name]
}

function Assert-FiltraceCompatibility([string]$FiltraceCommand) {
    $minimumVersion = [Version]'0.6.0'
    $minimumSchemaVersion = 8
    $upgradeGuidance = 'Upgrade with: dotnet tool update -g KlutzyNinja.Filtrace'

    try {
        $global:LASTEXITCODE = 0
        $versionOutput = (& $FiltraceCommand --version 2>$null | Out-String).Trim()
        $versionExitCode = $LASTEXITCODE
        $versionMatch = [regex]::Match($versionOutput, '(?<!\d)(\d+\.\d+\.\d+)')
        if ($versionExitCode -ne 0 -or -not $versionMatch.Success) {
            throw 'the --version query did not return a semantic version'
        }

        $resolvedVersion = [Version]$versionMatch.Groups[1].Value
        if ($resolvedVersion -lt $minimumVersion) {
            throw "version $resolvedVersion is older than required version $minimumVersion"
        }
    }
    catch {
        throw "Resolved filtrace '$FiltraceCommand' is incompatible: $($_.Exception.Message). $upgradeGuidance"
    }

    $preflightTrace = Join-Path (
        [System.IO.Path]::GetTempPath()) "filtrace-preflight-$([Guid]::NewGuid().ToString('N')).speedscope.json"
    try {
        $profile = [ordered]@{
            '$schema' = 'https://www.speedscope.app/file-format-schema.json'
            shared = [ordered]@{ frames = @([ordered]@{ name = 'preflight' }) }
            profiles = @(
                [ordered]@{
                    type = 'sampled'
                    name = 'preflight'
                    unit = 'milliseconds'
                    startValue = 0
                    endValue = 1
                    samples = ,([int[]]@(0))
                    weights = @(1)
                }
            )
            activeProfileIndex = 0
            exporter = 'filtrace compatibility preflight'
            name = 'filtrace compatibility preflight'
        } | ConvertTo-Json -Depth 6 -Compress
        $encoding = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($preflightTrace, $profile, $encoding)

        $global:LASTEXITCODE = 0
        $infoJson = & $FiltraceCommand info $preflightTrace --format json 2>$null | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw 'info --format json returned a nonzero exit code'
        }

        $infoEnvelope = $infoJson | ConvertFrom-Json
        $schemaVersionProperty = Get-ObjectPropertyInfo $infoEnvelope 'schemaVersion'
        $resultProperty = Get-ObjectPropertyInfo $infoEnvelope 'result'
        $infoResult = $null
        if ($null -ne $resultProperty) { $infoResult = $resultProperty.Value }
        $analysesProperty = Get-ObjectPropertyInfo $infoResult 'analyses'
        $analyses = $null
        if ($null -ne $analysesProperty) { $analyses = $analysesProperty.Value }
        $cpuProperty = Get-ObjectPropertyInfo $analyses 'cpu'
        $cpuAnalysis = $null
        if ($null -ne $cpuProperty) { $cpuAnalysis = $cpuProperty.Value }
        $captureStatusProperty = Get-ObjectPropertyInfo $cpuAnalysis 'captureStatus'
        $eventCountProperty = Get-ObjectPropertyInfo $cpuAnalysis 'eventCount'
        if ($null -eq $schemaVersionProperty -or
            [int]$schemaVersionProperty.Value -lt $minimumSchemaVersion -or
            $null -eq $resultProperty -or
            $null -eq $analysesProperty -or
            $null -eq $cpuProperty -or
            $null -eq $captureStatusProperty -or
            $null -eq $eventCountProperty) {
            throw "info --format json did not match schema $minimumSchemaVersion or newer"
        }
    }
    catch {
        throw "Resolved filtrace '$FiltraceCommand' is incompatible: $($_.Exception.Message). $upgradeGuidance"
    }
    finally {
        Remove-Item -LiteralPath $preflightTrace -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-MsbuildQuery(
    [string]$ProjectPath,
    [string]$DotnetCommand,
    [string[]]$Arguments) {
    $global:LASTEXITCODE = 0
    $output = & $DotnetCommand msbuild $ProjectPath -nologo @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        $detail = $output.Trim()
        if ($detail.Length -gt 2048) { $detail = $detail.Substring(0, 2048) }
        throw "Project evaluation failed for '$ProjectPath' (exit $LASTEXITCODE): $detail"
    }

    try {
        return $output | ConvertFrom-Json
    }
    catch {
        throw "Project evaluation for '$ProjectPath' did not return valid JSON: $($_.Exception.Message)"
    }
}

function Assert-ProjectPreflight(
    [string]$ProjectPath,
    [string]$TargetFramework,
    [string]$CaptureProfiler,
    [string]$DotnetCommand) {
    $properties = Invoke-MsbuildQuery $ProjectPath $DotnetCommand @(
        '-property:Configuration=Release',
        '-getProperty:TargetFramework,TargetFrameworks')
    $declaredFrameworks = if (-not [string]::IsNullOrWhiteSpace([string]$properties.Properties.TargetFrameworks)) {
        @([string]$properties.Properties.TargetFrameworks -split ';' | Where-Object { $_ })
    }
    elseif (-not [string]::IsNullOrWhiteSpace([string]$properties.Properties.TargetFramework)) {
        @([string]$properties.Properties.TargetFramework)
    }
    else {
        @()
    }
    if ($declaredFrameworks.Count -eq 0) {
        throw "Project '$ProjectPath' does not declare TargetFramework or TargetFrameworks."
    }
    if ($TargetFramework -notin $declaredFrameworks) {
        throw "Project '$ProjectPath' does not target '$TargetFramework'. Declared target frameworks: $($declaredFrameworks -join ', ')."
    }

    if ($CaptureProfiler -ne 'ETW') { return }

    $items = Invoke-MsbuildQuery $ProjectPath $DotnetCommand @(
        '-property:Configuration=Release',
        "-property:TargetFramework=$TargetFramework",
        '-getItem:PackageReference')
    $windowsDiagnosticsPackage = @(
        $items.Items.PackageReference |
            Where-Object { $_.Identity -eq 'BenchmarkDotNet.Diagnostics.Windows' }
    )
    if ($windowsDiagnosticsPackage.Count -eq 0) {
        throw "Project '$ProjectPath' does not include the evaluated PackageReference 'BenchmarkDotNet.Diagnostics.Windows' for target framework '$TargetFramework'. Add <PackageReference Include=`"BenchmarkDotNet.Diagnostics.Windows`" /> to the project or an imported props file. Central package management supplies the version only; it does not include the package."
    }
}

function Find-ExactSymbolDirectory([string]$TracePath, [string[]]$Candidates, [string]$FiltraceCommand) {
    $bestDirectory = $null
    $bestMappedFrames = 0
    foreach ($candidate in $Candidates) {
        $traceInfo = Get-TraceInfoResult $TracePath $candidate $FiltraceCommand
        $source = if ($null -ne $traceInfo) { $traceInfo.sourceResolution } else { $null }
        if ($null -eq $source -or $source.matchingPdbModules.Count -eq 0) { continue }
        $mappedFrames = [int]$source.mappedManagedFrameCount
        if ($mappedFrames -gt $bestMappedFrames) {
            $bestMappedFrames = $mappedFrames
            $bestDirectory = $candidate
        }
    }

    return $bestDirectory
}

function ConvertTo-PowerShellArgument([string]$Value) {
    return "'$($Value.Replace("'", "''"))'"
}

function Write-PreparationResult(
    [string]$LauncherPath,
    [string]$ManifestPath,
    [string]$CaptureRunId,
    [string]$OutputFormat) {
    if ($OutputFormat -eq 'Json') {
        $result = [ordered]@{
            schemaVersion = 1
            status = 'prepared'
            runId = $CaptureRunId
            launcher = $LauncherPath
            manifest = $ManifestPath
        }
        $encoding = New-Object System.Text.UTF8Encoding($false)
        $json = $result | ConvertTo-Json -Compress
        if ($encoding.GetByteCount($json) -ge 20KB) {
            $result = [ordered]@{
                schemaVersion = 1
                status = 'prepared'
                runId = $CaptureRunId
                launcher = "launchers/$CaptureRunId.ps1"
                manifest = "$CaptureRunId/manifest.json"
                pathsRelativeToOutputDirectory = $true
                message = 'Prepared paths exceeded 20 KiB; launcher and manifest are relative to OutputDirectory.'
            }
            $json = $result | ConvertTo-Json -Compress
        }
        $json
        return
    }

    Write-Output "Launcher: $LauncherPath"
    Write-Output "Expected manifest: $ManifestPath"
}

function Test-AnalysisEnabled([System.Collections.IDictionary]$Analyses, [string]$Name) {
    return $Analyses.Contains($Name) -and $Analyses[$Name].captureStatus -eq 'enabled'
}

function Get-CaseCommands(
    [System.Collections.IDictionary]$CaptureCase,
    [string]$CaptureProfiler,
    [string]$ProcessName,
    [string]$MethodFilter,
    [int]$TopRows) {
    $commands = New-Object 'System.Collections.Generic.List[string]'
    $analysisPath = if ($CaptureCase.trace) { $CaptureCase.trace } else { $CaptureCase.speedscope }
    $trace = ConvertTo-PowerShellArgument $analysisPath
    $symbols = if ($CaptureCase.symbolsDirectory) { ConvertTo-PowerShellArgument $CaptureCase.symbolsDirectory } else { $null }

    if ($CaptureProfiler -eq 'ETW') {
        $process = ConvertTo-PowerShellArgument $ProcessName
        if (Test-AnalysisEnabled $CaptureCase.analyses 'processes') {
            $commands.Add("filtrace processes $trace")
        }
        if (Test-AnalysisEnabled $CaptureCase.analyses 'cpu') {
            $commands.Add("filtrace rank $trace --metric cpu --process $process --benchmark --top $TopRows")
            if ($symbols) {
                $method = ConvertTo-PowerShellArgument $MethodFilter
                $commands.Add("filtrace source $trace --view lines --process $process --method $method --symbols $symbols")
            }
            $exportSymbols = if ($symbols) { " --symbols $symbols" } else { '' }
            $commands.Add("filtrace export $trace --process $process --benchmark --native-symbols$exportSymbols -o flame.speedscope.json")
        }
        if (Test-AnalysisEnabled $CaptureCase.analyses 'threadtime') {
            $commands.Add("filtrace rank $trace --metric threadtime --process $process --benchmark --top $TopRows")
        }
        if (Test-AnalysisEnabled $CaptureCase.analyses 'classify') {
            $commands.Add("filtrace classify $trace --process $process --benchmark --native-symbols")
        }
        if (Test-AnalysisEnabled $CaptureCase.analyses 'diskio') {
            $commands.Add("filtrace report $trace --kind diskio --top $TopRows")
        }
        return @($commands)
    }

    if (Test-AnalysisEnabled $CaptureCase.analyses 'cpu') {
        $commands.Add("filtrace rank $trace --metric cpu --benchmark --top $TopRows")
        if ($symbols) {
            $method = ConvertTo-PowerShellArgument $MethodFilter
            $commands.Add("filtrace source $trace --view lines --method $method --symbols $symbols")
            $commands.Add("filtrace export $trace --benchmark --symbols $symbols -o flame.speedscope.json")
        }
        else {
            $commands.Add("filtrace export $trace --benchmark -o flame.speedscope.json")
        }
    }
    if (Test-AnalysisEnabled $CaptureCase.analyses 'alloc') {
        $commands.Add("filtrace rank $trace --metric alloc --benchmark --top $TopRows")
    }
    if (Test-AnalysisEnabled $CaptureCase.analyses 'exceptions') {
        $commands.Add("filtrace rank $trace --metric exceptions --benchmark --top $TopRows")
    }
    foreach ($metric in @('contention', 'wait', 'activity')) {
        if (Test-AnalysisEnabled $CaptureCase.analyses $metric) {
            $commands.Add("filtrace rank $trace --metric $metric --benchmark --top $TopRows")
        }
    }
    foreach ($report in @(
        @{ Analysis = 'gcstats'; Kind = 'gc' }
        @{ Analysis = 'jitstats'; Kind = 'jit' }
        @{ Analysis = 'threadpool'; Kind = 'threadpool' })) {
        if (Test-AnalysisEnabled $CaptureCase.analyses $report.Analysis) {
            $commands.Add("filtrace report $trace --kind $($report.Kind)")
        }
    }
    return @($commands)
}

function Get-CaseWarnings([System.Collections.IDictionary]$CaptureCase) {
    $warnings = New-Object 'System.Collections.Generic.List[string]'
    if ($null -eq $CaptureCase.benchmarkId -or
        [string]::IsNullOrWhiteSpace($CaptureCase.benchmark) -or
        $null -eq $CaptureCase.parameters -or
        [string]::IsNullOrWhiteSpace($CaptureCase.benchmarkDisplay)) {
        $warnings.Add('benchmark identity unavailable or ambiguous; do not use this case with manifest batch/diff; analyze the trace directly')
    }
    foreach ($name in $CaptureCase.analyses.Keys) {
        $status = $CaptureCase.analyses[$name].captureStatus
        if ($status -eq 'disabled') {
            $warnings.Add("$name capture disabled; recapture with a profile that enables it")
        }
        elseif ($status -eq 'unknown') {
            $warnings.Add("$name capture status unknown; no command emitted")
        }
    }
    if ($CaptureCase.trace -and -not $CaptureCase.symbolsDirectory) {
        $warnings.Add('source lines unavailable; no logged child output had an exact matching PDB')
    }
    return @($warnings)
}

function Write-CaptureResult(
    [object[]]$CaptureCases,
    [string]$ManifestPath,
    [string]$CaptureRunId,
    [string]$OutputFormat,
    [bool]$QuietOutput,
    [string]$CaptureOutputDirectory = $null,
    [ValidateSet('completed', 'timeout')]
    [string]$Status = 'completed',
    [string]$LogPath = $null,
    [string]$Message = $null,
    [string]$ResultPath = $null,
    $Owner = $null) {
    if ($OutputFormat -eq 'Json') {
        if ($Status -eq 'timeout') {
            $result = [ordered]@{
                schemaVersion = 1
                status = 'timeout'
                runId = $CaptureRunId
                manifest = $null
                log = $LogPath
                result = $ResultPath
                owner = $Owner
                message = $Message
                warnings = @()
                cases = @()
            }
        }
        else {
            $result = [ordered]@{
                schemaVersion = 1
                status = $Status
                runId = $CaptureRunId
                manifest = $ManifestPath
                warnings = @(
                    foreach ($captureCase in $CaptureCases) {
                        foreach ($warning in $captureCase.warnings) {
                            [ordered]@{ case = $captureCase.id; message = $warning }
                        }
                    }
                )
                cases = @(
                    foreach ($captureCase in $CaptureCases) {
                        [ordered]@{
                            id = $captureCase.id
                            trace = $captureCase.trace
                            speedscope = $captureCase.speedscope
                            commands = $captureCase.commands
                        }
                    }
                )
            }
        }

        $maxResultBytes = 20KB
        $encoding = New-Object System.Text.UTF8Encoding($false)
        $runDirectoryPath = if ($ManifestPath) {
            Split-Path -Parent $ManifestPath
        }
        elseif ($LogPath) {
            Split-Path -Parent $LogPath
        }
        else {
            "BenchmarkDotNet.Artifacts/filtrace-runs/$CaptureRunId"
        }
        $json = $result | ConvertTo-Json -Depth 6 -Compress
        if ($encoding.GetByteCount($json) -ge $maxResultBytes) {
            # Completed runs have a manifest; a timed-out child may have produced only
            # partial output, so the fallback guidance differs by status.
            $fallbackMessage = if ($Status -eq 'timeout') {
                'Timeout details exceeded 20 KiB; inspect the run directory for partial output.'
            }
            else {
                'JSON handoff exceeded 20 KiB; read the manifest for full cases, commands, and warnings.'
            }
            $result = [ordered]@{
                schemaVersion = 1
                status = $Status
                runId = $CaptureRunId
                manifest = $ManifestPath
                outputDirectory = $CaptureOutputDirectory
                runDirectory = $runDirectoryPath
                message = $fallbackMessage
            }
            if ($Status -eq 'timeout') {
                $result.log = $LogPath
                $result.result = $ResultPath
                $result.owner = $Owner
            }
            $json = $result | ConvertTo-Json -Depth 3 -Compress
            if ($encoding.GetByteCount($json) -ge $maxResultBytes) {
                $result = [ordered]@{
                    schemaVersion = 1
                    status = $Status
                    runId = $CaptureRunId
                    manifest = if ($Status -eq 'completed') { "$CaptureRunId/manifest.json" } else { $null }
                    runDirectory = $CaptureRunId
                    pathsRelativeToOutputDirectory = $true
                    message = 'JSON handoff exceeded 20 KiB; paths are relative to OutputDirectory.'
                }
                if ($Status -eq 'timeout') {
                    $result.log = "$CaptureRunId/capture.log"
                    $result.result = "$CaptureRunId.timeout.json"
                    $result.owner = $Owner
                }
                $json = $result | ConvertTo-Json -Depth 3 -Compress
            }
        }

        $json
        return
    }

    if ($Status -eq 'timeout') {
        Write-Warning $Message
        return
    }

    if (-not $QuietOutput) {
        Write-Host "`nCaptured $($CaptureCases.Count) case(s)." -ForegroundColor Green
        Write-Host "Manifest: $ManifestPath" -ForegroundColor Green
        foreach ($captureCase in $CaptureCases) {
            $analysisPath = if ($captureCase.trace) { $captureCase.trace } else { $captureCase.speedscope }
            Write-Host "`nCase: $($captureCase.id)" -ForegroundColor Green
            Write-Host "Captured: $analysisPath"
            if ($captureCase.commands.Count -gt 0) {
                Write-Host 'Next-step filtrace commands:'
                foreach ($command in $captureCase.commands) { Write-Host "  $command" }
            }
            foreach ($warning in $captureCase.warnings) { Write-Warning "[$($captureCase.id)] $warning" }
        }
        return
    }

    foreach ($captureCase in $CaptureCases) {
        foreach ($warning in $captureCase.warnings) { Write-Warning "[$($captureCase.id)] $warning" }
    }
}

# Resolve the project file (accept either a .csproj or a directory holding one).
$projItem = Get-Item -LiteralPath $Project
if ($projItem.PSIsContainer) {
    $projFile = Get-ChildItem -LiteralPath $Project -Filter *.csproj | Select-Object -First 1
    if ($null -eq $projFile) { Write-Error "No .csproj found under $Project." -ErrorAction Continue ; exit 1 }
}
else {
    $projFile = $projItem
}
if (-not $Process) { $Process = [System.IO.Path]::GetFileNameWithoutExtension($projFile.Name) }
$hasOperationCount = $PSBoundParameters.ContainsKey('OperationCount')
$hasOperationUnit = $PSBoundParameters.ContainsKey('OperationUnit') -and
    -not [string]::IsNullOrWhiteSpace($OperationUnit)
if ($hasOperationCount -ne $hasOperationUnit) {
    Write-Error 'Specify OperationCount and OperationUnit together, or omit both.' -ErrorAction Continue
    exit 1
}

$repoRoot = (Get-Location).Path
if (-not $RunId) {
    $RunId = "$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
}
$runsDirectory = try {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'BenchmarkDotNet.Artifacts/filtrace-runs'))
    }
    else {
        [System.IO.Path]::GetFullPath($OutputDirectory)
    }
}
catch {
    Write-Error "OutputDirectory '$OutputDirectory' is invalid: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
$runDirectory = Join-Path $runsDirectory $RunId
$artifacts = Join-Path $runDirectory 'artifacts'
$log = Join-Path $runDirectory 'capture.log'
$manifestPath = Join-Path $runDirectory 'manifest.json'
$runClaimPath = Join-Path $runsDirectory "$RunId.claim"
$preflightFailurePath = Join-Path $runsDirectory "$RunId.preflight-failure.json"
$preflightLog = Join-Path $runsDirectory "$RunId.preflight.log"
$timeoutPath = Join-Path $runsDirectory "$RunId.timeout.json"
$launcherDirectory = Join-Path $runsDirectory 'launchers'
$launcherPath = Join-Path $launcherDirectory "$RunId.ps1"

function Test-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-SafeElevationArgument([string]$Value) {
    return $null -ne $Value -and
        $Value.IndexOfAny([char[]]@('"', "`r", "`n")) -lt 0 -and
        -not $Value.EndsWith('\', [StringComparison]::Ordinal)
}

function Read-BoundedUtf8File([string]$Path, [int]$MaxBytes) {
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        if ($stream.Length -ge $MaxBytes) {
            throw "File '$Path' is $($stream.Length) bytes; the safety limit is $MaxBytes bytes."
        }
        $length = [int]$stream.Length
        $bytes = [byte[]]::new($length)
        $offset = 0
        while ($offset -lt $length) {
            $read = $stream.Read($bytes, $offset, $length - $offset)
            if ($read -eq 0) { throw "File '$Path' ended before $length bytes could be read." }
            $offset += $read
        }
        return [System.Text.Encoding]::UTF8.GetString($bytes)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-ProcessPropertySafely($Process, [string]$Name) {
    try {
        if ($null -eq $Process.PSObject.Properties[$Name]) { return $null }
        return $Process.$Name
    }
    catch {
        return $null
    }
}

function Get-FirstExistingFile([string[]]$Paths) {
    foreach ($path in $Paths) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and
            (Test-Path -LiteralPath $path -PathType Leaf)) {
            return $path
        }
    }
    return $null
}

function Stop-ReservedRun([string]$Message, [int]$ExitCode = 1) {
    $failurePath = Join-Path $runsDirectory "$RunId.failure.json"
    $failureLog = Join-Path $runsDirectory "$RunId.failure.log"
    $resultWriteError = $null
    try {
        if (-not (Test-Path -LiteralPath $failurePath -PathType Leaf)) {
            Write-FailureResult $failurePath $failureLog $RunId $Message $ExitCode
        }
    }
    catch {
        $resultWriteError = $_.Exception.Message
    }

    $diagnostic = $Message
    if (Test-Path -LiteralPath $failurePath -PathType Leaf) {
        $diagnostic += " Failure result: $failurePath"
    }
    elseif ($resultWriteError) {
        $diagnostic += " Failure result could not be written: $resultWriteError"
    }
    Write-Error $diagnostic -ErrorAction Continue
    exit $ExitCode
}

if ($ElevatedChild -and $Profiler -ne 'ETW') {
    Write-Error '-ElevatedChild is reserved for the internal elevated ETW handoff.' -ErrorAction Continue
    exit 1
}

if ($Prepare -and ($Profiler -ne 'ETW' -or $ElevatedChild)) {
    Write-Error '-Prepare is valid only for a non-child ETW capture.' -ErrorAction Continue
    exit 1
}

$invokedFromPreparedLauncher = $false
if (-not [string]::IsNullOrWhiteSpace($PreparedLauncher)) {
    if ($Profiler -ne 'ETW' -or $Prepare) {
        Write-Error '-PreparedLauncher is reserved for a prepared ETW capture.' -ErrorAction Continue
        exit 1
    }
    try {
        $preparedLauncherPath = [System.IO.Path]::GetFullPath($PreparedLauncher)
    }
    catch {
        Write-Error "PreparedLauncher '$PreparedLauncher' is invalid: $($_.Exception.Message)" -ErrorAction Continue
        exit 1
    }
    if (-not $preparedLauncherPath.Equals($launcherPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        Write-Error "PreparedLauncher must identify the existing canonical launcher '$launcherPath'." -ErrorAction Continue
        exit 1
    }
    $PreparedLauncher = $preparedLauncherPath
    $invokedFromPreparedLauncher = $true
}

$inheritedReservation = -not [string]::IsNullOrWhiteSpace($ReservationToken)
if ($inheritedReservation -and -not ($ElevatedChild -or $invokedFromPreparedLauncher)) {
    Write-Error '-ReservationToken is reserved for an elevated or prepared capture handoff.' -ErrorAction Continue
    exit 1
}
if (($ElevatedChild -or $invokedFromPreparedLauncher) -and -not $inheritedReservation) {
    Write-Error '-ElevatedChild and -PreparedLauncher require -ReservationToken for the atomic run handoff.' -ErrorAction Continue
    exit 1
}

# Validate user-controlled handoff values before the platform check so unsafe
# arguments are rejected consistently on every host without reaching UAC.
if ($Profiler -eq 'ETW') {
    $requestedElevationArguments = [ordered]@{
        Script = $PSCommandPath
        Project = $projFile.FullName
        Filter = $Filter
        Tfm = $Tfm
        Process = $Process
        OutputDirectory = $runsDirectory
        DotnetPath = $DotnetPath
        FiltracePath = $FiltracePath
    }
    if ($hasOperationUnit) { $requestedElevationArguments.OperationUnit = $OperationUnit }
    if ($invokedFromPreparedLauncher) { $requestedElevationArguments.PreparedLauncher = $PreparedLauncher }
    if ($inheritedReservation) { $requestedElevationArguments.ReservationToken = $ReservationToken }
    foreach ($argument in $requestedElevationArguments.GetEnumerator()) {
        if (-not (Test-SafeElevationArgument ([string]$argument.Value))) {
            Write-Error "ETW elevation argument '$($argument.Key)' cannot contain quotes, newlines, or end in a backslash." -ErrorAction Continue
            exit 1
        }
    }
}

# Recording an .etl is Windows-only, and Test-Elevated below calls a Windows-only API, so
# fail fast with a clear message rather than a PlatformNotSupportedException. Compare
# against $false so Windows PowerShell 5.1 (undefined $IsWindows) is not mistaken for a
# non-Windows OS.
if ($Profiler -eq 'ETW' -and $IsWindows -eq $false) {
    Write-Error 'ETW capture is Windows-only. Use -Profiler EP on this OS.' -ErrorAction Continue
    exit 1
}

if ($ElevatedChild -and -not (Test-Elevated)) {
    Write-Error '-ElevatedChild requires an elevated Windows process.' -ErrorAction Continue
    exit 1
}

try {
    New-Item -ItemType Directory -Force -Path $runsDirectory | Out-Null
}
catch {
    Write-Error "OutputDirectory '$runsDirectory' could not be created: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
if (Test-Path -LiteralPath $runDirectory) {
    Write-Error "Capture run ID '$RunId' already exists at '$runDirectory'. Choose a new RunId; existing run artifacts are never reused." -ErrorAction Continue
    exit 1
}
if ((Test-Path -LiteralPath $preflightFailurePath) -or
    (Test-Path -LiteralPath $preflightLog) -or
    ((Test-Path -LiteralPath $timeoutPath) -and -not ($inheritedReservation -and $ElevatedChild))) {
    Write-Error "Capture run ID '$RunId' already has a terminal result. Choose a new RunId; terminal results are never reused." -ErrorAction Continue
    exit 1
}
if ((Test-Path -LiteralPath $launcherPath) -and -not ($invokedFromPreparedLauncher -and $inheritedReservation)) {
    Write-Error "Capture run ID '$RunId' already has a prepared launcher at '$launcherPath'. Run that launcher or choose a new RunId." -ErrorAction Continue
    exit 1
}
if ($inheritedReservation) {
    if (-not (Test-RunReservation $runClaimPath $RunId $ReservationToken)) {
        Write-Error "Capture run ID '$RunId' does not have a matching atomic reservation." -ErrorAction Continue
        exit 1
    }
}
else {
    try {
        $ReservationToken = New-RunReservation $runClaimPath $RunId
    }
    catch [System.IO.IOException] {
        Write-Error "Capture run ID '$RunId' is already reserved. Choose a new RunId; existing run artifacts are never reused." -ErrorAction Continue
        exit 1
    }
    # Close races with legacy writers that may not participate in the shared claim.
    if ((Test-Path -LiteralPath $runDirectory) -or
        (Test-Path -LiteralPath $preflightFailurePath) -or
        (Test-Path -LiteralPath $preflightLog) -or
        (Test-Path -LiteralPath $timeoutPath) -or
        (Test-Path -LiteralPath $launcherPath)) {
        $reservationMessage = "Capture run ID '$RunId' became occupied after its atomic reservation; existing artifacts were not modified."
        $reservationFailurePath = Join-Path $runsDirectory "$RunId.failure.json"
        $reservationFailureLog = Join-Path $runsDirectory "$RunId.failure.log"
        Write-FailureResult $reservationFailurePath $reservationFailureLog $RunId $reservationMessage 1
        Write-Error "$reservationMessage Failure result: $reservationFailurePath" -ErrorAction Continue
        exit 1
    }
}

$dotnetCommand = Get-Command $DotnetPath -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $dotnetCommand) {
    $preflightMessage = "dotnet host '$DotnetPath' could not be resolved."
    Write-FailureResult $preflightFailurePath $preflightLog $RunId $preflightMessage 1
    Write-Error "$preflightMessage Failure result: $preflightFailurePath" -ErrorAction Continue
    exit 1
}
$DotnetPath = if ($dotnetCommand.Path) { $dotnetCommand.Path } else { $dotnetCommand.Source }
try {
    Assert-ProjectPreflight $projFile.FullName $Tfm $Profiler $DotnetPath
}
catch {
    $preflightMessage = $_.Exception.Message
    Write-FailureResult $preflightFailurePath $preflightLog $RunId $preflightMessage 1
    Write-Error "$preflightMessage Failure result: $preflightFailurePath" -ErrorAction Continue
    exit 1
}

$filtraceCommand = Get-Command $FiltracePath -ErrorAction SilentlyContinue | Select-Object -First 1
$filtraceAvailable = $null -ne $filtraceCommand
if ($filtraceAvailable) {
    $FiltracePath = if ($filtraceCommand.Path) { $filtraceCommand.Path } else { $filtraceCommand.Source }
    try {
        Assert-FiltraceCompatibility $FiltracePath
    }
    catch {
        $preflightMessage = $_.Exception.Message
        Write-FailureResult $preflightFailurePath $preflightLog $RunId $preflightMessage 1
        Write-Error "$preflightMessage Failure result: $preflightFailurePath" -ErrorAction Continue
        exit 1
    }
}

if ($Profiler -eq 'ETW') {
    $elevationArguments = [ordered]@{
        Script = $PSCommandPath
        Project = $projFile.FullName
        Filter = $Filter
        Tfm = $Tfm
        Process = $Process
        OutputDirectory = $runsDirectory
        DotnetPath = $DotnetPath
        FiltracePath = $FiltracePath
    }
    if ($hasOperationUnit) { $elevationArguments.OperationUnit = $OperationUnit }
    if ($invokedFromPreparedLauncher) { $elevationArguments.PreparedLauncher = $PreparedLauncher }
    $elevationArguments.ReservationToken = $ReservationToken
    foreach ($argument in $elevationArguments.GetEnumerator()) {
        if (-not (Test-SafeElevationArgument ([string]$argument.Value))) {
            $preflightMessage = "ETW elevation argument '$($argument.Key)' cannot contain quotes, newlines, or end in a backslash."
            Write-FailureResult $preflightFailurePath $preflightLog $RunId $preflightMessage 1
            Write-Error "$preflightMessage Failure result: $preflightFailurePath" -ErrorAction Continue
            exit 1
        }
    }
}

if ($Prepare) {
    try {
        New-Item -ItemType Directory -Force -Path $launcherDirectory | Out-Null
        $parameterLines = @(
        "    Project = $(ConvertTo-PowerShellArgument $projFile.FullName)",
        "    Filter = $(ConvertTo-PowerShellArgument $Filter)",
        "    Profiler = 'ETW'",
        "    Tfm = $(ConvertTo-PowerShellArgument $Tfm)",
        "    Process = $(ConvertTo-PowerShellArgument $Process)",
        "    Top = $Top",
        "    ElevatedTimeoutSeconds = $ElevatedTimeoutSeconds",
        "    OutputDirectory = $(ConvertTo-PowerShellArgument $runsDirectory)",
        "    RunId = $(ConvertTo-PowerShellArgument $RunId)",
        "    DotnetPath = $(ConvertTo-PowerShellArgument $DotnetPath)",
        "    FiltracePath = $(ConvertTo-PowerShellArgument $FiltracePath)",
        "    Format = $(ConvertTo-PowerShellArgument $Format)",
        '    PreparedLauncher = $PSCommandPath',
        "    ReservationToken = $(ConvertTo-PowerShellArgument $ReservationToken)")
        if ($hasOperationCount) {
            $parameterLines += "    OperationCount = $($OperationCount.ToString('R', [Globalization.CultureInfo]::InvariantCulture))"
            $parameterLines += "    OperationUnit = $(ConvertTo-PowerShellArgument $OperationUnit)"
        }
        if ($Quiet) { $parameterLines += '    Quiet = $true' }
        $launcher = (@('$captureParameters = @{') + $parameterLines + @(
        '}',
        "& $(ConvertTo-PowerShellArgument $PSCommandPath) @captureParameters",
        'exit $LASTEXITCODE')) -join [Environment]::NewLine
        $tokens = $null
        $parseErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseInput(
        $launcher,
        [ref]$tokens,
        [ref]$parseErrors)
        if ($parseErrors.Count -ne 0) {
            throw "Generated launcher failed syntax validation: $($parseErrors[0].Message)"
        }
        $encoding = New-Object System.Text.UTF8Encoding($true)
        $launcherStream = [System.IO.File]::Open(
            $launcherPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $preamble = $encoding.GetPreamble()
            $launcherStream.Write($preamble, 0, $preamble.Length)
            $launcherBytes = $encoding.GetBytes($launcher)
            $launcherStream.Write($launcherBytes, 0, $launcherBytes.Length)
        }
        finally {
            $launcherStream.Dispose()
        }
        Write-PreparationResult $launcherPath $manifestPath $RunId $Format
        exit 0
    }
    catch {
        Stop-ReservedRun "Capture preparation failed: $($_.Exception.Message)" 1
    }
}

# ETW kernel sessions require Administrator. When not elevated, relaunch this script
# in an elevated window, then wait for it.
# -WorkingDirectory anchors the child at the repo root so BenchmarkDotNet.Artifacts
# and capture.log are created there, not in the elevated shell's system32 directory.
if ($Profiler -eq 'ETW' -and -not (Test-Elevated)) {
    $elevationFailureExitCode = 1
    try {
        if ($showProgress) {
        Write-Host 'ETW capture needs Administrator; relaunching elevated (a UAC prompt will appear).' -ForegroundColor Yellow
    }
    # Quote path/value args so a project path, filter, or process name containing spaces
    # survives Start-Process joining the array into a single command line.
    $argList = @('-NoProfile', '-File', "`"$PSCommandPath`"", '-Project', "`"$($projFile.FullName)`"",
        '-Filter', "`"$Filter`"", '-Profiler', 'ETW', '-Tfm', "`"$Tfm`"", '-Process', "`"$Process`"", '-Top', $Top,
        '-OutputDirectory', "`"$runsDirectory`"", '-RunId', $RunId, '-DotnetPath', "`"$DotnetPath`"", '-FiltracePath', "`"$FiltracePath`"", '-Format', $Format,
        '-ReservationToken', $ReservationToken, '-ElevatedChild')
    if ($hasOperationCount) {
        $argList += @(
            '-OperationCount', $OperationCount.ToString('R', [Globalization.CultureInfo]::InvariantCulture),
            '-OperationUnit', "`"$OperationUnit`"")
    }
    if ($invokedFromPreparedLauncher) {
        $argList += @('-PreparedLauncher', "`"$PreparedLauncher`"")
    }
    if ($Quiet) { $argList += '-Quiet' }
    # Relaunch with the host that is ALREADY running this script, not a hardcoded 'pwsh' -
    # a caller on Windows PowerShell 5.1 without PowerShell 7 installed would otherwise
    # fail here with pwsh unresolved.
    $hostExe = (Get-Process -Id $PID).Path
    # Do NOT pass -Wait here. With -Verb RunAs, Start-Process -Wait can fail to release
    # after the elevated child self-closes, hanging the parent forever even though the
    # capture already finished and the .etl is on disk. Take the process object and wait on
    # it directly with a bounded WaitForExit, so a lost or access-denied handle degrades to
    # a timeout result that reports the log path instead of an indefinite hang.
        $proc = Start-Process -FilePath $hostExe -Verb RunAs -PassThru -WorkingDirectory $repoRoot -ArgumentList $argList
        if ($null -eq $proc) {
            throw 'Elevated relaunch returned no process handle; cannot wait for the capture. Check for a blocked UAC prompt.'
        }
    # WaitForExit / HasExited / ExitCode can each throw (e.g. Access Denied reading the
    # elevated, higher-integrity child's handle). Under $ErrorActionPreference='Stop' an
    # uncaught throw would abort the script instead of producing the bounded timeout result,
    # so guard every handle access and treat a throw as a timeout-like miss.
    # Clamp to Int32.MaxValue so a large timeout cannot overflow the millisecond argument.
        $waitMs = [int][Math]::Min([long]$ElevatedTimeoutSeconds * 1000, [int]::MaxValue)
    $exited = $false
    try { $exited = $proc.WaitForExit($waitMs) } catch { $exited = $false }
        if (-not $exited) {
        $ownerProcessId = Get-ProcessPropertySafely $proc 'Id'
        $ownerProcessName = Get-ProcessPropertySafely $proc 'ProcessName'
        $owner = [ordered]@{
            processId = $ownerProcessId
            processName = $ownerProcessName
        }
        $ownerDescription = if ($null -ne $owner.processId) {
            "PID $($owner.processId)"
        }
        else {
            'the elevated child process'
        }
        $timeoutMessage = "Elevated capture owner $ownerDescription did not signal completion within $ElevatedTimeoutSeconds s; not blocking further. See $log for progress."
        Write-RunManifestAtomically $timeoutPath ([ordered]@{
            schemaVersion = 1
            status = 'timeout'
            runId = $RunId
            observedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            owner = $owner
            log = $log
            message = $timeoutMessage
        })
        Write-CaptureResult -CaptureCases @() -ManifestPath $null -CaptureRunId $RunId `
            -OutputFormat $Format -QuietOutput ([bool]$Quiet) -CaptureOutputDirectory $runsDirectory -Status timeout `
            -LogPath $log -Message $timeoutMessage -ResultPath $timeoutPath -Owner $owner
            exit 0
        }
    # ExitCode is only defined once the child has exited, and reading it on a higher-integrity
    # (elevated) process can throw Access Denied - treat either as 'not observed', non-fatal.
    $childExit = 0
    try { if ($proc.HasExited) { $childExit = $proc.ExitCode } } catch { $childExit = 0 }
        if ($childExit -ne 0) {
            $elevationFailureExitCode = $childExit
        $observedResult = Get-FirstExistingFile @(
            (Join-Path $runDirectory 'failure.json'),
            (Join-Path $runsDirectory "$RunId.failure.json"),
            $preflightFailurePath)
        $observedLog = Get-FirstExistingFile @(
            $log,
            (Join-Path $runsDirectory "$RunId.failure.log"),
            $preflightLog)
        $failureMessage = "Elevated capture failed (exit $childExit)."
        if ($observedLog) { $failureMessage += " See $observedLog." }
        if ($observedResult) { $failureMessage += " Failure result: $observedResult" }
            if ($observedResult) {
                Write-Error $failureMessage -ErrorAction Continue
                exit $childExit
            }
            throw $failureMessage
        }
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "Elevated capture did not produce $manifestPath. See $log for details."
        }
        $childManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        Write-CaptureResult @($childManifest.cases) $manifestPath $RunId $Format ([bool]$Quiet) $runsDirectory
        exit 0
    }
    catch {
        Stop-ReservedRun "Elevated capture handoff failed: $($_.Exception.Message)" $elevationFailureExitCode
    }
}

$projectLockName = [regex]::Replace(
    [System.IO.Path]::GetFileNameWithoutExtension($projFile.Name),
    '[^A-Za-z0-9._-]',
    '_')
if ([string]::IsNullOrEmpty($projectLockName)) { $projectLockName = 'project' }
$tfmLockName = [regex]::Replace($Tfm, '[^A-Za-z0-9._-]', '_')
if ([string]::IsNullOrEmpty($tfmLockName)) { $tfmLockName = 'default' }
$lockName = "$projectLockName-$tfmLockName"
$lockDirectory = Join-Path $projFile.DirectoryName 'obj/filtrace-capture-locks'
try {
    New-Item -ItemType Directory -Force -Path $lockDirectory | Out-Null
    $lockPath = Join-Path $lockDirectory "$lockName.lock"
    $captureLock = [System.IO.File]::Open(
        $lockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
}
catch {
    Stop-ReservedRun "A capture could not acquire the project lock for '$($projFile.FullName)' and TFM '$Tfm': $($_.Exception.Message)" 1
}

try {
    if (Test-Path -LiteralPath $runDirectory) {
        Write-Error "Capture run ID '$RunId' already exists at '$runDirectory'. Choose a new RunId; existing run artifacts are never reused." -ErrorAction Continue
        exit 1
    }

    $fallbackFailurePath = Join-Path $runsDirectory "$RunId.failure.json"
    $fallbackFailureLog = Join-Path $runsDirectory "$RunId.failure.log"
    $failurePath = $fallbackFailurePath
    $failureLog = $fallbackFailureLog
    $captureExitCode = 1
    try {
        # A pre-existing directory from an older helper may not have a claim file.
        # Recheck after the atomic claim to close the check/create race.
        if (Test-Path -LiteralPath $runDirectory) {
            throw "Capture run ID '$RunId' became occupied at '$runDirectory' after it was reserved; existing run artifacts were not modified."
        }

        New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
        $failurePath = Join-Path $runDirectory 'failure.json'
        $failureLog = $log

# Preserve the BenchmarkDotNet build output for source-symbol resolution under both
# profilers. Both branches are multi-element arrays, so they stay arrays (a
# single-element if-expression would unwrap to a scalar under Set-StrictMode).
$profArg = @('-p', $Profiler, '--keepFiles')
$benchmarkArguments = @('run', '-c', 'Release', '-f', $Tfm, '--project', $projFile.FullName, '--', '--filter', $Filter) +
    $profArg + @('--artifacts', $artifacts)
$startedUtc = [DateTimeOffset]::UtcNow
$commandLine = @(
    ConvertTo-PowerShellArgument $DotnetPath
    foreach ($argument in $benchmarkArguments) {
        ConvertTo-PowerShellArgument ([string]$argument)
    }
) -join ' '
$encoding = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    $log,
    "Command: $commandLine$([Environment]::NewLine)",
    $encoding)

if ($showProgress) {
    Write-Host "Capturing $Profiler trace: $Filter ($Tfm)..." -ForegroundColor Cyan
}
# Keep full BenchmarkDotNet output in the run log; stdout remains a compact filtrace
# handoff rather than a duplicate benchmark transcript.
$logWriter = New-Object System.IO.StreamWriter($log, $true, $encoding)
try {
    $PSNativeCommandUseErrorActionPreference = $false
    & $DotnetPath @benchmarkArguments 2>&1 |
        ForEach-Object { $logWriter.WriteLine($_.ToString()) }
    $benchmarkExitCode = $LASTEXITCODE
}
finally {
    $logWriter.Dispose()
}
if ($benchmarkExitCode -ne 0) {
    $captureExitCode = $benchmarkExitCode
    throw "Benchmark run failed (exit $captureExitCode). See $log."
}

$captureCases = @(Get-CaptureCases $artifacts $Profiler)
if ($captureCases.Count -eq 0) {
    throw "No capture files found in $artifacts. Did the capture run?"
}
Set-BenchmarkIdentities $captureCases $log $FiltracePath $filtraceAvailable
if ($hasOperationCount) {
    foreach ($captureCase in $captureCases) {
        $captureCase.operationCount = $OperationCount
        $captureCase.operationUnit = $OperationUnit
    }
}

# The project build output carries matching PDBs for source lines; --keepFiles also
# preserves BenchmarkDotNet's generated build output for manual follow-up.
$symbols = Join-Path (Split-Path -Parent $projFile.FullName) "bin/Release/$Tfm"
$symbolCandidates = @(Get-SymbolCandidates $log $symbols)
foreach ($captureCase in $captureCases) {
    $captureCase.symbolCandidates = $symbolCandidates
    if ($captureCase.trace -and $filtraceAvailable) {
        $captureCase.symbolsDirectory = Find-ExactSymbolDirectory $captureCase.trace $symbolCandidates $FiltracePath
    }
}
$methodFilter = $Filter.Trim('*')
if ([string]::IsNullOrWhiteSpace($methodFilter)) { $methodFilter = 'BenchmarkMethod' }

foreach ($captureCase in $captureCases) {
    $captureStatuses = Get-DefaultCaptureStatuses $Profiler ([bool]$captureCase.trace)
    if ($captureCase.trace) {
        Write-CaptureMetadata $captureCase.trace $captureStatuses
    }

    $analysisPath = if ($captureCase.trace) { $captureCase.trace } else { $captureCase.speedscope }
    $traceInfo = if ($filtraceAvailable) {
        Get-TraceInfoResult $analysisPath $captureCase.symbolsDirectory $FiltracePath
    }
    else {
        $null
    }
    $traceInfoFailed = $filtraceAvailable -and -not (Test-HasAnalysisInfo $traceInfo)
    $captureCase.analyses = ConvertTo-AnalysisMap $traceInfo $captureStatuses (-not $filtraceAvailable)
    $captureCase.commands = Get-CaseCommands $captureCase $Profiler $Process $methodFilter $Top
    $captureCase.warnings = @(
        if ($traceInfoFailed) {
            'filtrace info could not verify analysis availability; no commands emitted'
        }
        Get-CaseWarnings $captureCase
    )
}

$manifest = [ordered]@{
    schemaVersion = 1
    runId = $RunId
    startedUtc = $startedUtc.ToString('O')
    completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    command = [ordered]@{
        executable = $DotnetPath
        arguments = $benchmarkArguments
    }
    project = $projFile.FullName
    tfm = $Tfm
    filter = $Filter
    profiler = $Profiler
    process = $Process
    source = Get-SourceIdentity $projFile.DirectoryName
    runtimes = @(Get-RuntimeSummaries $log)
    paths = [ordered]@{
        outputDirectory = $runsDirectory
        runDirectory = $runDirectory
        artifactsDirectory = $artifacts
        log = $log
    }
    cases = $captureCases
}
Write-RunManifest $manifestPath $manifest

if (-not $ElevatedChild) {
    Write-CaptureResult $captureCases $manifestPath $RunId $Format ([bool]$Quiet) $runsDirectory
}
    }
    catch {
        $failureMessage = $_.Exception.Message
        try {
            Write-FailureResult $failurePath $failureLog $RunId $failureMessage $captureExitCode
        }
        catch {
            $resultWriteMessage = $_.Exception.Message
            if ($failurePath -ne $fallbackFailurePath) {
                try {
                    Write-FailureResult $fallbackFailurePath $fallbackFailureLog $RunId $failureMessage $captureExitCode
                    $failurePath = $fallbackFailurePath
                }
                catch {
                    Write-Error "Capture failed and its failure results could not be written: $resultWriteMessage; $($_.Exception.Message)" -ErrorAction Continue
                }
            }
            else {
                Write-Error "Capture failed and its failure result could not be written: $resultWriteMessage" -ErrorAction Continue
            }
        }
        $observedFailurePath = Get-FirstExistingFile @($failurePath, $fallbackFailurePath)
        $failureDiagnostic = $failureMessage
        if ($observedFailurePath) { $failureDiagnostic += " Failure result: $observedFailurePath" }
        Write-Error $failureDiagnostic -ErrorAction Continue
        exit $captureExitCode
    }
}
finally {
    $captureLock.Dispose()
}
