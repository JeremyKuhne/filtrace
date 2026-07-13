<#
.SYNOPSIS
    Capture a .NET CPU trace (EventPipe or ETW) of a BenchmarkDotNet benchmark, then
    print the filtrace commands to analyze it. This helper drives the capture step via
    BenchmarkDotNet's EventPipe or ETW profiler.

.DESCRIPTION
    Wraps the "record a trace, then analyze it" loop for a BenchmarkDotNet perf
        project. Run it from the repository root. Each invocation passes a run-specific
        `--artifacts` directory and `--keepFiles`, enumerates every profiler output in
        that run, and emits a compact manifest with parameterized benchmark identity,
        trace pairs, runtime/source identity, and exact symbols when verified:

      - EventPipe (-Profiler EP, the default): cross-platform, no elevation, single
                process. BenchmarkDotNet normally writes a raw .nettrace and a paired derived
                .speedscope.json. The manifest retains both; analysis commands use the raw
                trace when present, or limit a speedscope-only case to CPU/export commands.
      - ETW (-Profiler ETW): Windows only, self-elevates (one UAC prompt), machine
                wide. Uses `-p ETW --keepFiles`, which writes a .etl. Only an
        .etl carries wall-clock (threadtime), the native GC / JIT / memcpy split
        (classify --native-symbols), and multi-process scoping.

    Output is teed, never redirected away, so the elevated window shows live
    progress instead of looking hung. The printed filtrace commands are pre-scoped:
    an EventPipe trace with --benchmark (past the harness); an .etl additionally with
    --process, because an .etl is machine-wide.

    Every invocation writes BenchmarkDotNet output and capture.log under a unique
    BenchmarkDotNet.Artifacts/filtrace-runs/<RunId> directory, then emits manifest.json
    with every parameterized capture case. A same-project/same-TFM handle lock rejects
    overlap before any build starts. Logged child OutDir paths are verified with
    filtrace info; source commands are printed only when an exact PDB maps sampled
    frames. No globally newest artifact is selected.

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

.PARAMETER ElevatedTimeoutSeconds
    How long the non-elevated parent waits for the self-elevated ETW capture to finish
    before it stops blocking and surfaces the log tail. Default 1200 (20 minutes). Only
    the ETW self-elevation path uses it - it is the backstop that keeps a never-signaled
    elevated child from hanging the parent indefinitely.

.PARAMETER RunId
    Optional stable identifier for this capture run. Defaults to a UTC timestamp plus
    a random suffix. The run's BenchmarkDotNet artifacts and capture log are written
    under BenchmarkDotNet.Artifacts/filtrace-runs/<RunId>.

.PARAMETER DotnetPath
    Path or command name for the dotnet host. Defaults to dotnet from PATH.

.PARAMETER FiltracePath
    Path or command name for filtrace. Used after capture to verify which logged
    BenchmarkDotNet child output has an exact PDB match for each trace.

.EXAMPLE
    ./Capture-BenchmarkTrace.ps1 -Project src/App.Perf -Filter '*GlobMatchBench*'

.EXAMPLE
    ./Capture-BenchmarkTrace.ps1 -Project src/App.Perf -Filter '*GlobMatchBench*' -Profiler ETW
#>
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Filter,
    [ValidateSet('EP', 'ETW')][string]$Profiler = 'EP',
    [string]$Tfm = 'net10.0',
    [string]$Process,
    [int]$Top = 25,
    [ValidateRange(1, 2147483647)][int]$ElevatedTimeoutSeconds = 1200,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')][string]$RunId,
    [string]$DotnetPath = 'dotnet',
    [string]$FiltracePath = 'filtrace'
)

$ErrorActionPreference = 'Stop'

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
    $maxManifestBytes = 20KB
    $json = $Manifest | ConvertTo-Json -Depth 8 -Compress
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $manifestBytes = $encoding.GetByteCount($json)
    if ($manifestBytes -ge $maxManifestBytes) {
        throw "Capture manifest is $manifestBytes UTF-8 bytes; it must stay under 20 KiB. Narrow the benchmark filter or split the capture into fewer cases."
    }

    [System.IO.File]::WriteAllText($Path, $json, $encoding)
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
                benchmarkDisplay = $null
                capturedUtc = $file.LastWriteTimeUtc.ToString('O')
                trace = $null
                speedscope = $null
                symbolsDirectory = $null
                symbolCandidates = @()
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

