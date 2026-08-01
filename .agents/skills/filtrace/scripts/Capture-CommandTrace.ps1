<#
.SYNOPSIS
    Capture a matrix of short commands, each run repeatedly inside one ETW session, then
    write a capture manifest the filtrace batch and diff verbs read.

.DESCRIPTION
    A short command is the hardest thing to profile honestly. Starting and flushing an ETW
    session costs far more than a 30-100 ms process takes to run, so capturing one
    invocation yields a trace that is mostly capture overhead with too few samples to rank.
    This runs each scenario N times inside a single session: the session cost is paid once
    per scenario, one trace is converted instead of many thin ones, and the samples add up
    to something rankable.

    Each scenario becomes one case in a `manifest.json` of kind `command`, so
    `filtrace batch` ranks the whole matrix in one call and `filtrace diff` pairs two runs
    of it by scenario name. Every launch is recorded with its root process id and window,
    which is what lets an analysis attribute work to a particular run rather than to the
    scenario as a whole.

    ETW is machine-wide and needs Administrator, so this self-elevates once for the whole
    matrix rather than per scenario - one UAC prompt no matter how many scenarios run.

    A scenario that fails does not discard the run: it is recorded as a warning and the
    manifest is written with the cases that did succeed, so a long matrix is not lost to
    one bad command.

    filtrace: https://github.com/JeremyKuhne/filtrace - install once with
    `dotnet tool install -g KlutzyNinja.Filtrace`, or drive the MCP trace_* tools.

.PARAMETER Scenario
    The commands to capture. Each entry is a hashtable with Name, Command, and optional
    Arguments, for example:

        -Scenario @(
            @{ Name = 'version'; Command = 'dotnet'; Arguments = '--version' },
            @{ Name = 'build';   Command = 'dotnet'; Arguments = 'build --no-restore' })

    Names must be unique; they are the case identity that diff pairs on.

.PARAMETER Iterations
    How many times to launch each command inside its session. Default 25, which is enough
    for a 30-100 ms command to clear the sample count filtrace treats as directional.

.PARAMETER CaptureProfile
    Capture profile: startup (default), cpu, or threadtime. `startup` enables only the
    keywords a startup and CPU analysis reads, which is the lowest-perturbation choice for
    a short command.

.PARAMETER CpuSampleMSec
    CPU sample interval in milliseconds. Default 1 (the ETW default). A 30-100 ms command
    gets only tens of samples at 1 ms, so lower it - Windows honors down to about
    0.1221 ms - and the manifest records what was requested against what the machine
    actually honored.

.PARAMETER OutputDirectory
    Run directory for the traces, manifest, and log. Defaults to a run-stamped directory
    under ./perf-traces.

.PARAMETER SymbolsDirectory
    Optional build-output directory recorded against every case, so batch and diff resolve
    source lines without being told again.

.PARAMETER FiltracePath
    Path to a specific filtrace executable. Defaults to the one on PATH. Point this at a
    local build when testing changes to the tool itself, or when the installed version is
    older than the options this script needs.

.PARAMETER ElevatedTimeoutSeconds
    How long the non-elevated parent waits for the elevated child before giving up.
    Default 1800 (30 minutes).

.PARAMETER SpecPath
    Internal. Set on the elevated relaunch to point at the serialized scenario matrix.

.PARAMETER LogFile
    Internal. Set on the elevated relaunch so the calling console can surface the output.

.EXAMPLE
    ./Capture-CommandTrace.ps1 -Scenario @(@{ Name='version'; Command='dotnet'; Arguments='--version' })

.EXAMPLE
    ./Capture-CommandTrace.ps1 -Scenario $matrix -Iterations 50 -CaptureProfile cpu
