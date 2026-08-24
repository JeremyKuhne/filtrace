#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Contract checks for the bundled executable-project capture helper.

.DESCRIPTION
  Exercises dotnet-trace profile negotiation and sidecar metadata without building
  or launching a target. A fake recorder covers current, legacy, malformed, and
  incompatible profile surfaces.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$captureScript = Join-Path $root '.agents/skills/filtrace/scripts/Capture-ProjectTrace.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ftp-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-ThrowsLike([scriptblock]$Action, [string]$Expected) {
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message.IndexOf($Expected, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return
        }

        throw "Expected failure containing '$Expected', got: $($_.Exception.Message)"
    }

    throw "Expected failure containing '$Expected', but the action succeeded."
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$oldMode = $env:FILTRACE_FAKE_RECORDER_MODE
try {
    $tokens = $null
    $parseErrors = $null
    $captureAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $captureScript,
        [ref]$tokens,
        [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Capture-ProjectTrace.ps1 did not parse.'

    $functionNames = @('Get-DotnetTraceRecorder', 'Write-CaptureMetadata')
    $definitions = @(
        $captureAst.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $functionNames },
            $true) |
            Sort-Object { $_.Extent.StartOffset } |
            ForEach-Object { $_.Extent.Text }
    )
    Assert-True ($definitions.Count -eq $functionNames.Count) 'Recorder contract functions could not be isolated.'
    . ([scriptblock]::Create(($definitions -join [Environment]::NewLine)))

    $source = Get-Content -LiteralPath $captureScript -Raw
    $preflightOffset = $source.IndexOf(
        '$dotnetTraceRecorder = Get-DotnetTraceRecorder',
        [StringComparison]::Ordinal)
    $buildOffset = $source.IndexOf(
        'dotnet build $projFile.FullName',
        [StringComparison]::Ordinal)
    Assert-True ($preflightOffset -ge 0) 'Recorder preflight invocation was not found.'
    Assert-True ($buildOffset -ge 0) 'Project build invocation was not found.'
    Assert-True ($preflightOffset -lt $buildOffset) 'Recorder preflight must run before the project build.'

    $fakeRecorder = Join-Path $temporaryRoot 'fake-dotnet-trace.ps1'
    $fakeSource = @'
$command = $args[0]
if ($command -eq '--version') {
    if ($env:FILTRACE_FAKE_RECORDER_MODE -eq 'version-fail') {
        Write-Error 'version unavailable' -ErrorAction Continue
        exit 6
    }
    if ($env:FILTRACE_FAKE_RECORDER_MODE -eq 'version-malformed') {
        Write-Output 'unknown'
        exit 0
    }
    Write-Output '9.0.661903+fake'
    exit 0
}

if ($command -ne 'list-profiles') {
    Write-Error "unexpected fake recorder command: $command" -ErrorAction Continue
    exit 9
}

switch ($env:FILTRACE_FAKE_RECORDER_MODE) {
    'current' {
        Write-Output 'dotnet-trace profiles:'
        Write-Output '  dotnet-common                        - Runtime diagnostics'
        Write-Output '  dotnet-sampled-thread-time (collect) - Managed CPU samples'
        Write-Output '  gc-verbose                           - Allocation samples'
        Write-Output '  cpu-sampling (collect-linux)         - Kernel samples'
        exit 0
    }
    'legacy' {
        Write-Output 'dotnet-trace profiles:'
        Write-Output '  cpu-sampling - Managed CPU samples'
        Write-Output '  gc-verbose  - Allocation samples'
        exit 0
    }
    'collect-linux-only' {
        Write-Output 'dotnet-trace profiles:'
        Write-Output '  cpu-sampling (collect-linux) - Kernel samples'
        Write-Output '  gc-verbose                   - Allocation samples'
        exit 0
    }
    'none' {
        Write-Output 'dotnet-trace profiles:'
        Write-Output '  database - Database events'
        exit 0
    }
    'malformed' {
        Write-Output 'no parseable rows'
        exit 0
    }
    'list-fail' {
        Write-Error 'profile inventory unavailable' -ErrorAction Continue
        exit 7
    }
    default {
        Write-Error 'fake mode was not selected' -ErrorAction Continue
        exit 8
    }
}
'@
    [System.IO.File]::WriteAllText(
        $fakeRecorder,
        $fakeSource,
        (New-Object System.Text.UTF8Encoding($false)))

    $env:FILTRACE_FAKE_RECORDER_MODE = 'current'
    $current = Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    Assert-True (
        $current.ProfileArgument -ceq 'dotnet-common,dotnet-sampled-thread-time') `
        'Current recorder profiles did not select the proven CPU pair.'
    Assert-True ($current.Version -ceq '9.0.661903+fake') 'Recorder version was not retained.'
    Assert-True (
        ($current.Metadata.profiles -join ',') -ceq 'dotnet-common,dotnet-sampled-thread-time') `
        'Effective current profiles were not retained in metadata.'

    $allocation = Get-DotnetTraceRecorder $fakeRecorder 'alloc'
    Assert-True ($allocation.ProfileArgument -ceq 'gc-verbose') 'Allocation capture did not select gc-verbose.'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'legacy'
    $legacy = Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    Assert-True ($legacy.ProfileArgument -ceq 'cpu-sampling') 'Advertised legacy CPU profile was not selected.'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'collect-linux-only'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'supported CPU collect profile'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'none'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'Available: database'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'malformed'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'no profiles that apply to collect'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'list-fail'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'list-profiles failed (exit 7)'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'version-fail'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } '--version failed'

    $env:FILTRACE_FAKE_RECORDER_MODE = 'version-malformed'
    Assert-ThrowsLike {
        Get-DotnetTraceRecorder $fakeRecorder 'cpu'
    } 'no semantic version'

        $projectDirectory = Join-Path $temporaryRoot 'project with spaces'
        New-Item -ItemType Directory -Path $projectDirectory | Out-Null
        $buildMarker = Join-Path $projectDirectory 'build-started.txt'
        $projectPath = Join-Path $projectDirectory 'Probe.csproj'
        $escapedMarker = [System.Security.SecurityElement]::Escape($buildMarker)
        $projectSource = @"
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
    <Target Name="MarkBuildStart" BeforeTargets="Build">
        <WriteLinesToFile File="$escapedMarker" Lines="build started" Overwrite="true" />
    </Target>
