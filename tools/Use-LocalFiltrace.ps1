#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

#Requires -Version 7.0

<#
.SYNOPSIS
  Switch the Filtrace CLI, MCP server, and agent skill to this checkout.

.DESCRIPTION
  Install builds and packs the checkout, validates the local MCP server, installs
  the CLI from an isolated local NuGet source, points VS Code's Filtrace MCP entry
  directly at the built DLL, and vendors the repository's Filtrace skill.

  Before changing anything, Install records the existing CLI version, Filtrace MCP
  entry, and skill directory under artifacts/local-testing. Repeated installs keep
  that original baseline while refreshing the local build and skill.

  Restore removes the local setup and restores the recorded baseline. It changes
  only the Filtrace MCP entry, so unrelated MCP configuration added while testing
  is retained.

.PARAMETER Action
  Install the local checkout or Restore the setup recorded by the first Install.

.PARAMETER Configuration
  Build configuration used for the local CLI and MCP server. Defaults to Release.

.PARAMETER McpConfigPath
  VS Code mcp.json to update. Defaults to the stable VS Code user configuration.

.PARAMETER SkillDestination
  Directory that receives the local Filtrace skill. Defaults to the GitHub Copilot
  user skill directory. To vendor into a repository, pass its
  .agents/skills/filtrace directory.

.PARAMETER StatePath
  Reversible-state manifest. Defaults to artifacts/local-testing/state.json.

.PARAMETER CliToolPath
    Optional dotnet tool directory. By default the global tool is changed. This
    override supports isolated tests and callers that intentionally use --tool-path.

.PARAMETER SkipBuild
  Reuse packages and binaries from a prior successful local install.

.PARAMETER SkipCli
  Switch only MCP and skill state. The CLI is neither recorded nor changed.

.PARAMETER SkipValidation
  Skip the local MCP protocol check. Intended for isolated contract tests after a
  validated build, not routine use.

.EXAMPLE
  ./tools/Use-LocalFiltrace.ps1

.EXAMPLE
  ./tools/Use-LocalFiltrace.ps1 -SkillDestination ../consumer/.agents/skills/filtrace

.EXAMPLE
  ./tools/Use-LocalFiltrace.ps1 -Action Restore
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Install', 'Restore')]
    [string] $Action = 'Install',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $McpConfigPath,
    [string] $SkillDestination,
    [string] $StatePath,
    [string] $CliToolPath,
    [switch] $SkipBuild,
    [switch] $SkipCli,
    [switch] $SkipValidation
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'filtrace.slnx'
$skillSource = Join-Path $root '.agents/skills/filtrace'
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Get-DefaultMcpConfigPath {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
            throw 'APPDATA is not set; pass -McpConfigPath explicitly.'
        }

        return Join-Path $env:APPDATA 'Code/User/mcp.json'
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) {
        return Join-Path $HOME 'Library/Application Support/Code/User/mcp.json'
    }

    [string] $configRoot = if ([string]::IsNullOrWhiteSpace($env:XDG_CONFIG_HOME)) {
        Join-Path $HOME '.config'
    }
    else {
        $env:XDG_CONFIG_HOME
    }
    return Join-Path $configRoot 'Code/User/mcp.json'
}

function Get-FullPath([string] $Path, [string] $Description) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Description must be a nonempty path."
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Read-JsonFile([string] $Path, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description does not exist: '$Path'."
    }

    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Path
    if ($file.Length -gt 4MB) {
        throw "$Description is larger than the 4 MB safety limit: '$Path'."
    }

    try {
        return [System.IO.File]::ReadAllText($file.FullName, $utf8) |
            ConvertFrom-Json -Depth 32
    }
    catch {
        throw "$Description is not valid JSON: $($_.Exception.Message)"
    }
}

function Write-JsonFile([string] $Path, [object] $Value) {
    [string] $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $directory -Force
    }

    [string] $temporaryPath = Join-Path $directory ".$([System.IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [string] $json = ConvertTo-Json -InputObject $Value -Depth 32
        [System.IO.File]::WriteAllText($temporaryPath, "$json`n", $utf8)
        [System.IO.File]::Move($temporaryPath, $Path, $true)
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-Property([object] $Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    return $Object.PSObject.Properties[$Name]
}

