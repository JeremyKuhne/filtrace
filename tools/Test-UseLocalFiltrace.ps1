#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param(
    [string]$WrapperPath = (Join-Path $PSScriptRoot 'Use-LocalFiltrace.ps1'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.0') {
    throw 'Test-UseLocalFiltrace.ps1 requires PowerShell 7.'
}
if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'Test-UseLocalFiltrace.ps1 runs only on Windows.'
}

if (-not ('FiltraceBoundedCapture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public sealed class FiltraceBoundedCapture
{
    public string Text { get; private set; } = "";
    public bool ExceededLimit { get; private set; }
    public static async Task<FiltraceBoundedCapture> ReadAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        byte[] readBuffer = new byte[4096];
        using MemoryStream retained = new MemoryStream(limit);
        bool exceededLimit = false;
        int read;
        while ((read = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, cancellationToken)) != 0)
        {
            int retainedCount = Math.Min(read, limit - (int)retained.Length);
            if (retainedCount > 0)
            {
                retained.Write(readBuffer, 0, retainedCount);
            }
            exceededLimit |= retainedCount != read;
        }
        return new FiltraceBoundedCapture { Text = Encoding.UTF8.GetString(retained.ToArray()), ExceededLimit = exceededLimit };
    }
}
'@
}

function Assert-Equal([object]$Actual, [object]$Expected, [string]$Because) {
    if ($Actual -cne $Expected) {
        throw "$Because Expected '$Expected'; received '$Actual'."
    }
}
function Assert-Contains([string]$Actual, [string]$Expected, [string]$Because) {
    if (-not $Actual.Contains($Expected, [System.StringComparison]::Ordinal)) {
        throw "$Because Expected to find '$Expected'; received '$Actual'."
    }
}
function Invoke-BoundedProcess(
    [string]$FilePath,
    [string[]]$ArgumentList,
    [hashtable]$Environment,
    [string]$WorkingDirectory = '') {
    $outputLimit = 64 * 1024
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    if ($WorkingDirectory) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[$entry.Key] = [string]$entry.Value
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $cancellation = [System.Threading.CancellationTokenSource]::new()
    $stdoutTask = $null
    $stderrTask = $null
    $stdoutCapture = $null
    $stderrCapture = $null
    $started = $false
    $completed = $false
    $exitCode = $null
    $primaryFailure = $null
    $cleanupFailure = $null
    try {
        $started = $process.Start()
        if (-not $started) {
            throw "Failed to start '$FilePath'."
        }
        $stdoutTask = [FiltraceBoundedCapture]::ReadAsync($process.StandardOutput.BaseStream, $outputLimit, $cancellation.Token)
        $stderrTask = [FiltraceBoundedCapture]::ReadAsync($process.StandardError.BaseStream, $outputLimit, $cancellation.Token)
        $completed = $process.WaitForExit(30000)
        if (-not $completed) {
            throw "Process '$FilePath' exceeded the 30 second contract deadline."
        }
        $exitCode = $process.ExitCode
    }
    catch {
        $primaryFailure = $_
    }
    finally {
        if ($started -and -not $completed) {
            $cancellation.Cancel()
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                    if (-not $process.WaitForExit(5000)) {
                        $cleanupFailure = "The owned process '$FilePath' did not terminate within 5 seconds."
                    }
                }
            }
            catch {
                $cleanupFailure = "Unable to terminate the owned process '$FilePath': $($_.Exception.Message)"
            }
        }
        foreach ($streamTask in @($stdoutTask, $stderrTask)) {
            if ($null -ne $streamTask) {
                try {
                    if (-not $streamTask.Wait(5000)) {
                        $cleanupFailure = "A redirected stream from '$FilePath' did not complete within 5 seconds."
                    }
                }
                catch {
                    if ($null -eq $primaryFailure) {
                        $cleanupFailure = "Unable to capture a redirected stream from '$FilePath': $($_.Exception.Message)"
                    }
                }
            }
        }
        if ($null -ne $stdoutTask -and $stdoutTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
            $stdoutCapture = $stdoutTask.Result
        }
        if ($null -ne $stderrTask -and $stderrTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
            $stderrCapture = $stderrTask.Result
        }
        $process.Dispose()
        $cancellation.Dispose()
    }
    if ($null -ne $primaryFailure) {
        throw $primaryFailure
    }
    if ($null -ne $cleanupFailure) {
        throw $cleanupFailure
    }
    if ($stdoutCapture.ExceededLimit -or $stderrCapture.ExceededLimit) {
        throw "Process '$FilePath' exceeded the 64 KiB per-stream output limit."
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Stdout = $stdoutCapture.Text
        Stderr = $stderrCapture.Text
    }
}
function Write-CommandFile([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.Encoding]::ASCII)
}
function Install-NativeProbe(
    [string]$Directory,
    [string[]]$ExecutableNames,
    [string]$ProbeOutput,
    [string]$ProbeAppHost) {
    [void][System.IO.Directory]::CreateDirectory($Directory)
    foreach ($file in [System.IO.Directory]::GetFiles($ProbeOutput)) {
        [System.IO.File]::Copy($file, (Join-Path $Directory ([System.IO.Path]::GetFileName($file))), $true)
    }
    foreach ($executableName in $ExecutableNames) {
        [System.IO.File]::Copy($ProbeAppHost, (Join-Path $Directory $executableName), $true)
    }
}

