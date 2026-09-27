<#
.SYNOPSIS
    Capture a .NET perf trace (EventPipe or ETW) of an executable project running,
    then print the filtrace commands to analyze it. This helper drives the capture step:
    EventPipe via dotnet-trace, ETW via the filtrace collect verb.

.DESCRIPTION
    Wraps the "build the project, run its output under a profiler, then analyze the
    trace" loop for an ordinary executable project - a console app, worker, or web
    host. It is the whole-application counterpart to Capture-BenchmarkTrace.ps1,
    which profiles a BenchmarkDotNet micro-benchmark.

    It builds the project, resolves the actual run target from the build output, and
    launches THAT under the profiler - never `dotnet run`. `dotnet run` builds and
    then forks your app into a separate child process, so a single-process EventPipe
    session would trace the build/run host instead of your code. Launching the built
    apphost (or `dotnet <app>.dll`) directly keeps the trace on the app itself.

      - EventPipe (-Profiler EP, the default): cross-platform, no elevation, single
        process. Runs `dotnet-trace collect -- <app>` and writes a .nettrace. Pass
        -Metric alloc for a gc-verbose (allocation) capture instead of CPU sampling.
      - ETW (-Profiler ETW): Windows only, self-elevates (one UAC prompt),
        machine-wide, via `filtrace collect` (TraceEvent, no external recorder). Only an
        .etl carries wall-clock (threadtime), the native GC / JIT / memcpy split
        (classify --native-symbols), and multi-process scoping.

    The app runs to completion (launch-only); the profiler stops when it exits. The
    printed filtrace commands are pre-scoped: an EventPipe trace ranks the whole
    process; an .etl additionally uses --process, because an .etl is machine-wide.

    filtrace: https://github.com/JeremyKuhne/filtrace - install once with
    `dotnet tool install -g KlutzyNinja.Filtrace`, or drive the MCP trace_* tools.

.PARAMETER Project
    Path to the executable project - a .csproj or the directory holding one. Required.

.PARAMETER Profiler
    'EP' (EventPipe, default) or 'ETW' (Windows, self-elevating).

.PARAMETER Metric
    'cpu' (default) or 'alloc'. EventPipe only: 'alloc' captures the gc-verbose
    allocation profile instead of CPU sampling. An ETW capture always records CPU
    and wall-clock (threadtime) together. Allocation bytes identify potential
    work and deferred GC pressure, not elapsed or CPU time saved; compare matching
    workload timings and GC costs before claiming a performance improvement.

.PARAMETER Tfm
    Target-framework moniker to build and run. Default net10.0.

.PARAMETER Configuration
    Build configuration. Default Release - a perf trace should profile optimized output.

.PARAMETER AppArgs
    Arguments passed to the application after the profiler launches it.

.PARAMETER Top
    Rows per ranking in the printed commands. Default 25.

.PARAMETER Output
    Trace output path. Defaults to ./perf-traces/<AssemblyName>.<nettrace|etl>.

.PARAMETER DotnetTracePath
    dotnet-trace path or command name. Defaults to dotnet-trace from PATH. EventPipe
    capture only; an explicit missing value fails instead of installing another tool.

.PARAMETER ElevatedTimeoutSeconds
    How long the non-elevated parent waits for the self-elevated ETW capture to finish
    before it stops blocking. Default 1200 (20 minutes). Only the ETW self-elevation path
    uses it - it is the backstop that keeps a never-signaled elevated child from hanging
    the parent indefinitely.

.PARAMETER EventPipeTimeoutSeconds
    Optional wall-clock deadline for the EventPipe collector after launch. Only valid
    with -Profiler EP under PowerShell 7 or later. Omitted means the existing
    wait-until-exit behavior, including under Windows PowerShell 5.1. On timeout
    the helper requests termination of its own collector tree, exits nonzero, and
    retains a labeled partial trace plus <trace>.capture-attempt.json. It does not
    publish successful capture metadata or analysis commands. This bounds the
    collector wait, not the application's work: a zero recorder exit alone does
    not prove a child operation completed.

.EXAMPLE
    ./Capture-ProjectTrace.ps1 -Project src/MyApp

.EXAMPLE
    ./Capture-ProjectTrace.ps1 -Project src/MyApp -Metric alloc -AppArgs '--input','big.json'

.EXAMPLE
    ./Capture-ProjectTrace.ps1 -Project src/MyApp -Profiler EP -EventPipeTimeoutSeconds 120

