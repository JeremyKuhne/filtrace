#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param([string]$WrapperPath = (Join-Path $PSScriptRoot 'Use-LocalFiltrace.ps1'))
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

function Invoke-Wrapper(
    [string]$HostPath,
    [string]$FixtureWrapper,
    [string]$InvokerPath,
    [string]$PathValue,
    [string]$Action,
    [string]$Target,
    [string]$Configuration,
    [string]$LogPath,
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
        FILTRACE_CONTRACT_LOG = $LogPath
        FILTRACE_CONTRACT_EXIT = $NativeExitCode
        FILTRACE_CONTRACT_STDOUT = $NativeStdout
        FILTRACE_CONTRACT_STDERR = $NativeStderr
    }
}
function Test-HostContract(
    [string]$HostPath,
    [string]$HostName,
    [string]$FixtureRoot,
    [string]$FixtureWrapper,
    [string]$InvokerPath,
    [bool]$SupportsNativeErrorPromotion) {
    $hostRoot = Join-Path $FixtureRoot $HostName
    $first = Join-Path $hostRoot 'first'
    $second = Join-Path $hostRoot 'second'
    $missingDotnet = Join-Path $hostRoot 'missing-dotnet'
    $missingGit = Join-Path $hostRoot 'missing-git'
    $target = Join-Path $hostRoot 'target repository with spaces'
    foreach ($directory in @($first, $second, $missingDotnet, $missingGit, $target)) {
        [void][System.IO.Directory]::CreateDirectory($directory)
    }
    $dotnetRecorder = @'
@echo off
> "%FILTRACE_CONTRACT_LOG%" echo CWD=%CD%
:next
if "%~1"=="" goto done
>> "%FILTRACE_CONTRACT_LOG%" echo ARG=%~1
shift
goto next
:done
if defined FILTRACE_CONTRACT_STDOUT echo %FILTRACE_CONTRACT_STDOUT%
if defined FILTRACE_CONTRACT_STDERR 1>&2 echo %FILTRACE_CONTRACT_STDERR%
exit /b %FILTRACE_CONTRACT_EXIT%
'@
    $secondMarker = Join-Path $hostRoot 'second-selected.txt'
    Write-CommandFile (Join-Path $first 'dotnet.cmd') $dotnetRecorder
    Write-CommandFile (Join-Path $first 'git.cmd') "@echo off`r`nexit /b 0`r`n"
    Write-CommandFile (Join-Path $second 'dotnet.cmd') "@echo off`r`n> `"$secondMarker`" echo selected`r`nexit /b 88`r`n"
    Write-CommandFile (Join-Path $second 'git.cmd') "@echo off`r`nexit /b 89`r`n"
    Write-CommandFile (Join-Path $missingDotnet 'git.cmd') "@echo off`r`nexit /b 0`r`n"
    Write-CommandFile (Join-Path $missingGit 'dotnet.cmd') $dotnetRecorder

    $fixtureSource = Split-Path -Parent (Split-Path -Parent $FixtureWrapper)
    $project = Join-Path $fixtureSource 'tools/Filtrace.LocalTesting/Filtrace.LocalTesting.csproj'
    $pathValue = "$first$([System.IO.Path]::PathSeparator)$second"
    $emptyLog = Join-Path $hostRoot 'valid-empty.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $pathValue install $target -LogPath $emptyLog -OmitTargetRepository -WorkingDirectory $target
    Assert-Equal $result.ExitCode 0 "$HostName valid-empty exit mismatch."
    Assert-Equal $result.Stdout '' "$HostName valid-empty stdout mismatch."
    Assert-Equal $result.Stderr '' "$HostName valid-empty stderr mismatch."
    if (Test-Path -LiteralPath $secondMarker) {
        throw "$HostName selected the second dotnet candidate."
    }
    [string[]]$expected = @(
        "CWD=$fixtureSource", 'ARG=run', 'ARG=--project', "ARG=$project",
        'ARG=--configuration', 'ARG=Release', 'ARG=--no-launch-profile', 'ARG=--',
        'ARG=--action', 'ARG=install', 'ARG=--target-repository', "ARG=$target",
        'ARG=--configuration', 'ARG=Release', 'ARG=--source-checkout', "ARG=$fixtureSource",
        'ARG=--dotnet-path', "ARG=$(Join-Path $first 'dotnet.cmd')",
        'ARG=--git-path', "ARG=$(Join-Path $first 'git.cmd')")
    [string[]]$actual = [System.IO.File]::ReadAllLines($emptyLog)
    Assert-Equal ($actual -join "`n") ($expected -join "`n") "$HostName default argument sequence mismatch."
    $textLog = Join-Path $hostRoot 'text.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $pathValue Restore $target -Configuration Debug -LogPath $textLog -NativeStdout 'helper output'
    Assert-Equal $result.ExitCode 0 "$HostName text exit mismatch."
    Assert-Equal $result.Stdout "helper output$([Environment]::NewLine)" "$HostName text stdout mismatch."
    Assert-Equal $result.Stderr '' "$HostName text stderr mismatch."
    [string[]]$textLines = [System.IO.File]::ReadAllLines($textLog)
    Assert-Equal (($textLines | Select-Object -Index 5, 13) -join ',') 'ARG=Debug,ARG=Debug' "$HostName explicit Debug forwarding mismatch."
    $failureLog = Join-Path $hostRoot 'failure.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $pathValue install $target -LogPath $failureLog -NativeExitCode 37 -NativeStdout 'failure output' -NativeStderr 'failure error'
    Assert-Equal $result.ExitCode 37 "$HostName native failure exit mismatch."
    Assert-Equal $result.Stdout "failure output$([Environment]::NewLine)" "$HostName native failure stdout mismatch."
    Assert-Equal $result.Stderr "failure error$([Environment]::NewLine)" "$HostName native failure stderr mismatch."
    if ($SupportsNativeErrorPromotion) {
        $promotionLog = Join-Path $hostRoot 'native-error-promotion.txt'
        $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $pathValue Restore $target -Configuration Debug -LogPath $promotionLog -NativeExitCode 37 -NativeStdout 'promoted failure output' -NativeStderr 'promoted failure error' -EnableNativeErrorPromotion
        Assert-Equal $result.ExitCode 37 "$HostName native-error-promotion exit mismatch."
        Assert-Equal $result.Stdout "promoted failure output$([Environment]::NewLine)" "$HostName native-error-promotion stdout mismatch."
        Assert-Equal $result.Stderr "promoted failure error$([Environment]::NewLine)" "$HostName native-error-promotion stderr mismatch."
    }
    $missingLog = Join-Path $hostRoot 'missing.txt'
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $missingDotnet Restore $target -LogPath $missingLog
    if ($result.ExitCode -eq 0 -or (Test-Path -LiteralPath $missingLog)) {
        throw "$HostName did not fail before execution when dotnet was missing."
    }
    $result = Invoke-Wrapper $HostPath $FixtureWrapper $InvokerPath $missingGit Restore $target -LogPath $missingLog
    if ($result.ExitCode -eq 0 -or (Test-Path -LiteralPath $missingLog)) {
        throw "$HostName did not fail before execution when git was missing."
    }
}

$resolvedWrapper = (Resolve-Path -LiteralPath $WrapperPath).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace-wrapper-contract-$([Guid]::NewGuid().ToString('N'))"
$fixtureSource = Join-Path $root 'source checkout with spaces'
$fixtureTools = Join-Path $fixtureSource 'tools'
$fixtureWrapper = Join-Path $fixtureTools 'Use-LocalFiltrace.ps1'
$invokerPath = Join-Path $fixtureTools 'Invoke-UseLocalFiltrace.ps1'
$nativePreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
$nativePreferenceWasDefined = $null -ne $nativePreference
$nativePreferenceValue = if ($nativePreferenceWasDefined) { $nativePreference.Value } else { $null }
$failures = [System.Collections.Generic.List[string]]::new()
try {
    [void][System.IO.Directory]::CreateDirectory($fixtureTools)
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
            Test-HostContract -HostPath $entry.Value[0] -HostName $entry.Key -FixtureRoot $root -FixtureWrapper $fixtureWrapper -InvokerPath $invokerPath -SupportsNativeErrorPromotion $entry.Value[1]
        }
        catch {
            $failures.Add("$($entry.Key): $($_.Exception.Message)")
        }
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
Write-Output 'PASS: Use-LocalFiltrace preserved selection, default target, arguments, streams, and exit codes, including caller-enabled native error promotion, in Windows PowerShell 5.1 and PowerShell 7.'