function Invoke-Wrapper(
    [string]$HostPath,
    [string]$FixtureWrapper,
    [string]$InvokerPath,
    [string]$PathValue,
    [string]$Action,
    [string]$Target,
    [string]$Configuration,
    [string]$LogPath,
    [string]$ReadinessPath,
    [string]$GitLogPath = '',
    [string]$ProbeMode = 'record',
    [string]$RealDotnetPath = '',
    [string]$HelperAssemblyPath = '',
    [string]$RepositoryRoot = '',
    [string]$GitDirectory = '',
    [int]$NativeExitCode = 0,
    [string]$NativeStdout = '',
    [string]$NativeStderr = '',
    [switch]$EnableNativeErrorPromotion,
    [switch]$OmitTargetRepository,
    [string]$WorkingDirectory = '') {
    [string[]]$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File')
    if ($EnableNativeErrorPromotion) {
        $arguments += @($InvokerPath, '-WrapperPath', $FixtureWrapper)
    }
    else {
        $arguments += $FixtureWrapper
    }
    $arguments += @('-Action', $Action)
    if (-not $OmitTargetRepository) {
        $arguments += @('-TargetRepository', $Target)
    }
    if ($Configuration) {
        $arguments += @('-Configuration', $Configuration)
    }
    Invoke-BoundedProcess -FilePath $HostPath -ArgumentList $arguments -WorkingDirectory $WorkingDirectory -Environment @{
        PATH = $PathValue
        FILTRACE_WRAPPER_PROBE_MODE = $ProbeMode
        FILTRACE_WRAPPER_PROBE_READINESS_PATH = $ReadinessPath
        FILTRACE_WRAPPER_PROBE_DOTNET_LOG_PATH = $LogPath
        FILTRACE_WRAPPER_PROBE_GIT_LOG_PATH = $GitLogPath
        FILTRACE_WRAPPER_PROBE_REAL_DOTNET_PATH = $RealDotnetPath
        FILTRACE_WRAPPER_PROBE_HELPER_ASSEMBLY_PATH = $HelperAssemblyPath
        FILTRACE_WRAPPER_PROBE_REPOSITORY_ROOT = $RepositoryRoot
        FILTRACE_WRAPPER_PROBE_GIT_DIRECTORY = $GitDirectory
        FILTRACE_WRAPPER_PROBE_EXIT_CODE = $NativeExitCode
        FILTRACE_WRAPPER_PROBE_STANDARD_OUTPUT = $NativeStdout
        FILTRACE_WRAPPER_PROBE_STANDARD_ERROR = $NativeStderr
    }
}
function Test-HostContract(
    [string]$HostPath,
    [string]$HostName,
    [string]$FixtureRoot,
    [string]$FixtureWrapper,
    [string]$InvokerPath,
    [bool]$SupportsNativeErrorPromotion,
    [string]$ProbeOutput,
    [string]$ProbeAppHost,
    [string]$ReadinessPath) {
    $hostRoot = Join-Path $FixtureRoot $HostName
    $first = Join-Path $hostRoot 'first'
    $second = Join-Path $hostRoot 'second'
    $batchFirst = Join-Path $hostRoot 'batch-first'
    $nativeAfterBatch = Join-Path $hostRoot 'native-after-batch'
    $batchOnly = Join-Path $hostRoot 'batch-only'
    $missingDotnet = Join-Path $hostRoot 'missing-dotnet'
    $missingGit = Join-Path $hostRoot 'missing-git'
    $target = Join-Path $hostRoot 'target repository with spaces'
    [void][System.IO.Directory]::CreateDirectory($target)
    Install-NativeProbe $first @('dotnet.exe', 'git.exe') $ProbeOutput $ProbeAppHost
    Install-NativeProbe $second @('dotnet.exe', 'git.exe') $ProbeOutput $ProbeAppHost
    Install-NativeProbe $nativeAfterBatch @('dotnet.exe', 'git.exe') $ProbeOutput $ProbeAppHost
    Install-NativeProbe $missingDotnet @('git.exe') $ProbeOutput $ProbeAppHost
    Install-NativeProbe $missingGit @('dotnet.exe') $ProbeOutput $ProbeAppHost
    [void][System.IO.Directory]::CreateDirectory($batchFirst)
    [void][System.IO.Directory]::CreateDirectory($batchOnly)
    Write-CommandFile (Join-Path $missingDotnet 'dotnet.cmd') "@echo off`r`nexit /b 0`r`n"
    Write-CommandFile (Join-Path $missingGit 'git.cmd') "@echo off`r`nexit /b 0`r`n"

    $fixtureSource = Split-Path -Parent (Split-Path -Parent $FixtureWrapper)
    $project = Join-Path $fixtureSource 'tools/Filtrace.LocalTesting/Filtrace.LocalTesting.csproj'
    $batchOnlyLog = Join-Path $hostRoot 'batch-only.txt'
    Write-CommandFile (Join-Path $batchOnly 'dotnet.cmd') "@echo off`r`n> `"$batchOnlyLog`" echo selected`r`nexit /b 0`r`n"
    Write-CommandFile (Join-Path $batchOnly 'git.cmd') "@echo off`r`nexit /b 0`r`n"
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $batchOnly Restore $target -LogPath $batchOnlyLog -ReadinessPath $ReadinessPath
    if ($result.ExitCode -eq 0 -or (Test-Path -LiteralPath $batchOnlyLog)) {
        throw "$HostName did not reject batch-only dotnet and git before execution."
    }
    Assert-Contains $result.Stderr "directly launchable native 'dotnet.exe'" "$HostName batch-only error mismatch."

    $batchMarker = Join-Path $hostRoot 'batch-first-selected.txt'
    Write-CommandFile (Join-Path $batchFirst 'dotnet.cmd') "@echo off`r`n> `"$batchMarker`" echo selected`r`nexit /b 88`r`n"
    Write-CommandFile (Join-Path $batchFirst 'git.cmd') "@echo off`r`nexit /b 89`r`n"
    $batchFirstPath = "$batchFirst$([System.IO.Path]::PathSeparator)$nativeAfterBatch"
    $nativeAfterBatchLog = Join-Path $hostRoot 'native-after-batch.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $batchFirstPath Restore $target -LogPath $nativeAfterBatchLog -ReadinessPath $ReadinessPath
    Assert-Equal $result.ExitCode 0 "$HostName batch-first selection exit mismatch."
    if (Test-Path -LiteralPath $batchMarker) {
        throw "$HostName selected a batch shim before a native executable."
    }
    $nativeAfterBatchRecord = [System.IO.File]::ReadAllText($nativeAfterBatchLog)
    Assert-Contains $nativeAfterBatchRecord "ARG=$(Join-Path $nativeAfterBatch 'dotnet.exe')" "$HostName selected-dotnet record mismatch."
    Assert-Contains $nativeAfterBatchRecord "ARG=$(Join-Path $nativeAfterBatch 'git.exe')" "$HostName selected-git record mismatch."

    $pathValue = "$first$([System.IO.Path]::PathSeparator)$second"
    $emptyLog = Join-Path $hostRoot 'valid-empty.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $pathValue install $target -LogPath $emptyLog -ReadinessPath $ReadinessPath -OmitTargetRepository -WorkingDirectory $target
    Assert-Equal $result.ExitCode 0 "$HostName valid-empty exit mismatch."
    Assert-Equal $result.Stdout '' "$HostName valid-empty stdout mismatch."
    Assert-Equal $result.Stderr '' "$HostName valid-empty stderr mismatch."
    [string[]]$expected = @(
        'TOOL=dotnet', "CWD=$fixtureSource", 'ARG=run', 'ARG=--project', "ARG=$project",
        'ARG=--configuration', 'ARG=Release', 'ARG=--no-launch-profile', 'ARG=--',
        'ARG=--action', 'ARG=install', 'ARG=--target-repository', "ARG=$target",
        'ARG=--configuration', 'ARG=Release', 'ARG=--source-checkout', "ARG=$fixtureSource",
        'ARG=--dotnet-path', "ARG=$(Join-Path $first 'dotnet.exe')",
        'ARG=--git-path', "ARG=$(Join-Path $first 'git.exe')")
    [string[]]$actual = [System.IO.File]::ReadAllLines($emptyLog)
    Assert-Equal ($actual -join "`n") ($expected -join "`n") "$HostName default argument sequence mismatch."
    $textLog = Join-Path $hostRoot 'text.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $pathValue Restore $target -Configuration Debug -LogPath $textLog -ReadinessPath $ReadinessPath -NativeStdout "helper output$([Environment]::NewLine)"
    Assert-Equal $result.ExitCode 0 "$HostName text exit mismatch."
    Assert-Equal $result.Stdout "helper output$([Environment]::NewLine)" "$HostName text stdout mismatch."
    Assert-Equal $result.Stderr '' "$HostName text stderr mismatch."
    [string[]]$textLines = [System.IO.File]::ReadAllLines($textLog)
    Assert-Equal (($textLines | Select-Object -Index 6, 14) -join ',') 'ARG=Debug,ARG=Debug' "$HostName explicit Debug forwarding mismatch."
    $failureLog = Join-Path $hostRoot 'failure.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $pathValue install $target -LogPath $failureLog -ReadinessPath $ReadinessPath -NativeExitCode 37 -NativeStdout "failure output$([Environment]::NewLine)" -NativeStderr "failure error$([Environment]::NewLine)"
    Assert-Equal $result.ExitCode 37 "$HostName native failure exit mismatch."
    Assert-Equal $result.Stdout "failure output$([Environment]::NewLine)" "$HostName native failure stdout mismatch."
    Assert-Equal $result.Stderr "failure error$([Environment]::NewLine)" "$HostName native failure stderr mismatch."
    if ($SupportsNativeErrorPromotion) {
        $promotionLog = Join-Path $hostRoot 'native-error-promotion.txt'
        $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $pathValue Restore $target -Configuration Debug -LogPath $promotionLog -ReadinessPath $ReadinessPath -NativeExitCode 37 -NativeStdout "promoted failure output$([Environment]::NewLine)" -NativeStderr "promoted failure error$([Environment]::NewLine)" -EnableNativeErrorPromotion
        Assert-Equal $result.ExitCode 37 "$HostName native-error-promotion exit mismatch."
        Assert-Equal $result.Stdout "promoted failure output$([Environment]::NewLine)" "$HostName native-error-promotion stdout mismatch."
        Assert-Equal $result.Stderr "promoted failure error$([Environment]::NewLine)" "$HostName native-error-promotion stderr mismatch."
    }
    $missingLog = Join-Path $hostRoot 'missing.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $missingDotnet Restore $target -LogPath $missingLog -ReadinessPath $ReadinessPath
    if ($result.ExitCode -eq 0 -or (Test-Path -LiteralPath $missingLog)) {
        throw "$HostName did not fail before execution when dotnet was missing."
    }
    Assert-Contains $result.Stderr "directly launchable native 'dotnet.exe'" "$HostName missing-dotnet error mismatch."
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $missingGit Restore $target -LogPath $missingLog -ReadinessPath $ReadinessPath
    if ($result.ExitCode -eq 0 -or (Test-Path -LiteralPath $missingLog)) {
        throw "$HostName did not fail before execution when git was missing."
    }
    Assert-Contains $result.Stderr "directly launchable native 'git.exe'" "$HostName missing-git error mismatch."
}

