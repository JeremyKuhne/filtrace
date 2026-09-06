#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Activates or restores this Filtrace checkout for one target repository.

.PARAMETER Action
  Install activates or refreshes local Filtrace resources. Restore returns the
  target repository to its recorded baseline.

.PARAMETER TargetRepository
  The Git repository to update. Defaults to the caller's current directory.

.PARAMETER Configuration
  The Debug or Release configuration used to prepare an installation. Defaults to Release.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Restore')]
    [string]$Action,

    [string]$TargetRepository = (Get-Location).Path,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$script:PSNativeCommandUseErrorActionPreference = $false
if ($PSVersionTable.PSVersion -lt [Version]'5.1') {
    throw 'Use-LocalFiltrace.ps1 requires Windows PowerShell 5.1 or PowerShell 7.'
}

function Resolve-NativeApplication([string]$Name) {
    try {
        return @(Get-Command $Name -CommandType Application -ErrorAction Stop)[0].Source
    }
    catch {
        throw "A directly launchable native '$Name' application was not found on PATH."
    }
}

$sourceCheckout = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$target = (Resolve-Path -LiteralPath $TargetRepository).Path
$runningOnWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
$dotnetPath = Resolve-NativeApplication $(if ($runningOnWindows) { 'dotnet.exe' } else { 'dotnet' })
$gitPath = Resolve-NativeApplication $(if ($runningOnWindows) { 'git.exe' } else { 'git' })
$project = Join-Path $sourceCheckout 'tools/Filtrace.LocalTesting/Filtrace.LocalTesting.csproj'

Push-Location $sourceCheckout
try {
    & $dotnetPath run --project $project --configuration $Configuration --no-launch-profile -- `
        --action $Action `
        --target-repository $target `
        --configuration $Configuration `
        --source-checkout $sourceCheckout `
        --dotnet-path $dotnetPath `
        --git-path $gitPath
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $exitCode