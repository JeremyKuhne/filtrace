[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FastTraceRepoRoot,

    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$fastTraceRoot = (Resolve-Path -LiteralPath $FastTraceRepoRoot).Path
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)

if (Test-Path -LiteralPath $outputRoot) {
    throw "OutputDirectory must be a new owned directory: $outputRoot"
}

$hostOs = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    'win'
} elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)) {
    'linux'
} elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)) {
    'osx'
} else {
    throw 'The host operating system is not supported by this check.'
}

$hostArchitecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    ([System.Runtime.InteropServices.Architecture]::X64) { 'x64' }
    ([System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' }
    default { throw "The host OS architecture '$($_)' is not supported by this check." }
}

$hostRuntimeIdentifier = "$hostOs-$hostArchitecture"
if ($RuntimeIdentifier -ne $hostRuntimeIdentifier) {
    throw "RuntimeIdentifier '$RuntimeIdentifier' does not match native host '$hostRuntimeIdentifier'."
}

$logsDirectory = Join-Path $outputRoot 'logs'
$resultsDirectory = Join-Path $outputRoot 'results'
$rawDirectory = Join-Path $outputRoot 'raw'
$jitPublishDirectory = Join-Path $outputRoot 'build/jit-publish'
$nativePublishDirectory = Join-Path $outputRoot 'build/native-publish'

foreach ($directory in @($logsDirectory, $resultsDirectory, $rawDirectory)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Format-CommandLine {
    param([string] $FilePath, [string[]] $Arguments)

    $quotedArguments = $Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + $_.Replace('"', '\"') + '"'
        } else {
            $_
        }
    }

    return "$FilePath $($quotedArguments -join ' ')"
}

function Invoke-CapturedProcess {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [string] $WorkingDirectory,
        [string] $StandardOutputPath,
        [string] $StandardErrorPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start: $(Format-CommandLine $FilePath $Arguments)"
    }

    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
    $standardError = $standardErrorTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()

    [System.IO.File]::WriteAllText($StandardOutputPath, $standardOutput, $utf8NoBom)
    [System.IO.File]::WriteAllText($StandardErrorPath, $standardError, $utf8NoBom)
    return $exitCode
}

function Invoke-Build {
    param([string[]] $Arguments, [string] $LogPath)

    $stdoutPath = "$LogPath.stdout"
    $stderrPath = "$LogPath.stderr"
    $exitCode = Invoke-CapturedProcess 'dotnet' $Arguments $root $stdoutPath $stderrPath
    $log = @(
        (Format-CommandLine 'dotnet' $Arguments)
        '--- stdout ---'
        [System.IO.File]::ReadAllText($stdoutPath)
        '--- stderr ---'
        [System.IO.File]::ReadAllText($stderrPath)
    ) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($LogPath, $log, $utf8NoBom)
    Remove-Item -LiteralPath $stdoutPath, $stderrPath

    if ($exitCode -ne 0) {
        throw "Build failed with exit code $exitCode. See $LogPath"
    }
}

function Invoke-GitRevision {
    param([string] $RepositoryRoot)

    $stdoutPath = Join-Path $logsDirectory "git-$([System.IO.Path]::GetFileName($RepositoryRoot)).stdout"
    $stderrPath = "$stdoutPath.stderr"
    $exitCode = Invoke-CapturedProcess 'git' @('-C', $RepositoryRoot, 'rev-parse', 'HEAD') $root $stdoutPath $stderrPath
    if ($exitCode -ne 0) {
        throw "git rev-parse failed for '$RepositoryRoot'."
    }

    $revision = [System.IO.File]::ReadAllText($stdoutPath).Trim()
    Remove-Item -LiteralPath $stdoutPath, $stderrPath
    return $revision
}

$fixtureDirectory = Join-Path $root 'tests/Filtrace.Core.Tests/Fixtures'
foreach ($fixture in @('activity.nettrace', 'exceptions.nettrace', 'alloc.nettrace', 'jit.nettrace')) {
    Copy-Item -LiteralPath (Join-Path $fixtureDirectory $fixture) -Destination (Join-Path $rawDirectory $fixture)
}

$project = Join-Path $root 'src/Filtrace/Filtrace.csproj'
$jitArtifacts = Join-Path $outputRoot 'build/jit-artifacts'
$nativeArtifacts = Join-Path $outputRoot 'build/native-artifacts'

$jitArguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '-p:PublishAot=false',
    '-p:SelfContained=false',
    '-p:PackAsTool=false',
    '-p:IsPackable=false',
    "-p:FastTraceRepoRoot=$fastTraceRoot",
    "-p:ArtifactsPath=$jitArtifacts",
    '--output', $jitPublishDirectory
)
Invoke-Build $jitArguments (Join-Path $logsDirectory 'jit-publish.log')

$nativeArguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '-p:PublishAot=true',
    '-p:PackAsTool=false',
    '-p:IsPackable=false',
    '-p:RestoreLockedMode=false',
    "-p:FastTraceRepoRoot=$fastTraceRoot",
    "-p:ArtifactsPath=$nativeArtifacts",
    '--output', $nativePublishDirectory
)
Invoke-Build $nativeArguments (Join-Path $logsDirectory 'native-publish.log')

$dotnetInfoStdout = Join-Path $logsDirectory 'dotnet-info.log'
$dotnetInfoStderr = Join-Path $logsDirectory 'dotnet-info.stderr.log'
$dotnetInfoExit = Invoke-CapturedProcess 'dotnet' @('--info') $root $dotnetInfoStdout $dotnetInfoStderr
if ($dotnetInfoExit -ne 0) {
    throw "dotnet --info failed with exit code $dotnetInfoExit."
}

$jitDll = Join-Path $jitPublishDirectory 'filtrace.dll'
$jitDeps = Join-Path $jitPublishDirectory 'filtrace.deps.json'
$nativeExecutableName = if ($hostOs -eq 'win') { 'filtrace.exe' } else { 'filtrace' }
$nativeExecutable = Join-Path $nativePublishDirectory $nativeExecutableName
foreach ($requiredFile in @($jitDll, $jitDeps, $nativeExecutable)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Expected output was not produced: $requiredFile"
    }
}

$deps = Get-Content -LiteralPath $jitDeps -Raw | ConvertFrom-Json -Depth 100
$traceEventLibraries = @($deps.libraries.PSObject.Properties.Name | Where-Object {
    $_ -like 'Microsoft.Diagnostics.Tracing.TraceEvent/*'
})
if ($traceEventLibraries.Count -ne 0) {
    throw "The JIT dependency graph contains TraceEvent: $($traceEventLibraries -join ', ')"
}

$nativeAssetsPath = Join-Path $nativeArtifacts 'obj/Filtrace/project.assets.json'
$nativeAssets = Get-Content -LiteralPath $nativeAssetsPath -Raw | ConvertFrom-Json -Depth 100
$nativeLibraries = @($nativeAssets.libraries.PSObject.Properties)
$fastTraceProjects = @($nativeLibraries | Where-Object {
    $_.Name -like 'FastTrace/*' -and $_.Value.type -eq 'project'
})
$nativeTraceEventPackages = @($nativeLibraries | Where-Object {
    $_.Name -like 'Microsoft.Diagnostics.Tracing.TraceEvent/*'
})
if ($fastTraceProjects.Count -ne 1 -or $nativeTraceEventPackages.Count -ne 0) {
    throw 'The native restore graph must reference the FastTrace project without a TraceEvent package.'
}

if (Test-Path -LiteralPath (Join-Path $nativePublishDirectory 'filtrace.dll')) {
    throw 'The native publish output contains filtrace.dll.'
}
if (Test-Path -LiteralPath (Join-Path $nativePublishDirectory 'filtrace.deps.json')) {
    throw 'The native publish output contains filtrace.deps.json.'
}
$nativeTraceEventFiles = @(Get-ChildItem -LiteralPath $nativePublishDirectory -Recurse -File | Where-Object {
    $_.Name -like '*TraceEvent*'
})
if ($nativeTraceEventFiles.Count -ne 0) {
    throw "The native publish output contains TraceEvent files: $($nativeTraceEventFiles.Name -join ', ')"
}

$cases = @(
    [pscustomobject]@{
        Name = 'info-cpu'
        Fixture = 'activity.nettrace'
        Arguments = @('info', '{trace}', '--format', 'json')
        Validate = { param($json) $json.result.sampleCount -eq 298 }
        Evidence = 'sampleCount=298'
    },
    [pscustomobject]@{
        Name = 'rank-cpu'
        Fixture = 'activity.nettrace'
        Arguments = @('rank', '{trace}', '--metric', 'cpu', '--format', 'json')
        Validate = { param($json) @($json.result.rows).Count -gt 0 -and $json.result.contributingRecordCount -eq 298 }
        Evidence = 'rows>0;contributingRecordCount=298'
    },
    [pscustomobject]@{
        Name = 'snapshot-exceptions'
        Fixture = 'exceptions.nettrace'
        Arguments = @('timeline', '{trace}', '--mode', 'snapshot', '--at', '55', '--window', '10', '--format', 'json')
        Validate = {
            param($json)
            $json.result.snapshot.cpu.sampleCount -eq 10 `
                -and $json.result.snapshot.exceptions.exceptionCount -eq 1813 `
                -and $json.result.snapshot.events.eventCount -eq 7387
        }
        Evidence = 'cpuSamples=10;exceptions=1813;events=7387'
    },
    [pscustomobject]@{
        Name = 'report-gc'
        Fixture = 'alloc.nettrace'
        Arguments = @('report', '{trace}', '--kind', 'gc', '--format', 'json')
        Validate = { param($json) $json.result.gcCount -eq 7 }
        Evidence = 'gcCount=7'
    },
    [pscustomobject]@{
        Name = 'report-jit'
        Fixture = 'jit.nettrace'
        Arguments = @('report', '{trace}', '--kind', 'jit', '--format', 'json')
        Validate = { param($json) $json.result.methodCount -eq 840 }
        Evidence = 'methodCount=840'
    },
    [pscustomobject]@{
        Name = 'rank-alloc'
        Fixture = 'alloc.nettrace'
        Arguments = @('rank', '{trace}', '--metric', 'alloc', '--format', 'json')
        Validate = { param($json) @($json.result.rows).Count -gt 0 }
        Evidence = 'rows>0'
    }
)