function Set-BenchmarkIdentities([System.Collections.IDictionary[]]$CaptureCases, [string]$CaptureLog) {
    $benchmarksById = @{}
    $benchmarksInExecutionOrder = New-Object 'System.Collections.Generic.List[System.Collections.IDictionary]'
    $currentDisplay = $null
    foreach ($line in Get-Content -LiteralPath $CaptureLog) {
        if ($line -match '^// Benchmark: (.+)$') {
            $currentDisplay = $Matches[1]
            continue
        }

        if ($null -ne $currentDisplay -and
            $line -match '--benchmarkName\s+(?:"([^"]+)"|(\S+)).*--benchmarkId\s+(\d+)') {
            $benchmarkId = [int]$Matches[3]
            if (-not $benchmarksById.ContainsKey($benchmarkId)) {
                $benchmarkName = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                $benchmarksById[$benchmarkId] = [ordered]@{
                    benchmarkId = $benchmarkId
                    benchmark = $benchmarkName
                    benchmarkDisplay = $currentDisplay
                }
                $benchmarksInExecutionOrder.Add($benchmarksById[$benchmarkId])
            }
        }
    }

    foreach ($benchmarkName in @($benchmarksInExecutionOrder.benchmark | Select-Object -Unique)) {
        $benchmarks = @($benchmarksInExecutionOrder | Where-Object { $_.benchmark -eq $benchmarkName })
        $prefix = "$benchmarkName-"
        $cases = @(
            $CaptureCases |
                Where-Object { $_.id.StartsWith($prefix, [StringComparison]::Ordinal) } |
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
            $cases[$index].benchmarkId = $benchmarks[$index].benchmarkId
            $cases[$index].benchmark = $benchmarks[$index].benchmark
            $cases[$index].benchmarkDisplay = $benchmarks[$index].benchmarkDisplay
        }
    }
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

function Find-ExactSymbolDirectory([string]$TracePath, [string[]]$Candidates, [string]$FiltraceCommand) {
    $bestDirectory = $null
    $bestMappedFrames = 0
    foreach ($candidate in $Candidates) {
        try {
            $json = & $FiltraceCommand info $TracePath --symbols $candidate --format json 2>$null | Out-String
            if ($LASTEXITCODE -ne 0) { continue }
            $source = ($json | ConvertFrom-Json).result.sourceResolution
            if ($null -eq $source -or $source.matchingPdbModules.Count -eq 0) { continue }
            $mappedFrames = [int]$source.mappedManagedFrameCount
            if ($mappedFrames -gt $bestMappedFrames) {
                $bestMappedFrames = $mappedFrames
                $bestDirectory = $candidate
            }
        }
        catch {
            # A candidate that filtrace cannot read or match is simply not exact.
        }
    }

    return $bestDirectory
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

$repoRoot = (Get-Location).Path
if (-not $RunId) {
    $RunId = "$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
}
$runDirectory = Join-Path $repoRoot "BenchmarkDotNet.Artifacts/filtrace-runs/$RunId"
$artifacts = Join-Path $runDirectory 'artifacts'
$log = Join-Path $runDirectory 'capture.log'

function Test-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Recording an .etl is Windows-only, and Test-Elevated below calls a Windows-only API, so
# fail fast with a clear message rather than a PlatformNotSupportedException. Compare
# against $false so Windows PowerShell 5.1 (undefined $IsWindows) is not mistaken for a
# non-Windows OS.
if ($Profiler -eq 'ETW' -and $IsWindows -eq $false) {
    Write-Error 'ETW capture is Windows-only. Use -Profiler EP on this OS.' -ErrorAction Continue
    exit 1
}

# ETW kernel sessions require Administrator. When not elevated, relaunch this script
# in an elevated window that shows the capture's live progress, then wait for it.
# -WorkingDirectory anchors the child at the repo root so BenchmarkDotNet.Artifacts (and
# the capture log the parent tails) resolve there, not in the elevated shell's system32.
if ($Profiler -eq 'ETW' -and -not (Test-Elevated)) {
    Write-Host 'ETW capture needs Administrator; relaunching elevated (a UAC prompt will appear).' -ForegroundColor Yellow
    # Quote path/value args so a project path, filter, or process name containing spaces
    # survives Start-Process joining the array into a single command line.
    $argList = @('-NoProfile', '-File', "`"$PSCommandPath`"", '-Project', "`"$($projFile.FullName)`"",
        '-Filter', "`"$Filter`"", '-Profiler', 'ETW', '-Tfm', $Tfm, '-Process', "`"$Process`"", '-Top', $Top,
        '-RunId', $RunId, '-DotnetPath', "`"$DotnetPath`"", '-FiltracePath', "`"$FiltracePath`"")
    # Relaunch with the host that is ALREADY running this script, not a hardcoded 'pwsh' -
    # a caller on Windows PowerShell 5.1 without PowerShell 7 installed would otherwise
    # fail here with pwsh unresolved.
    $hostExe = (Get-Process -Id $PID).Path
    # Do NOT pass -Wait here. With -Verb RunAs, Start-Process -Wait can fail to release
    # after the elevated child self-closes, hanging the parent forever even though the
    # capture already finished and the .etl is on disk. Take the process object and wait on
    # it directly with a bounded WaitForExit, so a lost or access-denied handle degrades to
    # a timeout (the log still surfaces) instead of an indefinite hang.
    $proc = Start-Process -FilePath $hostExe -Verb RunAs -PassThru -WorkingDirectory $repoRoot -ArgumentList $argList
    if ($null -eq $proc) {
        Write-Error 'Elevated relaunch returned no process handle; cannot wait for the capture. Check for a blocked UAC prompt.' -ErrorAction Continue
        exit 1
    }
    # WaitForExit / HasExited / ExitCode can each throw (e.g. Access Denied reading the
    # elevated, higher-integrity child's handle). Under $ErrorActionPreference='Stop' an
    # uncaught throw would abort the script and reintroduce the very hang/no-tail failure
    # this fix avoids, so guard every handle access and treat a throw as a timeout-like miss.
    # Clamp to Int32.MaxValue so a large timeout cannot overflow the millisecond argument.
    $waitMs = [int][Math]::Min([long]$ElevatedTimeoutSeconds * 1000, [int]::MaxValue)
    $exited = $false
    try { $exited = $proc.WaitForExit($waitMs) } catch { $exited = $false }
    if (-not $exited) {
        Write-Warning "Elevated capture did not signal completion within $ElevatedTimeoutSeconds s; not blocking further. See $log for progress."
    }
    # ExitCode is only defined once the child has exited, and reading it on a higher-integrity
    # (elevated) process can throw Access Denied - treat either as 'not observed', non-fatal.
    $childExit = 0
    try { if ($proc.HasExited) { $childExit = $proc.ExitCode } } catch { $childExit = 0 }
    if ($childExit -ne 0) { Write-Error "Elevated capture failed (exit $childExit). See $log." -ErrorAction Continue ; exit $childExit }
    if (Test-Path $log) { Write-Host "`n--- capture log tail (full log: $log) ---" -ForegroundColor Cyan ; Get-Content $log -Tail 20 }
    exit 0
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
New-Item -ItemType Directory -Force -Path $lockDirectory | Out-Null
$lockPath = Join-Path $lockDirectory "$lockName.lock"
try {
    $captureLock = [System.IO.File]::Open(
        $lockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
}
catch [System.IO.IOException] {
    Write-Error "A capture is already active for project '$($projFile.FullName)' and TFM '$Tfm'. Wait for it to finish before starting another." -ErrorAction Continue
    exit 1
}

try {
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# Without BenchmarkDotNet.Diagnostics.Windows the `-p ETW` profiler silently resolves
# to UnresolvedDiagnoser and no .etl is written - fail fast with guidance.
if ($Profiler -eq 'ETW' -and -not (Select-String -Path $projFile.FullName -Pattern 'BenchmarkDotNet.Diagnostics.Windows' -Quiet)) {
    Write-Error "$($projFile.Name) does not reference BenchmarkDotNet.Diagnostics.Windows; -p ETW will no-op. Add the package first." -ErrorAction Continue
    exit 1
}

# Preserve the BenchmarkDotNet build output for source-symbol resolution under both
# profilers. Both branches are multi-element arrays, so they stay arrays (a
# single-element if-expression would unwrap to a scalar under Set-StrictMode).
$profArg = @('-p', $Profiler, '--keepFiles')
$benchmarkArguments = @('run', '-c', 'Release', '-f', $Tfm, '--project', $projFile.FullName, '--', '--filter', $Filter) +
    $profArg + @('--artifacts', $artifacts)
$startedUtc = [DateTimeOffset]::UtcNow

Write-Host "Capturing $Profiler trace: $Filter ($Tfm)..." -ForegroundColor Cyan
# Tee, do not redirect: an elevated window shows BenchmarkDotNet's live progress
# while the run is also logged for the parent window to surface.
& $DotnetPath @benchmarkArguments 2>&1 |
    Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { Write-Error "Benchmark run failed (exit $LASTEXITCODE). See $log." -ErrorAction Continue ; exit $LASTEXITCODE }

$captureCases = @(Get-CaptureCases $artifacts $Profiler)
if ($captureCases.Count -eq 0) {
    Write-Error "No capture files found in $artifacts. Did the capture run?" -ErrorAction Continue
    exit 1
}
Set-BenchmarkIdentities $captureCases $log

# The project build output carries matching PDBs for source lines; --keepFiles also
# preserves BenchmarkDotNet's generated build output for manual follow-up.
$symbols = Join-Path (Split-Path -Parent $projFile.FullName) "bin/Release/$Tfm"
$symbolCandidates = @(Get-SymbolCandidates $log $symbols)
$filtraceAvailable = $null -ne (Get-Command $FiltracePath -ErrorAction SilentlyContinue)
foreach ($captureCase in $captureCases) {
    $captureCase.symbolCandidates = $symbolCandidates
    if ($captureCase.trace -and $filtraceAvailable) {
        $captureCase.symbolsDirectory = Find-ExactSymbolDirectory $captureCase.trace $symbolCandidates $FiltracePath
    }
}
$methodFilter = $Filter.Trim('*')
if ([string]::IsNullOrWhiteSpace($methodFilter)) { $methodFilter = 'BenchmarkMethod' }

foreach ($captureCase in $captureCases) {
    if (-not $captureCase.trace) { continue }
    if ($Profiler -eq 'ETW') {
        Write-CaptureMetadata $captureCase.trace ([ordered]@{
            cpu = 'enabled'; threadtime = 'enabled'; classify = 'enabled';
            processes = 'enabled'; diskio = 'disabled'; events = 'enabled'
        })
    }
    else {
        # BenchmarkDotNet's default EventPipeProfile.CpuSampling enables the sample
        # profiler, CLR Default keywords, and its benchmark Engine provider.
        Write-CaptureMetadata $captureCase.trace ([ordered]@{
            cpu = 'enabled'; alloc = 'disabled'; exceptions = 'enabled';
            contention = 'enabled'; wait = 'disabled'; activity = 'enabled';
            gcstats = 'enabled'; jitstats = 'enabled'; threadpool = 'enabled';
            events = 'enabled'
        })
    }
}

$manifestPath = Join-Path $runDirectory 'manifest.json'
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
    runtimes = @(
        Get-Content -LiteralPath $log |
            Where-Object { $_ -match '^Runtime = ' } |
            Sort-Object -Unique
    )
    paths = [ordered]@{
        runDirectory = $runDirectory
        artifactsDirectory = $artifacts
        log = $log
    }
    cases = $captureCases
}
Write-RunManifest $manifestPath $manifest

Write-Host "`nCaptured $($captureCases.Count) case(s)." -ForegroundColor Green
Write-Host "Manifest: $manifestPath" -ForegroundColor Green
foreach ($captureCase in $captureCases) {
    $analysisPath = if ($captureCase.trace) { $captureCase.trace } else { $captureCase.speedscope }
    Write-Host "`nCase: $($captureCase.id)" -ForegroundColor Green
    Write-Host "Captured: $analysisPath"
    Write-Host "Next-step filtrace commands:"
    $exactSymbols = $captureCase.symbolsDirectory
    if ($Profiler -eq 'ETW') {
        # An .etl is machine-wide: narrow it to the benchmark process. Root-aware
        # rankings/exports also use --benchmark to exclude harness/overhead scaffolding.
        Write-Host "  filtrace processes `"$analysisPath`""
        Write-Host "  filtrace cpu `"$analysisPath`" --process `"$Process`" --benchmark --top $Top"
        Write-Host "  filtrace threadtime `"$analysisPath`" --process `"$Process`" --benchmark --top $Top"
        if ($exactSymbols) {
            Write-Host "  filtrace lines `"$analysisPath`" --process `"$Process`" --method `"$methodFilter`" --symbols `"$exactSymbols`""
        }
        else {
            Write-Host "  # source lines unavailable: no logged child output had an exact matching PDB"
        }
        Write-Host "  filtrace classify `"$analysisPath`" --process `"$Process`" --benchmark --native-symbols"
        $exportSymbols = if ($exactSymbols) { " --symbols `"$exactSymbols`"" } else { '' }
        Write-Host "  filtrace export `"$analysisPath`" --process `"$Process`" --benchmark --native-symbols$exportSymbols -o flame.speedscope.json"
    }
    elseif ($captureCase.trace) {
        Write-Host "  filtrace cpu `"$analysisPath`" --benchmark --top $Top"
        if ($exactSymbols) {
            Write-Host "  filtrace lines `"$analysisPath`" --method `"$methodFilter`" --symbols `"$exactSymbols`""
            Write-Host "  filtrace export `"$analysisPath`" --benchmark --symbols `"$exactSymbols`" -o flame.speedscope.json"
        }
        else {
            Write-Host "  # source lines unavailable: no logged child output had an exact matching PDB"
            Write-Host "  filtrace export `"$analysisPath`" --benchmark -o flame.speedscope.json"
        }
        Write-Host "  # alloc disabled by BenchmarkDotNet's default CpuSampling profile; recapture with EventPipeProfile.GcVerbose"
    }
    else {
        Write-Host "  filtrace cpu `"$analysisPath`" --benchmark --top $Top"
        Write-Host "  filtrace export `"$analysisPath`" --benchmark -o flame.speedscope.json"
    }
}
}
finally {
    $captureLock.Dispose()
}