function Test-RealHelperInvocation(
    [string]$HostPath,
    [string]$FixtureRoot,
    [string]$FixtureWrapper,
    [string]$InvokerPath,
    [string]$ProbeOutput,
    [string]$ProbeAppHost,
    [string]$ReadinessPath,
    [string]$RealDotnetPath,
    [string]$HelperAssemblyPath) {
    $helperRoot = Join-Path $FixtureRoot 'real helper invocation'
    $nativeTools = Join-Path $helperRoot 'native tools'
    $target = Join-Path $helperRoot 'target repository'
    $gitDirectory = Join-Path $target '.git'
    [void][System.IO.Directory]::CreateDirectory($gitDirectory)
    Install-NativeProbe $nativeTools @('dotnet.exe', 'git.exe') $ProbeOutput $ProbeAppHost

    $consumerMarker = Join-Path $target 'consumer.txt'
    $lockPath = Join-Path $gitDirectory 'filtrace-local-testing.lock'
    [System.IO.File]::WriteAllText($consumerMarker, 'unchanged')
    [System.IO.File]::WriteAllText($lockPath, 'existing lock')
    $dotnetLog = Join-Path $helperRoot 'dotnet.txt'
    $gitLog = Join-Path $helperRoot 'git.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $nativeTools Restore $target `
        -Configuration $Configuration `
        -LogPath $dotnetLog `
        -ReadinessPath $ReadinessPath `
        -GitLogPath $gitLog `
        -ProbeMode 'forward-helper' `
        -RealDotnetPath $RealDotnetPath `
        -HelperAssemblyPath $HelperAssemblyPath `
        -RepositoryRoot $target `
        -GitDirectory $gitDirectory

    Assert-Equal $result.ExitCode 1 'Real helper missing-state exit mismatch.'
    Assert-Contains $result.Stderr 'Restore requires existing local-testing state.' 'Real helper missing-state error mismatch.'
    $fixtureSource = Split-Path -Parent (Split-Path -Parent $FixtureWrapper)
    $dotnetRecord = [System.IO.File]::ReadAllText($dotnetLog)
    $gitRecord = [System.IO.File]::ReadAllText($gitLog)
    Assert-Contains $dotnetRecord "TOOL=dotnet$([Environment]::NewLine)CWD=$fixtureSource" 'Real helper dotnet source working directory mismatch.'
    Assert-Contains $dotnetRecord "ARG=--dotnet-path$([Environment]::NewLine)ARG=$(Join-Path $nativeTools 'dotnet.exe')" 'Real helper selected-dotnet record mismatch.'
    Assert-Contains $dotnetRecord "ARG=--git-path$([Environment]::NewLine)ARG=$(Join-Path $nativeTools 'git.exe')" 'Real helper selected-git record mismatch.'
    Assert-Contains $gitRecord "TOOL=git$([Environment]::NewLine)CWD=$fixtureSource" 'Real helper git source working directory mismatch.'
    Assert-Contains $gitRecord "ARG=-C$([Environment]::NewLine)ARG=$target$([Environment]::NewLine)ARG=rev-parse" 'Real helper git argument record mismatch.'
    Assert-Equal ([System.IO.File]::ReadAllText($consumerMarker)) 'unchanged' 'Real helper changed consumer content.'
    Assert-Equal ([System.IO.File]::ReadAllText($lockPath)) 'existing lock' 'Real helper changed the existing synchronization lock.'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $gitDirectory 'filtrace-local-testing')) $false 'Real helper created private git state.'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $target '.vscode')) $false 'Real helper created target VS Code resources.'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $target '.agents')) $false 'Real helper created target agent resources.'
}