#>
param(
    [hashtable[]]$Scenario,
    [ValidateRange(1, 1000)][int]$Iterations = 25,
    [ValidateSet('startup', 'cpu', 'threadtime')][string]$CaptureProfile = 'startup',
    [ValidateRange(0.01, 1000.0)][double]$CpuSampleMSec = 1.0,
    [string]$OutputDirectory,
    [string]$SymbolsDirectory,
    [string]$FiltracePath,
    [ValidateRange(1, 2147483647)][int]$ElevatedTimeoutSeconds = 1800,
    [string]$SpecPath,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'

function Test-Elevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Normalizes the caller's matrix into plain objects that survive the JSON round-trip to the
# elevated child, failing on the mistakes that would otherwise surface as a confusing
# capture error much later.
function Resolve-Scenarios([hashtable[]]$Entries) {
    if (-not $Entries -or $Entries.Count -eq 0) {
        throw 'Supply at least one -Scenario, for example @{ Name = ''version''; Command = ''dotnet''; Arguments = ''--version'' }.'
    }

    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $resolved = foreach ($entry in $Entries) {
        $name = [string]$entry['Name']
        $command = [string]$entry['Command']
        if ([string]::IsNullOrWhiteSpace($name)) { throw 'Every scenario needs a Name.' }
        if ([string]::IsNullOrWhiteSpace($command)) { throw "Scenario '$name' needs a Command." }
        if (-not $names.Add($name)) {
            throw "Scenario name '$name' is used more than once; names are the identity diff pairs on."
        }

        [pscustomobject]@{
            Name      = $name
            Command   = $command
            Arguments = [string]$entry['Arguments']
        }
    }

    return @($resolved)
}

# The launched command inherits filtrace's stdout, so its own output is interleaved with
# the JSON result - a scenario that prints anything would otherwise be dropped as
# unparseable and vanish from the manifest. The result is a single line written last, so
# scan back a line at a time; going character by character would work too but records a
# failed parse per attempt in the transcript, which reads like a broken run.
function Get-TrailingJson([string]$Text) {
    $lines = $Text -split "`r?`n"
    for ($line = $lines.Length - 1; $line -ge 0; $line--) {
        $start = $lines[$line].IndexOf('{')
        while ($start -ge 0) {
            try {
                return $lines[$line].Substring($start) | ConvertFrom-Json
            }
            catch {
                $start = $lines[$line].IndexOf('{', $start + 1)
            }
        }
    }

    return $null
}

if ($IsWindows -eq $false) {
    Write-Error 'ETW capture is Windows-only. Use dotnet-trace for an EventPipe capture on this OS.' -ErrorAction Continue
    exit 1
}

# The elevated child receives the matrix through a file rather than the command line: a
# scenario's arguments can contain quotes and spaces that would not survive Start-Process
# joining an argument array into one string.
if ($SpecPath) {
    $spec = Get-Content -LiteralPath $SpecPath -Raw | ConvertFrom-Json
    $scenarios = @($spec.scenarios)
    $Iterations = $spec.iterations
    $CaptureProfile = $spec.profile
    $CpuSampleMSec = $spec.cpuSampleMSec
    $OutputDirectory = $spec.outputDirectory
    $SymbolsDirectory = $spec.symbolsDirectory
    $FiltracePath = $spec.filtracePath
}
else {
    $scenarios = Resolve-Scenarios $Scenario
    if (-not $OutputDirectory) {
        $runStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
        $OutputDirectory = Join-Path (Get-Location).Path "perf-traces/command-$runStamp"
    }
}

$runDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

if (-not (Test-Elevated)) {
    # One elevation for the whole matrix. Elevating per scenario would prompt once per
    # command and leave a partially captured run behind on the first refusal.
    $spec = [ordered]@{
        scenarios        = $scenarios
        iterations       = $Iterations
        profile          = $CaptureProfile
        cpuSampleMSec    = $CpuSampleMSec
        outputDirectory  = $runDirectory
        symbolsDirectory = $SymbolsDirectory
        filtracePath     = $FiltracePath
    } | ConvertTo-Json -Depth 5
    $specFile = Join-Path $runDirectory 'scenarios.json'
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($specFile, $spec, $encoding)

    $childLog = Join-Path $runDirectory 'capture.log'
    Write-Host "Elevating to capture $($scenarios.Count) scenario(s) (a UAC prompt will appear)..." -ForegroundColor Yellow

    $argList = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
        '-SpecPath', "`"$specFile`"", '-LogFile', "`"$childLog`"")

    # Do NOT pass -Wait. With -Verb RunAs it can fail to release after the child closes,
    # hanging this runner; wait on the process object with a bounded timeout instead so a
    # lost handle degrades to a timeout rather than a hang.
    $proc = Start-Process pwsh -Verb RunAs -PassThru -WorkingDirectory $runDirectory -ArgumentList $argList
    if ($null -eq $proc) {
        Write-Error 'Elevated relaunch returned no process handle; check for a blocked UAC prompt.' -ErrorAction Continue
        exit 1
    }

    $waitMs = [int][Math]::Min([long]$ElevatedTimeoutSeconds * 1000, [int]::MaxValue)
    $exited = $false
    try { $exited = $proc.WaitForExit($waitMs) } catch { $exited = $false }

    if (Test-Path $childLog) {
        Write-Host "`n--- capture output ($childLog) ---" -ForegroundColor Cyan
        Get-Content $childLog
    }

    if (-not $exited) {
        Write-Error "The elevated capture did not finish within $ElevatedTimeoutSeconds s. See $childLog." -ErrorAction Continue
        exit 1
    }

    $childExit = 1
    try { $childExit = $proc.ExitCode } catch { $childExit = 1 }
    exit $childExit
}

# Elevated from here: run the matrix and write the manifest. The transcript is what the
# non-elevated parent surfaces once this window closes, so it has to start before any work.
$transcript = $LogFile ? $LogFile : (Join-Path $runDirectory 'capture.log')
try { Start-Transcript -Path $transcript -Force | Out-Null } catch { }
$startedUtc = [DateTimeOffset]::UtcNow

$filtrace = $FiltracePath
if (-not $filtrace) {
    $command = Get-Command filtrace -ErrorAction SilentlyContinue
    if (-not $command) {
        Write-Error 'filtrace is not on PATH. Install it with: dotnet tool install -g KlutzyNinja.Filtrace, or pass -FiltracePath.' -ErrorAction Continue
        exit 1
    }

    $filtrace = $command.Source
}

if (-not (Test-Path -LiteralPath $filtrace)) {
    Write-Error "filtrace was not found at '$filtrace'." -ErrorAction Continue
    exit 1
}

# Fail on a tool that predates the options this depends on, rather than letting every
# scenario fail one at a time with an unrecognized-option message.
$collectHelp = & $filtrace collect --help 2>&1 | Out-String
foreach ($required in '--iterations', '--format') {
    if ($collectHelp -notmatch [regex]::Escape($required)) {
        Write-Error "This filtrace build's collect verb has no $required option; update the tool." -ErrorAction Continue
        exit 1
    }
}

$cases = @()
$warnings = @()
foreach ($item in $scenarios) {
    $tracePath = Join-Path $runDirectory "$($item.Name).etl"
    Write-Host "Capturing '$($item.Name)': $($item.Command) $($item.Arguments) x$Iterations" -ForegroundColor Cyan

    $collectArgs = @(
        'collect',
        '--launch', $item.Command,
        '--output', $tracePath,
        '--profile', $CaptureProfile,
        '--cpu-ms', $CpuSampleMSec,
        '--iterations', $Iterations,
        '--format', 'json')
    if ($item.Arguments) { $collectArgs += @('--launch-args', $item.Arguments) }

    # The JSON result is what carries each launch; the human summary would have to be
    # parsed back, which is not a contract worth depending on.
    $raw = & $filtrace @collectArgs 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        $warnings += "Scenario '$($item.Name)' failed (exit $LASTEXITCODE): $($raw.Trim())"
        Write-Warning "Scenario '$($item.Name)' failed; continuing with the rest of the matrix."
        continue
    }

    $result = (Get-TrailingJson $raw)?.result
    if (-not $result -or -not $result.invocations) {
        $warnings += "Scenario '$($item.Name)' produced no readable collect result."
        Write-Warning "Scenario '$($item.Name)' produced no readable collect result; continuing."
        continue
    }

    $case = [ordered]@{
        id               = $item.Name
        benchmark        = $item.Name
        parameters       = ''
        benchmarkDisplay = "$($item.Command) $($item.Arguments)".Trim()
        trace            = $tracePath
        cpuSampleMSec    = if ($result.cpuSample) { $result.cpuSample.effectiveMSec } else { $CpuSampleMSec }
        invocations      = @($result.invocations | ForEach-Object {
                [ordered]@{
                    ordinal    = $_.ordinal
                    processId  = $_.processId
                    exitCode   = $_.exitCode
                    startedUtc = $_.startedUtc
                    stoppedUtc = $_.stoppedUtc
                }
            })
    }

    # Omitted rather than written empty: an unset [string] parameter is "", and the reader
    # rejects an empty optional field rather than treating it as absent.
    if ($SymbolsDirectory) { $case['symbolsDirectory'] = $SymbolsDirectory }
    $cases += $case

    # Windows honors the interval only inside the profile source's bounds and reports no
    # error outside them, so a clamp is only visible if the capture records it. Every
    # weight in the trace is scaled to the effective interval, not the requested one.
    if ($result.cpuSample) {
        $effectiveMSec = $result.cpuSample.effectiveMSec
        if ($result.cpuSample.clamped) {
            $warnings += ("Scenario '{0}' requested a {1} ms sample interval but this machine honors {2} to {3} ms; it sampled at {4} ms." -f `
                    $item.Name, $result.cpuSample.requestedMSec, $result.cpuSample.minimumMSec, $result.cpuSample.maximumMSec, $effectiveMSec)
            Write-Warning "Scenario '$($item.Name)' sampled at $effectiveMSec ms, not the requested $($result.cpuSample.requestedMSec) ms."
        }
    }
}

if ($cases.Count -eq 0) {
    Write-Error 'No scenario captured successfully; no manifest was written.' -ErrorAction Continue
    $warnings | ForEach-Object { Write-Host $_ }
    exit 1
}

$manifest = [ordered]@{
    schemaVersion    = 2
    kind             = 'command'
    startedUtc       = $startedUtc.ToString('O')
    completedUtc     = [DateTimeOffset]::UtcNow.ToString('O')
    profile          = $CaptureProfile
    iterations       = $Iterations
    cpuSampleMSec    = $CpuSampleMSec
    cases            = $cases
    warnings         = $warnings
}

# An .etl is machine-wide and a short command uses almost no CPU, so without a recorded
# process the analysis auto-scopes to whatever was busiest on the box - which is never the
# command being measured. One name only covers a matrix that launches one executable;
# anything else needs the per-invocation ids, which scoping does not yet consume.
$executables = @($scenarios | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Command) } | Sort-Object -Unique)
if ($executables.Count -eq 1) {
    $manifest['process'] = $executables[0]
}
else {
    $warnings += "Scenarios launch $($executables.Count) different executables ($($executables -join ', ')), so no manifest-wide process scope was recorded; pass --process or --pid when analyzing."
    $manifest['warnings'] = $warnings
}

$manifestPath = Join-Path $runDirectory 'manifest.json'
$encoding = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), $encoding)

Write-Host ""
Write-Host "Captured $($cases.Count) scenario(s) -> $manifestPath" -ForegroundColor Green
if ($warnings.Count -gt 0) {
    Write-Host "$($warnings.Count) scenario(s) failed:" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "Next-step filtrace commands:"
Write-Host "  filtrace batch `"$manifestPath`""
Write-Host "  filtrace diff `"<baseline>/manifest.json`" `"$manifestPath`""

try { Stop-Transcript | Out-Null } catch { }