function Set-Property([object] $Object, [string] $Name, [object] $Value) {
    [System.Management.Automation.PSPropertyInfo] $property = Get-Property $Object $Name
    if ($null -eq $property) {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
    else {
        $property.Value = $Value
    }
}

function Get-McpConfig([string] $Path) {
    [object] $config = if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Read-JsonFile $Path 'VS Code MCP configuration'
    }
    else {
        [pscustomobject] [ordered] @{
            servers = [pscustomobject] @{}
            inputs = @()
        }
    }

    [System.Management.Automation.PSPropertyInfo] $serversProperty = Get-Property $config 'servers'
    if ($null -eq $serversProperty) {
        Set-Property $config 'servers' ([pscustomobject] @{})
    }
    elseif ($null -eq $serversProperty.Value) {
        $serversProperty.Value = [pscustomobject] @{}
    }
    elseif ($serversProperty.Value -isnot [pscustomobject]) {
        throw "VS Code MCP configuration property 'servers' must be a JSON object: '$Path'."
    }

    return $config
}

function Set-McpServer([string] $Path, [bool] $Exists, [object] $Value) {
    [object] $config = Get-McpConfig $Path
    [object] $servers = (Get-Property $config 'servers').Value
    [System.Management.Automation.PSPropertyInfo] $property = Get-Property $servers 'filtrace'
    if ($Exists) {
        Set-Property $servers 'filtrace' $Value
    }
    elseif ($null -ne $property) {
        $servers.PSObject.Properties.Remove('filtrace')
    }

    Write-JsonFile $Path $config
}

function Invoke-Dotnet([string[]] $Arguments, [switch] $Capture) {
    if ($Capture) {
        [string[]] $output = @(& dotnet @Arguments)
        [int] $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            throw "dotnet $($Arguments -join ' ') exited with code $exitCode."
        }

        return $output -join [Environment]::NewLine
    }

    & dotnet @Arguments
    [int] $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dotnet $($Arguments -join ' ') exited with code $exitCode."
    }
}

function Get-CliScopeArguments {
    if ($CliToolPath) {
        return @('--tool-path', $CliToolPath)
    }

    return @('--global')
}

function Get-CliState {
    [string[]] $arguments = @('tool', 'list') + @(Get-CliScopeArguments) + @('--format', 'json')
    [string] $json = Invoke-Dotnet -Arguments $arguments -Capture
    [object] $toolList = $json | ConvertFrom-Json -Depth 8
    [object[]] $matches = @($toolList.data | Where-Object {
            $_.packageId -ieq 'klutzyninja.filtrace'
        })
    if ($matches.Count -gt 1) {
        throw 'dotnet tool list returned more than one KlutzyNinja.Filtrace entry.'
    }

    if ($matches.Count -eq 0) {
        return [pscustomobject] [ordered] @{
            installed = $false
            version = $null
        }
    }

    return [pscustomobject] [ordered] @{
        installed = $true
        version = [string] $matches[0].version
    }
}

function Remove-CliIfInstalled {
    [object] $cli = Get-CliState
    if ($cli.installed) {
        [string[]] $arguments = @('tool', 'uninstall') + @(Get-CliScopeArguments) + @('KlutzyNinja.Filtrace')
        Invoke-Dotnet -Arguments $arguments
    }
}