.EXAMPLE
    ./Capture-ProjectTrace.ps1 -Project src/MyApp -Profiler ETW
#>
param(
    [Parameter(Mandatory)][string]$Project,
    [ValidateSet('EP', 'ETW')][string]$Profiler = 'EP',
    [ValidateSet('cpu', 'alloc')][string]$Metric = 'cpu',
    [string]$Tfm = 'net10.0',
    [string]$Configuration = 'Release',
    [string[]]$AppArgs = @(),
    [int]$Top = 25,
    [string]$Output,
    [string]$DotnetTracePath = 'dotnet-trace',
    [ValidateRange(1, 2147483647)][int]$ElevatedTimeoutSeconds = 1200,
    [ValidateRange(1, 86400)][int]$EventPipeTimeoutSeconds
)

$ErrorActionPreference = 'Stop'
$boundedEventPipe = $PSBoundParameters.ContainsKey('EventPipeTimeoutSeconds')
if ($boundedEventPipe -and $Profiler -ne 'EP') {
    Write-Error '-EventPipeTimeoutSeconds is valid only with -Profiler EP.' -ErrorAction Continue
    exit 1
}
if ($boundedEventPipe -and $PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error '-EventPipeTimeoutSeconds requires PowerShell 7 or later (pwsh). Omit it to use the existing uncapped capture under Windows PowerShell 5.1.' -ErrorAction Continue
    exit 1
}

. (Join-Path $PSScriptRoot 'Get-DotnetTraceRecorder.ps1')

function Invoke-BoundedEventPipeCapture(
    [string]$Command,
    [string[]]$Arguments,
    [int]$TimeoutSeconds) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = (Get-Location).Path
    $invocationArgs = [System.Collections.Generic.List[string]]::new()
    if ($Command.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
        $startInfo.FileName = (Get-Process -Id $PID).Path
        foreach ($value in @('-NoProfile', '-NonInteractive', '-File', $Command)) {
            $invocationArgs.Add($value)
        }
    }
    else {
        $startInfo.FileName = $Command
    }

    foreach ($value in $Arguments) {
        if ($null -eq $value) { throw 'EventPipe recorder arguments cannot contain null.' }
        $invocationArgs.Add($value)
    }

    foreach ($value in $invocationArgs) { $startInfo.ArgumentList.Add($value) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start EventPipe recorder '$Command'." }
        $collectorPid = $process.Id
        $waitMilliseconds = [int][Math]::Min([long]$TimeoutSeconds * 1000, [int]::MaxValue)
        if ($process.WaitForExit($waitMilliseconds)) {
            return [pscustomobject]@{
                TimedOut = $false
                ExitCode = $process.ExitCode
                ProcessId = $collectorPid
                Cleanup = 'not-needed'
                CollectorStopped = $true
                CleanupError = $null
            }
        }

        $cleanup = 'tree-termination-requested'
        $cleanupError = $null
        try { $process.Kill($true) }
        catch {
            $cleanup = 'termination-failed'
            $cleanupError = $_.Exception.Message
        }

        $collectorStopped = $false
        try { $collectorStopped = $process.WaitForExit(5000) }
        catch {
            $cleanup = 'termination-unverified'
            $cleanupError = $_.Exception.Message
        }
        return [pscustomobject]@{
            TimedOut = $true
            ExitCode = $null
            ProcessId = $collectorPid
            Cleanup = $cleanup
            CollectorStopped = $collectorStopped
            CleanupError = $cleanupError
        }
    }
    finally {
        $process.Dispose()
    }
}