$caseResults = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $tracePath = Join-Path $rawDirectory $case.Fixture
    $arguments = @($case.Arguments | ForEach-Object { if ($_ -eq '{trace}') { $tracePath } else { $_ } })
    $modeResults = @{}

    foreach ($mode in @('jit', 'native')) {
        $derivedEtlx = "$tracePath.etlx"
        if (Test-Path -LiteralPath $derivedEtlx) {
            Remove-Item -LiteralPath $derivedEtlx
        }

        $jsonPath = Join-Path $resultsDirectory "$($case.Name).$mode.json"
        $logPath = Join-Path $logsDirectory "$($case.Name).$mode.log"
        if ($mode -eq 'jit') {
            $filePath = 'dotnet'
            $processArguments = @($jitDll) + $arguments
        } else {
            $filePath = $nativeExecutable
            $processArguments = $arguments
        }

        $exitCode = Invoke-CapturedProcess $filePath $processArguments $root $jsonPath $logPath
        $rawJson = [System.IO.File]::ReadAllText($jsonPath).TrimEnd("`r", "`n")
        [System.IO.File]::WriteAllText($jsonPath, $rawJson, $utf8NoBom)
        if ($exitCode -ne 0) {
            throw "$($case.Name) $mode exited with $exitCode. See $logPath"
        }
        if ([string]::IsNullOrWhiteSpace($rawJson)) {
            throw "$($case.Name) $mode returned blank JSON."
        }

        try {
            $json = $rawJson | ConvertFrom-Json -Depth 100
        } catch {
            throw "$($case.Name) $mode returned invalid JSON: $($_.Exception.Message)"
        }
        if (-not (& $case.Validate $json)) {
            throw "$($case.Name) $mode did not provide the expected evidence: $($case.Evidence)"
        }

        $modeResults[$mode] = [pscustomobject]@{
            ExitCode = $exitCode
            Json = $rawJson
            Sha256 = (Get-FileHash -LiteralPath $jsonPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    if ($modeResults.jit.Json -cne $modeResults.native.Json) {
        throw "$($case.Name) produced different ordered JSON in JIT and native modes."
    }

    $caseResults.Add([ordered]@{
        name = $case.Name
        evidence = $case.Evidence
        jitExitCode = $modeResults.jit.ExitCode
        nativeExitCode = $modeResults.native.ExitCode
        jitSha256 = $modeResults.jit.Sha256
        nativeSha256 = $modeResults.native.Sha256
        exactJsonMatch = $true
    })
}

$summary = [ordered]@{
    schemaVersion = 1
    runtimeIdentifier = $RuntimeIdentifier
    hostOSArchitecture = $hostArchitecture
    filtraceCommit = Invoke-GitRevision $root
    fastTraceCommit = Invoke-GitRevision $fastTraceRoot
    nativeExecutable = $nativeExecutableName
    nativeExecutableSha256 = (Get-FileHash -LiteralPath $nativeExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    nativeProof = [ordered]@{
        aotPublishSucceeded = $true
        nativeBuildLog = 'logs/native-publish.log'
        managedCliDllAbsent = $true
        cliDepsFileAbsent = $true
        traceEventAbsentFromJitDependencies = $true
        fastTraceProjectInNativeRestore = $fastTraceProjects[0].Name
        traceEventAbsentFromNativeRestore = $true
        nativeRestoreSha256 = (Get-FileHash -LiteralPath $nativeAssetsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        traceEventFilesAbsentFromNativeOutput = $true
    }
    cases = $caseResults
}

$summaryPath = Join-Path $outputRoot 'summary.json'
$summaryJson = $summary | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($summaryPath, $summaryJson, $utf8NoBom)
Write-Host "FastTrace source adoption check passed: $summaryPath"