$resolvedWrapper = (Resolve-Path -LiteralPath $WrapperPath).Path
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$probeOutput = Join-Path $repositoryRoot "tests/Filtrace.LocalTesting.Tests/bin/$Configuration/net10.0"
$probeAppHost = Join-Path $probeOutput 'Filtrace.LocalTesting.Tests.exe'
$helperAssembly = Join-Path $probeOutput 'Filtrace.LocalTesting.dll'
if (-not (Test-Path -LiteralPath $probeAppHost) -or -not (Test-Path -LiteralPath $helperAssembly)) {
    throw "Build Filtrace.LocalTesting.Tests in $Configuration before running this contract."
}
$realDotnetPath = @(Get-Command dotnet.exe -CommandType Application -ErrorAction Stop)[0].Source
$root = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace-wrapper-contract-$([Guid]::NewGuid().ToString('N'))"
$fixtureSource = Join-Path $root 'source checkout with spaces'
$fixtureTools = Join-Path $fixtureSource 'tools'
$fixtureWrapper = Join-Path $fixtureTools 'Use-LocalFiltrace.ps1'
$invokerPath = Join-Path $fixtureTools 'Invoke-UseLocalFiltrace.ps1'
$readinessPath = Join-Path $root 'native-probe-ready'
$nativePreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
$nativePreferenceWasDefined = $null -ne $nativePreference
$nativePreferenceValue = if ($nativePreferenceWasDefined) { $nativePreference.Value } else { $null }
$failures = [System.Collections.Generic.List[string]]::new()
try {
    [void][System.IO.Directory]::CreateDirectory($fixtureTools)
    [System.IO.File]::WriteAllText($readinessPath, 'ready')
    [System.IO.File]::Copy($resolvedWrapper, $fixtureWrapper)
    [System.IO.File]::WriteAllText($invokerPath, @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WrapperPath,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Restore')]
    [string]$Action,
    [string]$TargetRepository,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$wrapperParameters = @{
    Action = $Action
    Configuration = $Configuration
}
if ($PSBoundParameters.ContainsKey('TargetRepository')) {
    $wrapperParameters.TargetRepository = $TargetRepository
}
& $WrapperPath @wrapperParameters
exit $LASTEXITCODE
'@, [System.Text.UTF8Encoding]::new($false))
    $hosts = [ordered]@{
        'Windows PowerShell 5.1' = @(@(Get-Command powershell.exe -CommandType Application -ErrorAction Stop)[0].Source, $false)
        'PowerShell 7' = @(@(Get-Command pwsh.exe -CommandType Application -ErrorAction Stop)[0].Source, $true)
    }
    foreach ($entry in $hosts.GetEnumerator()) {
        try {
            Test-HostContract -HostPath $entry.Value[0] -HostName $entry.Key -FixtureRoot $root -FixtureWrapper $fixtureWrapper -InvokerPath $invokerPath -SupportsNativeErrorPromotion $entry.Value[1] -ProbeOutput $probeOutput -ProbeAppHost $probeAppHost -ReadinessPath $readinessPath
        }
        catch {
            $failures.Add("$($entry.Key): $($_.Exception.Message)")
        }
    }
    try {
        Test-RealHelperInvocation -HostPath $hosts['PowerShell 7'][0] -FixtureRoot $root -FixtureWrapper $fixtureWrapper -InvokerPath $invokerPath -ProbeOutput $probeOutput -ProbeAppHost $probeAppHost -ReadinessPath $readinessPath -RealDotnetPath $realDotnetPath -HelperAssemblyPath $helperAssembly
    }
    catch {
        $failures.Add("Real helper invocation: $($_.Exception.Message)")
    }
    $currentPreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    Assert-Equal ($null -ne $currentPreference) $nativePreferenceWasDefined 'The contract changed whether the parent native-error preference was defined.'
    if ($nativePreferenceWasDefined) {
        Assert-Equal $currentPreference.Value $nativePreferenceValue 'The contract changed the parent native-error preference value.'
    }
}
catch {
    $failures.Add($_.Exception.Message)
}

if ($failures.Count -ne 0) {
    [Console]::Error.WriteLine("Contract failure evidence retained at '$root'.")
    throw ($failures -join [Environment]::NewLine)
}
try {
    [System.IO.Directory]::Delete($root, $true)
}
catch {
    throw "The contract passed, but owned fixture cleanup failed at '$root': $($_.Exception.Message)"
}
Write-Output 'PASS: Use-LocalFiltrace selected native executables, rejected batch-only tools, preserved defaults, arguments, streams, and exit codes in both Windows hosts, and reached the real helper process boundary.'