function Write-EventPipeCaptureAttempt(
    [string]$TracePath,
    [string]$Status,
    [string]$Reason,
    [object]$Recorder,
    [object]$Result,
    [int]$TimeoutSeconds) {
    $attemptPath = "$TracePath.capture-attempt.json"
    $temporaryPath = "$attemptPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $traceFile = Get-Item -LiteralPath $TracePath -ErrorAction SilentlyContinue
    $attempt = [ordered]@{
        schemaVersion = 1
        status = $Status
        reason = $Reason
        tracePath = [System.IO.Path]::GetFullPath($TracePath)
        traceBytes = if ($null -ne $traceFile -and -not $traceFile.PSIsContainer) { [long]$traceFile.Length } else { $null }
        recorder = [ordered]@{
            name = 'dotnet-trace'
            version = $Recorder.Version
            profiles = @($Recorder.Metadata.profiles)
            processId = if ($null -ne $Result) { $Result.ProcessId } else { $null }
            exitCode = if ($null -ne $Result) { $Result.ExitCode } else { $null }
        }
        timeoutSeconds = $TimeoutSeconds
        subjectCompletion = 'unverified'
        cleanup = if ($null -ne $Result) {
            [ordered]@{
                action = $Result.Cleanup
                collectorStopped = $Result.CollectorStopped
                error = $Result.CleanupError
                descendantsMayRemain = ($Result.TimedOut -eq $true)
            }
        }
        else { $null }
    }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    try {
        [System.IO.File]::WriteAllText($temporaryPath, ($attempt | ConvertTo-Json -Depth 5 -Compress), $encoding)
        [System.IO.File]::Move($temporaryPath, $attemptPath)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            [System.IO.File]::Delete($temporaryPath)
        }
    }
    return $attemptPath
}