</Project>
"@
        [System.IO.File]::WriteAllText(
                $projectPath,
                $projectSource,
                (New-Object System.Text.UTF8Encoding($false)))
        $rejectedTrace = Join-Path $projectDirectory 'should-not-exist.nettrace'
        $env:FILTRACE_FAKE_RECORDER_MODE = 'collect-linux-only'
        $fullOutput = & (Get-Process -Id $PID).Path -NoProfile -File $captureScript `
                -Project $projectPath `
                -Profiler EP `
                -Metric cpu `
                -DotnetTracePath $fakeRecorder `
                -Output $rejectedTrace 2>&1 | Out-String
        $fullExitCode = $LASTEXITCODE
        Assert-True ($fullExitCode -ne 0) 'Incompatible recorder unexpectedly passed the full helper preflight.'
        Assert-True (
            $fullOutput.IndexOf('capture preflight failed', [StringComparison]::OrdinalIgnoreCase) -ge 0) `
                'Full helper did not report the recorder preflight failure.'
        Assert-True (-not (Test-Path -LiteralPath $buildMarker)) 'Project build started before recorder compatibility was established.'
        Assert-True (-not (Test-Path -LiteralPath $rejectedTrace)) 'Target trace was created after recorder preflight failed.'

    $sidecarPath = Join-Path $temporaryRoot 'probe.nettrace'
    $emitted = @(Write-CaptureMetadata $sidecarPath ([ordered]@{ cpu = 'enabled' }) $current.Metadata)
    Assert-True ($emitted.Count -eq 0) 'Sidecar writer polluted the success stream.'

    $sidecarFile = "$sidecarPath.filtrace.json"
    $sidecar = Get-Content -LiteralPath $sidecarFile -Raw | ConvertFrom-Json
    Assert-True ($sidecar.schemaVersion -eq 1) 'Sidecar schema version changed.'
    Assert-True ($sidecar.analyses.cpu -ceq 'enabled') 'Sidecar analysis state was not retained.'
    Assert-True ($sidecar.recorder.name -ceq 'dotnet-trace') 'Sidecar recorder name was not retained.'
    Assert-True ($sidecar.recorder.version -ceq '9.0.661903+fake') 'Sidecar recorder version was not retained.'
    Assert-True (
        ($sidecar.recorder.profiles -join ',') -ceq 'dotnet-common,dotnet-sampled-thread-time') `
        'Sidecar effective profiles were not retained.'

    $bytes = [System.IO.File]::ReadAllBytes($sidecarFile)
    Assert-True (
        $bytes.Length -lt 3 -or -not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) `
        'Sidecar must be UTF-8 without a BOM.'

    $global:LASTEXITCODE = 0
    Write-Host 'Project capture contract passed.' -ForegroundColor Green
}
finally {
    $env:FILTRACE_FAKE_RECORDER_MODE = $oldMode
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}