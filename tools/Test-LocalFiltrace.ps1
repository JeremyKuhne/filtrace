#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

#Requires -Version 7.0

<#
.SYNOPSIS
  Contract checks for the reversible local Filtrace setup helper.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$workflow = Join-Path $root 'tools/Use-LocalFiltrace.ps1'
$skillSource = Join-Path $root '.agents/skills/filtrace'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace local setup $([guid]::NewGuid().ToString('N'))"
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Write-Json([string] $Path, [object] $Value) {
    [string] $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $directory -Force
    }
    [System.IO.File]::WriteAllText(
        $Path,
        "$(ConvertTo-Json -InputObject $Value -Depth 16)`n",
        $utf8)
}

function Read-Json([string] $Path) {
    return [System.IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json -Depth 16
}

function Get-Property([object] $Object, [string] $Name) {
    return $Object.PSObject.Properties[$Name]
}

function Invoke-Dotnet([string[]] $Arguments, [switch] $Capture) {
    if ($Capture) {
        [string[]] $output = @(& dotnet @Arguments)
        [int] $exitCode = $LASTEXITCODE
        Assert-True ($exitCode -eq 0) "dotnet $($Arguments -join ' ') exited with code $exitCode."
        return $output -join [Environment]::NewLine
    }

    & dotnet @Arguments | Out-Host
    Assert-True ($LASTEXITCODE -eq 0) "dotnet $($Arguments -join ' ') exited with code $LASTEXITCODE."
}

function Write-NuGetConfig([string] $Path, [string] $PackageDirectory) {
    [xml] $document = '<configuration><packageSources><clear/><add key="local-filtrace" value=""/></packageSources></configuration>'
    $document.configuration.packageSources.add.value = $PackageDirectory
    [System.Xml.XmlWriterSettings] $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = $utf8
    $settings.Indent = $true
    [System.Xml.XmlWriter] $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
}

function Get-ToolState([string] $ToolPath) {
    [string] $json = Invoke-Dotnet -Arguments @(
        'tool', 'list', '--tool-path', $ToolPath, '--format', 'json') -Capture
    [object] $toolList = $json | ConvertFrom-Json -Depth 8
    [object[]] $matches = @($toolList.data | Where-Object packageId -IEQ 'klutzyninja.filtrace')
    Assert-True ($matches.Count -le 1) 'The isolated tool path contains duplicate Filtrace entries.'
    return $(if ($matches.Count -eq 0) { $null } else { $matches[0] })
}

function Copy-LocalPackages([string] $StatePath, [string] $SourceDirectory) {
    [string] $packageDirectory = Join-Path (Split-Path -Parent $StatePath) 'packages'
    $null = New-Item -ItemType Directory -Path $packageDirectory -Force
    Get-ChildItem -LiteralPath $SourceDirectory -File -Filter '*.nupkg' |
        Copy-Item -Destination $packageDirectory
}

function Invoke-Workflow(
    [string] $Action,
    [string] $McpConfigPath,
    [string] $SkillDestination,
    [string] $StatePath,
    [string] $CliToolPath = '',
    [switch] $ManageCli) {
    [System.Collections.Generic.List[string]] $arguments = @(
        '-NoProfile',
        '-File', $workflow,
        '-Action', $Action,
        '-Configuration', $Configuration,
        '-McpConfigPath', $McpConfigPath,
        '-SkillDestination', $SkillDestination,
        '-StatePath', $StatePath,
        '-SkipBuild',
        '-SkipValidation'
    )
    if ($CliToolPath) {
        $arguments.Add('-CliToolPath')
        $arguments.Add($CliToolPath)
    }
    if (-not $ManageCli) {
        $arguments.Add('-SkipCli')
    }
    & (Get-Process -Id $PID).Path @arguments 2>&1 | Out-Host
    [int] $exitCode = $LASTEXITCODE
    Assert-True ($exitCode -eq 0) "Local Filtrace $Action exited with code $exitCode."
}

function Invoke-WorkflowFailure(
    [string] $Action,
    [string] $McpConfigPath,
    [string] $SkillDestination,
    [string] $StatePath,
    [string] $CliToolPath) {
    [string[]] $arguments = @(
        '-NoProfile',
        '-File', $workflow,
        '-Action', $Action,
        '-Configuration', $Configuration,
        '-McpConfigPath', $McpConfigPath,
        '-SkillDestination', $SkillDestination,
        '-StatePath', $StatePath,
        '-CliToolPath', $CliToolPath,
        '-SkipBuild',
        '-SkipValidation'
    )
    [string[]] $output = @(& (Get-Process -Id $PID).Path @arguments 2>&1)
    [int] $exitCode = $LASTEXITCODE
    $output | Out-Host
    Assert-True ($exitCode -ne 0) "Local Filtrace $Action unexpectedly succeeded."
    return $output -join [Environment]::NewLine
}

function Assert-LocalSkill([string] $Destination, [string] $ExpectedOverlay) {
    foreach ($sourceFile in Get-ChildItem -LiteralPath $skillSource -Recurse -File) {
        [string] $relativePath = [System.IO.Path]::GetRelativePath($skillSource, $sourceFile.FullName)
        [string] $destinationFile = Join-Path $Destination $relativePath
        Assert-True (Test-Path -LiteralPath $destinationFile -PathType Leaf) "Vendored skill is missing '$relativePath'."
        Assert-True (
            (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ceq
            (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash) `
            "Vendored skill file '$relativePath' differs from the repository source."
    }

    Assert-True (
        [System.IO.File]::ReadAllText((Join-Path $Destination 'overlay.md'), $utf8) -ceq $ExpectedOverlay) `
        'The consumer-owned overlay was not preserved while vendoring the local skill.'
}

$null = New-Item -ItemType Directory -Path $temporaryRoot
try {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $workflow,
        [ref] $tokens,
        [ref] $parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Use-LocalFiltrace.ps1 did not parse.'

    Invoke-Dotnet -Arguments @(
        'build', (Join-Path $root 'filtrace.slnx'),
        '--configuration', $Configuration,
        '--nologo')
    [string] $fixturePackages = Join-Path $temporaryRoot 'fixture packages'
    $null = New-Item -ItemType Directory -Path $fixturePackages
    Invoke-Dotnet -Arguments @(
        'pack', (Join-Path $root 'filtrace.slnx'),
        '--configuration', $Configuration,
        '--no-build',
        '--output', $fixturePackages,
        '--nologo')
    [System.IO.FileInfo[]] $cliPackages = @(
        Get-ChildItem -LiteralPath $fixturePackages -File -Filter 'KlutzyNinja.Filtrace.*.nupkg' |
            Where-Object Name -NotLike 'KlutzyNinja.Filtrace.Mcp.*')
    Assert-True ($cliPackages.Count -eq 1) "Expected one local CLI package; found $($cliPackages.Count)."
    [string] $fixtureVersion = $cliPackages[0].BaseName.Substring('KlutzyNinja.Filtrace.'.Length)

    # Existing shipped setup: local install must preserve it, and restore must put
    # it back while retaining unrelated MCP changes made during local testing.
    [string] $existingRoot = Join-Path $temporaryRoot 'existing baseline'
    [string] $existingConfig = Join-Path $existingRoot 'mcp.json'
    [string] $existingSkill = Join-Path $existingRoot '.copilot/skills/filtrace'
    [string] $existingState = Join-Path $existingRoot 'state/local-state.json'
    $null = New-Item -ItemType Directory -Path $existingSkill -Force
    [System.IO.File]::WriteAllText((Join-Path $existingSkill 'SKILL.md'), 'shipped skill core', $utf8)
    [System.IO.File]::WriteAllText((Join-Path $existingSkill 'README.md'), 'shipped skill readme', $utf8)
    [System.IO.File]::WriteAllText((Join-Path $existingSkill 'overlay.md'), 'original overlay', $utf8)
    Write-Json $existingConfig ([ordered] @{
            servers = [ordered] @{
                docs = [ordered] @{ type = 'http'; url = 'https://example.invalid/mcp' }
                filtrace = [ordered] @{
                    type = 'stdio'
                    command = 'dnx'
                    args = @('KlutzyNinja.Filtrace.Mcp', '--yes')
                }
            }
            inputs = @()
        })

    Invoke-Workflow 'Install' $existingConfig $existingSkill $existingState
    Assert-True (Test-Path -LiteralPath $existingState -PathType Leaf) 'Install did not write reversible state.'
    [object] $localConfig = Read-Json $existingConfig
    Assert-True ($localConfig.servers.docs.url -ceq 'https://example.invalid/mcp') 'Install changed an unrelated MCP server.'
    Assert-True ($localConfig.servers.filtrace.command -ceq 'dotnet') 'Install did not select the local MCP DLL.'
    Assert-True (@($localConfig.servers.filtrace.args).Count -eq 1) 'Local MCP entry did not have exactly one DLL argument.'
    Assert-True (Test-Path -LiteralPath $localConfig.servers.filtrace.args[0] -PathType Leaf) 'Local MCP entry points to a missing DLL.'
    Assert-LocalSkill $existingSkill 'original overlay'

    [byte[]] $activeStateBytes = [System.IO.File]::ReadAllBytes($existingState)
    Invoke-Workflow 'Install' $existingConfig $existingSkill $existingState
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $activeStateBytes,
            [System.IO.File]::ReadAllBytes($existingState))) `
        'Refreshing local mode rewrote the original rollback manifest.'

    $localConfig.servers | Add-Member -MemberType NoteProperty -Name later -Value ([pscustomobject] @{ type = 'http'; url = 'https://later.invalid/mcp' })
    Write-Json $existingConfig $localConfig
    [System.IO.File]::WriteAllText((Join-Path $existingSkill 'overlay.md'), 'updated overlay', $utf8)

    Invoke-Workflow 'Restore' $existingConfig $existingSkill $existingState
    [object] $restoredConfig = Read-Json $existingConfig
    Assert-True ($restoredConfig.servers.filtrace.command -ceq 'dnx') 'Restore did not restore the shipped MCP command.'
    Assert-True ($restoredConfig.servers.filtrace.args[0] -ceq 'KlutzyNinja.Filtrace.Mcp') 'Restore changed the shipped MCP package id.'
    Assert-True ($restoredConfig.servers.later.url -ceq 'https://later.invalid/mcp') 'Restore removed an MCP server added during local testing.'
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $existingSkill 'SKILL.md'), $utf8) -ceq 'shipped skill core') 'Restore did not restore the prior skill core.'
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $existingSkill 'overlay.md'), $utf8) -ceq 'updated overlay') 'Restore did not retain the updated consumer overlay.'
    Assert-True (-not (Test-Path -LiteralPath $existingState)) 'Restore did not remove consumed state.'

    # Absent baseline: restore removes only the local MCP property and skill.
    [string] $absentRoot = Join-Path $temporaryRoot 'absent baseline'
    [string] $absentConfig = Join-Path $absentRoot 'mcp.json'
    [string] $absentSkill = Join-Path $absentRoot '.copilot/skills/filtrace'
    [string] $absentState = Join-Path $absentRoot 'state/local-state.json'
    Write-Json $absentConfig ([ordered] @{
            servers = [ordered] @{
                docs = [ordered] @{ type = 'http'; url = 'https://example.invalid/mcp' }
            }
            inputs = @()
        })

    Invoke-Workflow 'Install' $absentConfig $absentSkill $absentState
    Assert-True (Test-Path -LiteralPath (Join-Path $absentSkill 'SKILL.md') -PathType Leaf) 'Install did not vendor the skill into an absent destination.'
    Invoke-Workflow 'Restore' $absentConfig $absentSkill $absentState
    [object] $absentRestoredConfig = Read-Json $absentConfig
    Assert-True ($null -eq (Get-Property $absentRestoredConfig.servers 'filtrace')) 'Restore left a local MCP entry when none existed before.'
    Assert-True ($absentRestoredConfig.servers.docs.url -ceq 'https://example.invalid/mcp') 'Restore changed an unrelated MCP entry for the absent baseline.'
    Assert-True (-not (Test-Path -LiteralPath $absentSkill)) 'Restore left the locally vendored skill when none existed before.'
    Assert-True (-not (Test-Path -LiteralPath $absentState)) 'Restore left state for the absent baseline.'

    # CLI package restoration uses the recorded package bytes, not NuGet.org or
    # the transient local-build feed.
    [string] $cliRoot = Join-Path $temporaryRoot 'cli baseline'
    [string] $cliConfig = Join-Path $cliRoot 'mcp.json'
    [string] $cliSkill = Join-Path $cliRoot '.copilot/skills/filtrace'
    [string] $cliState = Join-Path $cliRoot 'state/local-state.json'
    [string] $cliToolPath = Join-Path $cliRoot 'tools'
    [string] $fixtureNuGetConfig = Join-Path $temporaryRoot 'fixture.nuget.config'
    $null = New-Item -ItemType Directory -Path $cliRoot -Force
    $null = New-Item -ItemType Directory -Path $cliToolPath -Force
    Write-NuGetConfig $fixtureNuGetConfig $fixturePackages
    Invoke-Dotnet -Arguments @(
        'tool', 'install', '--tool-path', $cliToolPath,
        '--configfile', $fixtureNuGetConfig,
        '--version', $fixtureVersion,
        'KlutzyNinja.Filtrace')
    [object] $baselineCli = Get-ToolState $cliToolPath
    Assert-True ($null -ne $baselineCli) 'The isolated baseline CLI was not installed.'
    Write-Json $cliConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    Copy-LocalPackages $cliState $fixturePackages

    Invoke-Workflow -Action Install -McpConfigPath $cliConfig -SkillDestination $cliSkill `
        -StatePath $cliState -CliToolPath $cliToolPath -ManageCli
    [object] $cliLocalState = Read-Json $cliState
    Assert-True ($cliLocalState.status -ceq 'local-active') 'CLI install did not mark local mode active.'
    Assert-True (Test-Path -LiteralPath $cliLocalState.cli.backupPackage -PathType Leaf) 'CLI install did not retain the baseline package.'
    [string] $baselinePackageHash = [string] $cliLocalState.cli.backupSha256
    [System.IO.FileInfo[]] $installedDlls = @(
        Get-ChildItem -LiteralPath (Join-Path $cliToolPath '.store') -Recurse -File -Filter 'filtrace.dll' |
            Where-Object FullName -Like '*tools*net10.0*any*')
    Assert-True ($installedDlls.Count -eq 1) "Expected one installed Filtrace DLL; found $($installedDlls.Count)."
    Assert-True (
        (Get-FileHash -LiteralPath $installedDlls[0].FullName -Algorithm SHA256).Hash -ceq
        (Get-FileHash -LiteralPath (Join-Path $root "src/Filtrace/bin/$Configuration/net10.0/filtrace.dll") -Algorithm SHA256).Hash) `
        'The isolated CLI assembly does not match the local build.'

    [string] $activeMcpConfig = [System.IO.File]::ReadAllText($cliConfig, $utf8)
    [byte[]] $baselinePackageBytes = [System.IO.File]::ReadAllBytes([string] $cliLocalState.cli.backupPackage)
    [System.IO.File]::WriteAllText([string] $cliLocalState.cli.backupPackage, 'corrupt package', $utf8)
    [string] $corruptBackupFailure = Invoke-WorkflowFailure -Action Restore -McpConfigPath $cliConfig `
        -SkillDestination $cliSkill -StatePath $cliState -CliToolPath $cliToolPath
    Assert-True ($corruptBackupFailure -match 'package backup hash changed') 'Corrupt CLI backup failure was not actionable.'
    Assert-True ((Read-Json $cliState).status -ceq 'local-active') 'CLI backup preflight failure changed active state.'
    [System.IO.File]::WriteAllBytes([string] $cliLocalState.cli.backupPackage, $baselinePackageBytes)

    Remove-Item -LiteralPath (Join-Path (Split-Path -Parent $cliState) 'packages') -Recurse -Force
    [System.IO.File]::WriteAllText($cliConfig, '{', $utf8)
    [string] $mcpFailure = Invoke-WorkflowFailure -Action Restore -McpConfigPath $cliConfig `
        -SkillDestination $cliSkill -StatePath $cliState -CliToolPath $cliToolPath
    Assert-True ($mcpFailure -match 'not valid JSON') 'Mid-restore MCP failure was not actionable.'
    Assert-True ((Read-Json $cliState).status -ceq 'restore-in-progress') 'Mid-restore failure did not retain retryable state.'
    [System.IO.File]::WriteAllText($cliConfig, $activeMcpConfig, $utf8)
    Invoke-Workflow -Action Restore -McpConfigPath $cliConfig -SkillDestination $cliSkill `
        -StatePath $cliState -CliToolPath $cliToolPath -ManageCli
    [object] $restoredCli = Get-ToolState $cliToolPath
    Assert-True ($restoredCli.version -ceq $baselineCli.version) 'CLI restore did not restore the baseline version.'
    [System.IO.FileInfo[]] $restoredPackages = @(
        Get-ChildItem -LiteralPath (Join-Path $cliToolPath '.store') -Recurse -File -Filter "klutzyninja.filtrace.$($baselineCli.version).nupkg")
    Assert-True ($restoredPackages.Count -eq 1) "Expected one restored CLI package; found $($restoredPackages.Count)."
    Assert-True ((Get-FileHash -LiteralPath $restoredPackages[0].FullName -Algorithm SHA256).Hash -ceq $baselinePackageHash) `
        'CLI restore did not use the exact baseline package bytes.'

    # An absent managed CLI is removed again on restore.
    [string] $emptyCliRoot = Join-Path $temporaryRoot 'empty cli baseline'
    [string] $emptyCliConfig = Join-Path $emptyCliRoot 'mcp.json'
    [string] $emptyCliSkill = Join-Path $emptyCliRoot '.copilot/skills/filtrace'
    [string] $emptyCliState = Join-Path $emptyCliRoot 'state/local-state.json'
    [string] $emptyCliToolPath = Join-Path $emptyCliRoot 'tools'
    $null = New-Item -ItemType Directory -Path $emptyCliToolPath -Force
    Write-Json $emptyCliConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    Copy-LocalPackages $emptyCliState $fixturePackages
    Invoke-Workflow -Action Install -McpConfigPath $emptyCliConfig -SkillDestination $emptyCliSkill `
        -StatePath $emptyCliState -CliToolPath $emptyCliToolPath -ManageCli
    Assert-True ($null -ne (Get-ToolState $emptyCliToolPath)) 'Local CLI was not installed into the empty tool path.'
    Invoke-Workflow -Action Restore -McpConfigPath $emptyCliConfig -SkillDestination $emptyCliSkill `
        -StatePath $emptyCliState -CliToolPath $emptyCliToolPath -ManageCli
    Assert-True ($null -eq (Get-ToolState $emptyCliToolPath)) 'Restore left a CLI that was absent from the baseline.'

    Write-Host 'Local Filtrace setup contract passed (MCP, skill, exact CLI package restore, and failed-restore retry).'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}