function Write-CaptureMetadata(
    [string]$TracePath,
    [System.Collections.IDictionary]$Analyses,
    [System.Collections.IDictionary]$Recorder = $null) {
    $sidecar = [ordered]@{
        schemaVersion = 1
        analyses = $Analyses
    }
    if ($null -ne $Recorder) {
        $sidecar['recorder'] = $Recorder
    }

    $metadata = $sidecar | ConvertTo-Json -Depth 4 -Compress
    $encoding = New-Object System.Text.UTF8Encoding($false)
    try {
        [System.IO.File]::WriteAllText("$TracePath.filtrace.json", $metadata, $encoding)
    }
    catch {
        Write-Warning "Capture succeeded, but metadata could not be written: $($_.Exception.Message). Provider enablement will be unknown during analysis."
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

# Reading an .etl (and recording one via a kernel ETW session) is Windows-only.
# Compare against $false so Windows PowerShell 5.1 (where $IsWindows is undefined) is
# not mistaken for a non-Windows OS.
if ($Profiler -eq 'ETW' -and $IsWindows -eq $false) {
    Write-Error 'ETW capture is Windows-only. Use -Profiler EP on this OS.' -ErrorAction Continue
    exit 1
}

function Test-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# filtrace records the ETW session itself (no external recorder); it installs as a global
# tool under ~/.dotnet/tools, which a freshly elevated shell may not have on PATH yet.
# Check it in the normal console (before any elevation) so the install hint is not buried
# in a UAC window that then closes.
$toolsDir = Join-Path $HOME '.dotnet/tools'
if ((Test-Path $toolsDir) -and ($env:PATH -notlike "*$toolsDir*")) {
    $env:PATH = "$toolsDir$([System.IO.Path]::PathSeparator)$env:PATH"
}
if ($Profiler -eq 'ETW' -and -not (Get-Command filtrace -ErrorAction SilentlyContinue)) {
    Write-Error 'filtrace not found. Install it (dotnet tool install -g KlutzyNinja.Filtrace), then re-run.' -ErrorAction Continue
    exit 1
}

$dotnetTraceRecorder = $null
if ($Profiler -eq 'EP') {
    $dotnetTraceCommand = Get-Command $DotnetTracePath -ErrorAction SilentlyContinue
    if ($null -eq $dotnetTraceCommand) {
        if (-not [string]::Equals($DotnetTracePath, 'dotnet-trace', [StringComparison]::OrdinalIgnoreCase)) {
            Write-Error "dotnet-trace override could not be resolved: '$DotnetTracePath'." -ErrorAction Continue
            exit 1
        }

        Write-Host 'dotnet-trace not found; installing the global tool...' -ForegroundColor Yellow
        dotnet tool install --global dotnet-trace | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Error 'Failed to install dotnet-trace. Install it manually: dotnet tool install -g dotnet-trace.' -ErrorAction Continue
            exit 1
        }

        $dotnetTraceCommand = Get-Command dotnet-trace -ErrorAction SilentlyContinue
        if ($null -eq $dotnetTraceCommand) {
            Write-Error 'dotnet-trace installed but could not be resolved on PATH.' -ErrorAction Continue
            exit 1
        }
    }

    try {
        $dotnetTraceRecorder = Get-DotnetTraceRecorder $dotnetTraceCommand.Source $Metric
    }
    catch {
        Write-Error "dotnet-trace capture preflight failed: $($_.Exception.Message)" -ErrorAction Continue
        exit 1
    }
}

# ETW kernel sessions require Administrator. When not elevated, relaunch this script
# in an elevated window; -WorkingDirectory keeps the default output path (and any
# relative -Output) resolving against the caller's directory, not system32.
if ($Profiler -eq 'ETW' -and -not (Test-Elevated)) {
    Write-Host 'ETW capture needs Administrator; relaunching elevated (a UAC prompt will appear).' -ForegroundColor Yellow
    # Quote path/value args so a project path, output path, or app argument containing
    # spaces survives Start-Process joining the array into a single command line.
    $argList = @('-NoProfile', '-File', "`"$PSCommandPath`"", '-Project', "`"$($projFile.FullName)`"",
        '-Profiler', 'ETW', '-Metric', $Metric, '-Tfm', $Tfm, '-Configuration', "`"$Configuration`"", '-Top', $Top)
    if ($Output) { $argList += @('-Output', "`"$Output`"") }
    if ($AppArgs.Count -gt 0) { $argList += @('-AppArgs') + ($AppArgs | ForEach-Object { "`"$_`"" }) }
    # Relaunch with the host that is ALREADY running this script, not a hardcoded 'pwsh' -
    # a caller on Windows PowerShell 5.1 without PowerShell 7 installed would otherwise
    # fail here with pwsh unresolved.
    $hostExe = (Get-Process -Id $PID).Path
    # Do NOT pass -Wait here. With -Verb RunAs, Start-Process -Wait can fail to release
    # after the elevated child self-closes, hanging the parent forever even though the
    # capture already finished. Wait on the returned process object directly with a bounded
    # WaitForExit, so a lost or access-denied handle degrades to a timeout, not a hang.
    $proc = Start-Process -FilePath $hostExe -Verb RunAs -PassThru -WorkingDirectory (Get-Location).Path -ArgumentList $argList
    if ($null -eq $proc) {
        Write-Error 'Elevated relaunch returned no process handle; cannot wait for the capture. Check for a blocked UAC prompt.' -ErrorAction Continue
        exit 1
    }
    # WaitForExit / HasExited / ExitCode can each throw (e.g. Access Denied reading the
    # elevated, higher-integrity child's handle). Under $ErrorActionPreference='Stop' an
    # uncaught throw would abort the script and reintroduce the very hang this fix avoids,
    # so guard every handle access and treat a throw as a timeout-like miss. Clamp to
    # Int32.MaxValue so a large timeout cannot overflow the millisecond argument.
    $waitMs = [int][Math]::Min([long]$ElevatedTimeoutSeconds * 1000, [int]::MaxValue)
    $exited = $false
    try { $exited = $proc.WaitForExit($waitMs) } catch { $exited = $false }
    if (-not $exited) {
        Write-Warning "Elevated capture did not signal completion within $ElevatedTimeoutSeconds s; not blocking further."
    }
    # ExitCode is only defined once the child has exited, and reading it on a higher-integrity
    # (elevated) process can throw Access Denied - treat either as 'not observed', non-fatal.
    $childExit = 0
    try { if ($proc.HasExited) { $childExit = $proc.ExitCode } } catch { $childExit = 0 }
    if ($childExit -ne 0) { Write-Error "Elevated capture failed (exit $childExit)." -ErrorAction Continue ; exit $childExit }
    exit 0
}

Write-Host "Building $($projFile.Name) ($Configuration, $Tfm)..." -ForegroundColor Cyan
dotnet build $projFile.FullName -c $Configuration -f $Tfm | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed (exit $LASTEXITCODE)." -ErrorAction Continue ; exit $LASTEXITCODE }

# Resolve the built assembly, its name, and the output kind in one evaluation. With
# more than one -getProperty the SDK returns JSON, so parse the Properties object.
$propsJson = dotnet msbuild $projFile.FullName -getProperty:TargetPath -getProperty:AssemblyName `
    -getProperty:OutputType "-p:Configuration=$Configuration" "-p:TargetFramework=$Tfm" 2>$null | Out-String
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($propsJson)) {
    Write-Error "Could not read build properties from $($projFile.Name) (dotnet msbuild -getProperty failed). Ensure the project restores and builds for $Configuration/$Tfm." -ErrorAction Continue
    exit 1
}
$props = ($propsJson | ConvertFrom-Json).Properties
$targetPath = $props.TargetPath
$assemblyName = $props.AssemblyName
$outputType = $props.OutputType

if ([string]::IsNullOrWhiteSpace($targetPath)) {
    Write-Error "Could not resolve the build output (TargetPath) for $($projFile.Name)." -ErrorAction Continue
    exit 1
}
if ($outputType -notin @('Exe', 'WinExe')) {
    Write-Error "$($projFile.Name) is a '$outputType' project, not an executable. Point at an app project (OutputType Exe)." -ErrorAction Continue
    exit 1
}

# Prefer the apphost so the process carries the app's own name (an .etl is
# machine-wide, and `--process $assemblyName` is far easier to scope than `dotnet`).
# Fall back to `dotnet <app>.dll` when no apphost was produced (UseAppHost=false).
$outputDir = Split-Path -Parent $targetPath
# Treat an undefined $IsWindows (Windows PowerShell 5.1) as Windows, matching the ETW
# guard above, so the .exe apphost is still found there.
$exeSuffix = ''
if ($IsWindows -ne $false) { $exeSuffix = '.exe' }
$appHost = Join-Path $outputDir ($assemblyName + $exeSuffix)
if (Test-Path -LiteralPath $appHost) {
    $runExe = $appHost
    $runPrefixArgs = @()
    $processName = $assemblyName
}
else {
    $runExe = 'dotnet'
    $runPrefixArgs = @($targetPath)
    $processName = 'dotnet'
}

# The build output directory holds the portable PDBs that resolve source lines.
$symbols = $outputDir

# Default the trace path under ./perf-traces (created on demand).
if (-not $Output) {
    $captureDir = Join-Path (Get-Location).Path 'perf-traces'
    New-Item -ItemType Directory -Force -Path $captureDir | Out-Null
    $ext = 'nettrace'
    if ($Profiler -eq 'ETW') { $ext = 'etl' }
    $name = $assemblyName
    if ($boundedEventPipe) {
        $name = "$assemblyName-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N'))"
    }
    $Output = Join-Path $captureDir "$name.$ext"
}

if ($boundedEventPipe) {
    foreach ($path in @($Output, "$Output.filtrace.json", "$Output.capture-attempt.json")) {
        if (Test-Path -LiteralPath $path) {
            Write-Error "EventPipe output '$path' already exists; use a new -Output to preserve prior capture evidence." -ErrorAction Continue
            exit 1
        }
    }
    $outputDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Output))
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

if ($Profiler -eq 'EP') {
    $traceProfile = $dotnetTraceRecorder.ProfileArgument

    Write-Host "Capturing EventPipe ($Metric) trace of $processName..." -ForegroundColor Cyan
    # Launch the built app directly (never `dotnet run`) so this single-process
    # EventPipe session records the app, not a separate build/run host process.
    $collectArgs = @('collect', '--output', $Output, '--profile', $traceProfile, '--', $runExe)
    $collectArgs += $runPrefixArgs
    $collectArgs += $AppArgs
    if ($boundedEventPipe) {
        try {
            $recorderResult = Invoke-BoundedEventPipeCapture $dotnetTraceRecorder.Command $collectArgs $EventPipeTimeoutSeconds
        }
        catch {
            $reason = "Could not start or wait for dotnet-trace: $($_.Exception.Message)"
            $attemptPath = Write-EventPipeCaptureAttempt $Output 'recorder-error' $reason $dotnetTraceRecorder $null $EventPipeTimeoutSeconds
            Write-Error "$reason. Capture attempt: $attemptPath." -ErrorAction Continue
            exit 1
        }

        if ($recorderResult.TimedOut) {
            $reason = "EventPipe recorder exceeded $EventPipeTimeoutSeconds s"
            $attemptPath = Write-EventPipeCaptureAttempt $Output 'timeout' $reason $dotnetTraceRecorder $recorderResult $EventPipeTimeoutSeconds
            Write-Error "$reason. Trace may be partial; the subject or its descendants may still be running. For managed children, check inherited DOTNET_DiagnosticPorts. Capture attempt: $attemptPath." -ErrorAction Continue
            exit 1
        }
        if ($recorderResult.ExitCode -ne 0) {
            $reason = "dotnet-trace failed (exit $($recorderResult.ExitCode))"
            $attemptPath = Write-EventPipeCaptureAttempt $Output 'recorder-failed' $reason $dotnetTraceRecorder $recorderResult $EventPipeTimeoutSeconds
            Write-Error "$reason. Trace may be partial. Capture attempt: $attemptPath." -ErrorAction Continue
            exit $recorderResult.ExitCode
        }
        $traceFile = Get-Item -LiteralPath $Output -ErrorAction SilentlyContinue
        if ($null -eq $traceFile -or $traceFile.PSIsContainer -or $traceFile.Length -eq 0) {
            $reason = 'dotnet-trace exited successfully without a nonempty trace'
            $attemptPath = Write-EventPipeCaptureAttempt $Output 'empty-trace' $reason $dotnetTraceRecorder $recorderResult $EventPipeTimeoutSeconds
            Write-Error "$reason. Capture attempt: $attemptPath." -ErrorAction Continue
            exit 1
        }
    }
    else {
        & $dotnetTraceRecorder.Command @collectArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Error "dotnet-trace failed (exit $LASTEXITCODE)." -ErrorAction Continue ; exit $LASTEXITCODE }
    }

    if ($Metric -eq 'alloc') {
        # gc-verbose establishes allocation and GC events, but dotnet-trace profile
        # composition varies across tool versions; leave other families unknown.
        $captureAnalyses = [ordered]@{
            alloc = 'enabled'; gcstats = 'enabled'; events = 'enabled'
        }
        Write-CaptureMetadata $Output $captureAnalyses $dotnetTraceRecorder.Metadata
    }
    else {
        # The selected profile establishes CPU, but profile composition varies across
        # tool versions; leave other runtime families unknown.
        $captureAnalyses = [ordered]@{ cpu = 'enabled'; events = 'enabled' }
        Write-CaptureMetadata $Output $captureAnalyses $dotnetTraceRecorder.Metadata
    }
}
else {
    Write-Host "Capturing ETW (CPU + threadtime) trace of $processName via filtrace collect..." -ForegroundColor Cyan
    # filtrace records the ETW session itself with TraceEvent - no PerfView or wpr. It
    # launches the built app, captures CPU + context-switch (threadtime) stacks with managed
    # method names, and writes the machine-wide .etl the analysis verbs read.
    # filtrace collect takes a single command-line string; quote any element that has
    # whitespace (escaping embedded quotes) so argument boundaries survive the join, the
    # way the EventPipe path preserves them by passing an array.
    $launchArgs = (@($runPrefixArgs) + $AppArgs | ForEach-Object {
            if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
        }) -join ' '
    $collectArgs = @('collect', '--launch', $runExe, '--output', $Output, '--profile', 'threadtime')
    if ($launchArgs) { $collectArgs += @('--launch-args', $launchArgs) }
    filtrace @collectArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Error "filtrace collect failed (exit $LASTEXITCODE)." -ErrorAction Continue ; exit $LASTEXITCODE }
    Write-CaptureMetadata $Output ([ordered]@{
        cpu = 'enabled'; threadtime = 'enabled'; classify = 'enabled';
        processes = 'enabled'; diskio = 'disabled'; events = 'enabled'
    })
}

Write-Host "`nCaptured: $Output" -ForegroundColor Green
Write-Host "`nNext-step filtrace commands:" -ForegroundColor Green
if ($Profiler -eq 'ETW') {
    # An .etl is machine-wide: scope every query to the captured process.
    Write-Host "  filtrace processes `"$Output`""
    Write-Host "  filtrace rank `"$Output`" --metric cpu --process $processName --top $Top"
    Write-Host "  filtrace rank `"$Output`" --metric threadtime --process $processName --top $Top"
    Write-Host "  filtrace source `"$Output`" --view lines --process $processName --symbols `"$symbols`""
    Write-Host "  filtrace classify `"$Output`" --process $processName --native-symbols"
}
else {
    # A single-process EventPipe trace ranks the whole app; there is no harness.
    if ($Metric -eq 'alloc') {
        Write-Host "  filtrace rank `"$Output`" --metric alloc --top $Top"
        Write-Host "  filtrace report `"$Output`" --kind gc"
    }
    else {
        Write-Host "  filtrace rank `"$Output`" --metric cpu --top $Top"
        Write-Host "  filtrace source `"$Output`" --view lines --symbols `"$symbols`""
        Write-Host "  # scope past runtime startup with --root <Type>.<Method> once you see the ranking"
    }
}
