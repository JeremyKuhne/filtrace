#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) Jeremy W Kuhne and contributors
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Verifies a release tag's merged-main and exact-tree CI evidence before publishing.
.DESCRIPTION
  Performs only read-only git and GitHub queries. A squash merge is accepted only
  when its tree equals the reviewed PR head that passed every CI prerequisite.
  No tag, release, package, or repository setting is created or changed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TagName,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $CommitSha,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [string] $EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PSNativeCommandUseErrorActionPreference = $false
Import-Module (Join-Path $PSScriptRoot 'ReleaseReadiness.psm1') -Force

function Invoke-ReleaseQuery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string[]] $Arguments,
        [switch] $AllowEmptyOutput
    )
    [System.Diagnostics.ProcessStartInfo] $start = [System.Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    [System.Diagnostics.Process] $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    [bool] $started = $false
    try {
        if (-not $process.Start()) { throw "Could not launch '$Executable'." }
        $started = $true
        [object[]] $streams = @(
            [pscustomobject]@{ reader = $process.StandardOutput; buffer = [char[]]::new(4096); text = [Text.StringBuilder]::new(); limit = 4MB; task = $null }
            [pscustomobject]@{ reader = $process.StandardError; buffer = [char[]]::new(4096); text = [Text.StringBuilder]::new(); limit = 64KB; task = $null }
        )
        foreach ($stream in $streams) { $stream.task = $stream.reader.ReadAsync($stream.buffer, 0, $stream.buffer.Length) }
        [Diagnostics.Stopwatch] $clock = [Diagnostics.Stopwatch]::StartNew()
        while (@($streams | Where-Object { $null -ne $_.task }).Count -gt 0 -or -not $process.HasExited) {
            if ($clock.Elapsed.TotalSeconds -gt 60) { throw "Read-only '$Executable' query exceeded 60 seconds." }
            foreach ($stream in $streams) {
                if ($null -eq $stream.task -or -not $stream.task.IsCompleted) { continue }
                [int] $read = $stream.task.GetAwaiter().GetResult()
                if ($read -eq 0) { $stream.task = $null; continue }
                if ($stream.text.Length + $read -gt $stream.limit) {
                    throw "Read-only '$Executable' query exceeded its diagnostic limit."
                }
                [void]$stream.text.Append($stream.buffer, 0, $read)
                $stream.task = $stream.reader.ReadAsync($stream.buffer, 0, $stream.buffer.Length)
            }
            Start-Sleep -Milliseconds 10
        }
        if ($process.ExitCode -ne 0) {
            throw "Read-only '$Executable' query failed (exit $($process.ExitCode)): $($streams[1].text)"
        }
        [string] $stdout = $streams[0].text.ToString()
        if (-not $AllowEmptyOutput -and [string]::IsNullOrWhiteSpace($stdout)) {
            throw "Read-only '$Executable' query returned no evidence."
        }
        return $stdout.Trim()
    }
    finally {
        try {
            if ($started -and -not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw "Could not confirm termination of query process $($process.Id)." }
            }
        }
        finally { $process.Dispose() }
    }
}

[string] $git = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
[string] $gh = @(Get-Command gh -CommandType Application -ErrorAction Stop)[0].Source
[object] $evidence = Get-ReleaseReadinessEvidence -TagName $TagName -CommitSha $CommitSha -Repository $Repository -Query {
    param([string] $Tool, [string[]] $Arguments, [bool] $AllowEmpty)
    [string] $executable = if ($Tool -ceq 'git') { $git } else { $gh }
    Invoke-ReleaseQuery -Executable $executable -Arguments $Arguments -AllowEmptyOutput:$AllowEmpty
}
if ($EvidencePath) {
    [string] $path = [System.IO.Path]::GetFullPath($EvidencePath)
    if (Test-Path -LiteralPath $path) { throw "Evidence already exists: '$path'." }
    [System.IO.File]::WriteAllText(
        $path, ($evidence | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
}
Write-Host "Release candidate $TagName passed merged-main and exact-tree CI verification."