function Get-PackageIdentity([string] $Path) {
    [System.IO.Compression.ZipArchive] $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        [System.IO.Compression.ZipArchiveEntry[]] $nuspecs = @(
            $archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($nuspecs.Count -ne 1) {
            throw "Package '$Path' contains $($nuspecs.Count) nuspec files; expected one."
        }

        [System.IO.Stream] $stream = $nuspecs[0].Open()
        [System.IO.StreamReader] $reader = [System.IO.StreamReader]::new($stream)
        try {
            [xml] $nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }

        return [pscustomobject] [ordered] @{
            id = [string] $nuspec.package.metadata.id
            version = [string] $nuspec.package.metadata.version
            path = $Path
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-LocalCliPackage([string] $PackageDirectory) {
    [object[]] $packages = @(
        Get-ChildItem -LiteralPath $PackageDirectory -File -Filter '*.nupkg' |
            ForEach-Object { Get-PackageIdentity $_.FullName } |
            Where-Object id -CEQ 'KlutzyNinja.Filtrace')
    if ($packages.Count -ne 1) {
        throw "Expected one KlutzyNinja.Filtrace package in '$PackageDirectory'; found $($packages.Count)."
    }

    return $packages[0]
}

function Get-InstalledCliPackage([string] $Version) {
    [string] $storeRoot = if ($CliToolPath) {
        Join-Path $CliToolPath '.store'
    }
    else {
        if ([string]::IsNullOrWhiteSpace($HOME)) {
            throw 'HOME is not set; pass -CliToolPath explicitly.'
        }
        Join-Path $HOME '.dotnet/tools/.store'
    }
    if (-not (Test-Path -LiteralPath $storeRoot -PathType Container)) {
        throw "The dotnet tool store was not found: '$storeRoot'."
    }

    [object[]] $matches = @(
        Get-ChildItem -LiteralPath $storeRoot -Recurse -File -Filter '*.nupkg' |
            ForEach-Object { Get-PackageIdentity $_.FullName } |
            Where-Object {
                $_.id -ieq 'KlutzyNinja.Filtrace' -and $_.version -ceq $Version
            })
    if ($matches.Count -eq 0) {
        throw "The installed KlutzyNinja.Filtrace $Version package was not found under '$storeRoot'."
    }

    [object[]] $distinctHashes = @($matches |
            ForEach-Object { (Get-FileHash -LiteralPath $_.path -Algorithm SHA256).Hash } |
            Sort-Object -Unique)
    if ($distinctHashes.Count -ne 1) {
        throw "The tool store contains conflicting KlutzyNinja.Filtrace $Version packages under '$storeRoot'."
    }

    [string] $versionedName = "klutzyninja.filtrace.$Version.nupkg"
    [object[]] $preferred = @($matches | Where-Object {
            [System.IO.Path]::GetFileName($_.path) -ieq $versionedName
        })
    return $(if ($preferred.Count -gt 0) { $preferred[0] } else { $matches[0] })
}

function Backup-CliPackage([object] $Cli, [string] $DestinationDirectory) {
    if (-not [bool] $Cli.installed) { return }

    [object] $installedPackage = Get-InstalledCliPackage ([string] $Cli.version)
    if (Test-Path -LiteralPath $DestinationDirectory) {
        Remove-Item -LiteralPath $DestinationDirectory -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $DestinationDirectory -Force
    [string] $backupPath = Join-Path $DestinationDirectory ([System.IO.Path]::GetFileName($installedPackage.path))
    Copy-Item -LiteralPath $installedPackage.path -Destination $backupPath
    Set-Property $Cli 'backupPackage' $backupPath
    Set-Property $Cli 'backupSha256' ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash)
}

function Assert-CliPackage([object] $Cli) {
    if (-not [bool] $Cli.installed) { return }

    [string] $backupPackage = [string] $Cli.backupPackage
    if (-not (Test-Path -LiteralPath $backupPackage -PathType Leaf)) {
        throw "Recorded CLI package backup is missing: '$backupPackage'."
    }
    [string] $actualHash = (Get-FileHash -LiteralPath $backupPackage -Algorithm SHA256).Hash
    if ($actualHash -cne [string] $Cli.backupSha256) {
        throw "Recorded CLI package backup hash changed: '$backupPackage'."
    }
    [object] $identity = Get-PackageIdentity $backupPackage
    if ($identity.id -ine 'KlutzyNinja.Filtrace' -or $identity.version -cne [string] $Cli.version) {
        throw "Recorded CLI package backup is '$($identity.id)' $($identity.version); expected KlutzyNinja.Filtrace $($Cli.version)."
    }
}

function Write-LocalNuGetConfig([string] $Path, [string] $PackageDirectory) {
    [System.Xml.XmlWriterSettings] $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = $utf8
    $settings.Indent = $true
    [System.Xml.XmlWriter] $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('configuration')
        $writer.WriteStartElement('packageSources')
        $writer.WriteStartElement('clear')
        $writer.WriteEndElement()
        $writer.WriteStartElement('add')
        $writer.WriteAttributeString('key', 'local-filtrace')
        $writer.WriteAttributeString('value', $PackageDirectory)
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }
}

function Copy-Skill([string] $Source, [string] $Destination, [byte[]] $OverlayBytes) {
    [string] $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $parent -Force
    }

    [string] $leafName = [System.IO.Path]::GetFileName($Destination)
    [string] $operationId = [guid]::NewGuid().ToString('N')
    [string] $staging = Join-Path $parent ".$leafName.$operationId.tmp"
    [string] $previous = Join-Path $parent ".$leafName.$operationId.previous"
    [bool] $previousMoved = $false
    [bool] $published = $false
    try {
        Copy-Item -LiteralPath $Source -Destination $staging -Recurse -Force
        if ($null -ne $OverlayBytes) {
            [System.IO.File]::WriteAllBytes((Join-Path $staging 'overlay.md'), $OverlayBytes)
        }

        if (Test-Path -LiteralPath $Destination) {
            Move-Item -LiteralPath $Destination -Destination $previous
            $previousMoved = $true
        }
        Move-Item -LiteralPath $staging -Destination $Destination
        $published = $true
        if ($previousMoved) {
            Remove-Item -LiteralPath $previous -Recurse -Force
            $previousMoved = $false
        }
    }
    catch {
        if (-not $published -and $previousMoved -and
            -not (Test-Path -LiteralPath $Destination)) {
            Move-Item -LiteralPath $previous -Destination $Destination
            $previousMoved = $false
        }
        throw
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        if ($published) {
            Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-OverlayBytes([string] $SkillDirectory) {
    [string] $overlay = Join-Path $SkillDirectory 'overlay.md'
    if (Test-Path -LiteralPath $overlay -PathType Leaf) {
        return ,([System.IO.File]::ReadAllBytes($overlay))
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($SkillDestination) -and [string]::IsNullOrWhiteSpace($HOME)) {
    throw 'HOME is not set; pass -SkillDestination explicitly.'
}
if (-not $SkipCli -and [string]::IsNullOrWhiteSpace($CliToolPath) -and
    [string]::IsNullOrWhiteSpace($HOME)) {
    throw 'HOME is not set; pass -CliToolPath explicitly.'
}

$McpConfigPath = Get-FullPath $(if ($McpConfigPath) { $McpConfigPath } else { Get-DefaultMcpConfigPath }) 'MCP configuration path'
$SkillDestination = Get-FullPath $(if ($SkillDestination) { $SkillDestination } else { Join-Path $HOME '.copilot/skills/filtrace' }) 'Skill destination'
$StatePath = Get-FullPath $(if ($StatePath) { $StatePath } else { Join-Path $root 'artifacts/local-testing/state.json' }) 'State path'
if ($CliToolPath) {
    $CliToolPath = Get-FullPath $CliToolPath 'CLI tool path'
    if (-not (Test-Path -LiteralPath $CliToolPath -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $CliToolPath -Force
    }
}

[string] $skillSourceFull = [System.IO.Path]::GetFullPath($skillSource)
[StringComparison] $pathComparison = if (
    [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}
if ($skillSourceFull.Equals($SkillDestination, $pathComparison)) {
    throw 'SkillDestination must differ from the repository skill source.'
}

[string] $stateDirectory = Split-Path -Parent $StatePath
[string] $skillBackup = Join-Path $stateDirectory 'skill-backup'
[string] $cliBackup = Join-Path $stateDirectory 'cli-backup'
[string] $packageDirectory = Join-Path $stateDirectory 'packages'
[string] $nugetConfig = Join-Path $stateDirectory 'local.nuget.config'
[string] $restoreNugetConfig = Join-Path $stateDirectory 'restore.nuget.config'
[string] $mcpDll = Join-Path $root "src/Filtrace.Mcp/bin/$Configuration/net10.0/Filtrace.Mcp.dll"

if ($Action -ceq 'Install') {
    if (-not $SkipBuild) {
        Invoke-Dotnet @('build', $solution, '--configuration', $Configuration)
        if (Test-Path -LiteralPath $packageDirectory) {
            Remove-Item -LiteralPath $packageDirectory -Recurse -Force
        }
        $null = New-Item -ItemType Directory -Path $packageDirectory -Force
        Invoke-Dotnet @(
            'pack', $solution,
            '--configuration', $Configuration,
            '--no-build',
            '--output', $packageDirectory)
    }

    if (-not (Test-Path -LiteralPath $mcpDll -PathType Leaf)) {
        throw "Local MCP binary was not found: '$mcpDll'. Run without -SkipBuild."
    }
    if (-not $SkipValidation) {
        & (Join-Path $root 'tools/Test-McpServer.ps1') -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "Local MCP validation exited with code $LASTEXITCODE."
        }
    }

    [object] $localPackage = $null
    if (-not $SkipCli) {
        $localPackage = Get-LocalCliPackage $packageDirectory
    }

    [object] $state = $null
    if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
        $state = Read-JsonFile $StatePath 'Local-testing state'
        if ($state.schemaVersion -ne 2 -or
            -not [string]::Equals([string] $state.mcp.path, $McpConfigPath, $pathComparison) -or
            -not [string]::Equals([string] $state.skill.destination, $SkillDestination, $pathComparison) -or
            -not [string]::Equals([string] $state.cliToolPath, [string] $CliToolPath, $pathComparison) -or
            [bool] $state.cliManaged -ne (-not [bool] $SkipCli)) {
            throw "Existing local-testing state does not match this invocation: '$StatePath'. Restore it with the original arguments first."
        }
        if ($state.status -ceq 'restore-in-progress') {
            throw "Restore is already in progress for '$StatePath'. Run -Action Restore with the original arguments."
        }
    }
    else {
        if (-not (Test-Path -LiteralPath $stateDirectory -PathType Container)) {
            $null = New-Item -ItemType Directory -Path $stateDirectory -Force
        }
        if (Test-Path -LiteralPath $skillBackup) {
            Remove-Item -LiteralPath $skillBackup -Recurse -Force
        }

        [object] $mcpConfig = Get-McpConfig $McpConfigPath
        [object] $servers = (Get-Property $mcpConfig 'servers').Value
        [System.Management.Automation.PSPropertyInfo] $priorServer = Get-Property $servers 'filtrace'
        if (Test-Path -LiteralPath $SkillDestination -PathType Leaf) {
            throw "Skill destination is a file, not a directory: '$SkillDestination'."
        }
        [bool] $priorSkillExists = Test-Path -LiteralPath $SkillDestination -PathType Container
        if ($priorSkillExists) {
            Copy-Item -LiteralPath $SkillDestination -Destination $skillBackup -Recurse -Force
        }

        [object] $priorCli = if ($SkipCli) { $null } else { Get-CliState }
        if (-not $SkipCli) {
            Backup-CliPackage $priorCli $cliBackup
        }
        $state = [pscustomobject] [ordered] @{
            schemaVersion = 2
            createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
            status = 'baseline-recorded'
            cliManaged = -not [bool] $SkipCli
            cliToolPath = $CliToolPath
            cli = $priorCli
            mcp = [pscustomobject] [ordered] @{
                path = $McpConfigPath
                serverExisted = $null -ne $priorServer
                server = if ($null -eq $priorServer) { $null } else { $priorServer.Value }
            }
            skill = [pscustomobject] [ordered] @{
                destination = $SkillDestination
                existed = $priorSkillExists
            }
        }
        Write-JsonFile $StatePath $state
    }

    if (-not $SkipCli) {
        Remove-CliIfInstalled
        if (-not (Test-Path -LiteralPath $stateDirectory -PathType Container)) {
            $null = New-Item -ItemType Directory -Path $stateDirectory -Force
        }
        Write-LocalNuGetConfig $nugetConfig $packageDirectory
        [string[]] $installArguments = @('tool', 'install') + @(Get-CliScopeArguments) + @(
            '--configfile', $nugetConfig,
            '--version', $localPackage.version,
            'KlutzyNinja.Filtrace')
        Invoke-Dotnet -Arguments $installArguments
        [object] $installedCli = Get-CliState
        if (-not $installedCli.installed) {
            throw 'dotnet tool install completed without installing the Filtrace CLI.'
        }
        if ($installedCli.version -cne $localPackage.version) {
            throw "Installed CLI version '$($installedCli.version)' does not match local package '$($localPackage.version)'."
        }
        [object] $installedPackage = Get-InstalledCliPackage ([string] $installedCli.version)
        if ((Get-FileHash -LiteralPath $installedPackage.path -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $localPackage.path -Algorithm SHA256).Hash) {
            throw 'The installed CLI package bytes do not match the locally packed package.'
        }
    }

    [object] $localServer = [pscustomobject] [ordered] @{
        type = 'stdio'
        command = 'dotnet'
        args = @($mcpDll)
    }
    Set-McpServer $McpConfigPath $true $localServer

    [byte[]] $overlayBytes = Get-OverlayBytes $SkillDestination
    Copy-Skill $skillSource $SkillDestination $overlayBytes

    if ($state.status -cne 'local-active') {
        $state.status = 'local-active'
        Write-JsonFile $StatePath $state
    }

    Write-Host "Filtrace local mode is active ($Configuration)."
    if (-not $SkipCli) { Write-Host "  CLI: $($localPackage.version) from $packageDirectory" }
    Write-Host "  MCP: $mcpDll"
    Write-Host "  Skill: $SkillDestination"
    Write-Host "  Restore: $PSScriptRoot/Use-LocalFiltrace.ps1 -Action Restore"
    exit 0
}

if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
    throw "No local-testing state was found at '$StatePath'. Nothing can be restored automatically."
}

[object] $state = Read-JsonFile $StatePath 'Local-testing state'
if ($state.schemaVersion -ne 2 -or
    -not [string]::Equals([string] $state.mcp.path, $McpConfigPath, $pathComparison) -or
    -not [string]::Equals([string] $state.skill.destination, $SkillDestination, $pathComparison) -or
    -not [string]::Equals([string] $state.cliToolPath, [string] $CliToolPath, $pathComparison) -or
    [bool] $state.cliManaged -ne (-not [bool] $SkipCli)) {
    throw "Local-testing state does not match this Restore invocation: '$StatePath'."
}

if ([bool] $state.skill.existed -and
    -not (Test-Path -LiteralPath $skillBackup -PathType Container)) {
    throw "Recorded skill backup is missing: '$skillBackup'."
}
if (-not $SkipCli) {
    Assert-CliPackage $state.cli
}

$state.status = 'restore-in-progress'
Write-JsonFile $StatePath $state

if (-not $SkipCli) {
    Remove-CliIfInstalled
    if ([bool] $state.cli.installed) {
        [string] $backupPackageDirectory = Split-Path -Parent ([string] $state.cli.backupPackage)
        Write-LocalNuGetConfig $restoreNugetConfig $backupPackageDirectory
        [string[]] $restoreArguments = @('tool', 'install') + @(Get-CliScopeArguments) + @(
            '--configfile', $restoreNugetConfig,
            '--version', [string] $state.cli.version,
            'KlutzyNinja.Filtrace')
        Invoke-Dotnet -Arguments $restoreArguments
        [object] $restoredPackage = Get-InstalledCliPackage ([string] $state.cli.version)
        if ((Get-FileHash -LiteralPath $restoredPackage.path -Algorithm SHA256).Hash -cne
            [string] $state.cli.backupSha256) {
            throw 'The restored CLI package bytes do not match the recorded baseline package.'
        }
    }
}

Set-McpServer $McpConfigPath ([bool] $state.mcp.serverExisted) $state.mcp.server

[byte[]] $currentOverlay = Get-OverlayBytes $SkillDestination
if ([bool] $state.skill.existed) {
    Copy-Skill $skillBackup $SkillDestination $currentOverlay
}
elseif (Test-Path -LiteralPath $SkillDestination) {
    if ($null -ne $currentOverlay) {
        [string] $retainedOverlay = "$StatePath.restored-overlay.md"
        [System.IO.File]::WriteAllBytes($retainedOverlay, $currentOverlay)
        Write-Warning "The pre-local setup had no skill. The current overlay was retained at '$retainedOverlay'."
    }
    Remove-Item -LiteralPath $SkillDestination -Recurse -Force
}

Remove-Item -LiteralPath $StatePath -Force
Remove-Item -LiteralPath $skillBackup -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $cliBackup -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $packageDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $nugetConfig -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $restoreNugetConfig -Force -ErrorAction SilentlyContinue

Write-Host 'Filtrace local mode was removed and the recorded setup was restored.'
exit 0