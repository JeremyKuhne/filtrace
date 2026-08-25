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

function Test-CaseInsensitivePathPlatform {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows) -or
        [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)
}

function Test-WindowsPlatform {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Set-TestRestrictiveFileSecurity([string] $Path) {
    if (Test-WindowsPlatform) {
        [System.Security.Principal.SecurityIdentifier] $identity =
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        [System.Security.AccessControl.FileSecurity] $security =
            [System.Security.AccessControl.FileSecurity]::new()
        $security.SetOwner($identity)
        $security.SetAccessRuleProtection($true, $false)
        [System.Security.AccessControl.FileSystemAccessRule] $rule =
            [System.Security.AccessControl.FileSystemAccessRule]::new(
                $identity,
                [System.Security.AccessControl.FileSystemRights]::FullControl,
                [System.Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
        Set-Acl -LiteralPath $Path -AclObject $security
        return
    }

    [System.IO.File]::SetUnixFileMode(
        $Path,
        [System.IO.UnixFileMode]::UserRead -bor
            [System.IO.UnixFileMode]::UserWrite)
}

function Get-FileSecurityFingerprint([string] $Path) {
    if (Test-WindowsPlatform) {
        return [string] (Get-Acl -LiteralPath $Path).Sddl
    }

    return [int] [System.IO.File]::GetUnixFileMode($Path)
}

function Assert-RestrictedFile([string] $Path, [string] $Description) {
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) `
        "$Description does not exist: '$Path'."
    if (Test-WindowsPlatform) {
        [System.Security.AccessControl.FileSecurity] $security = Get-Acl -LiteralPath $Path
        [string] $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        [System.Security.AccessControl.AuthorizationRule[]] $rules = @($security.Access)
        Assert-True $security.AreAccessRulesProtected `
            "$Description inherits a broader Windows ACL."
        Assert-True ($rules.Count -eq 1 -and
            $rules[0].IdentityReference.Translate(
                [System.Security.Principal.SecurityIdentifier]).Value -ceq $currentUser -and
            $rules[0].AccessControlType -eq
                [System.Security.AccessControl.AccessControlType]::Allow) `
            "$Description is not restricted to the current Windows user."
        return
    }

    [int] $expectedMode = [int] (
        [System.IO.UnixFileMode]::UserRead -bor
        [System.IO.UnixFileMode]::UserWrite)
    Assert-True (
        [int] [System.IO.File]::GetUnixFileMode($Path) -eq $expectedMode) `
        "$Description does not have Unix mode 0600."
}

function Get-PathIdentity([string] $Path) {
    [string] $identity = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($Path))
    if (Test-CaseInsensitivePathPlatform) {
        return $identity.ToUpperInvariant()
    }

    return $identity
}

function Get-StableHash([string] $Value) {
    [System.Security.Cryptography.SHA256] $algorithm =
        [System.Security.Cryptography.SHA256]::Create()
    try {
        [byte[]] $hash = $algorithm.ComputeHash($utf8.GetBytes($Value))
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-DefaultStatePath([string] $Repository) {
    [string] $identityHash = Get-StableHash (Get-PathIdentity $Repository)
    return Join-Path $root "artifacts/local-testing/repositories/$identityHash/state.json"
}

function Get-StateLockPath([string] $StatePath) {
    [string] $lockRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'filtrace-local-testing-locks'
    $null = [System.IO.Directory]::CreateDirectory($lockRoot)
    return Join-Path $lockRoot "$(Get-StableHash (Get-PathIdentity $StatePath)).lock"
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

function Write-NuGetConfig(
    [string] $Path,
    [string] $PackageDirectory) {
    [xml] $document = '<configuration><packageSources><clear/><add key="local-filtrace" value=""/></packageSources></configuration>'
    $document.configuration.packageSources.add.value = $PackageDirectory
    [System.Xml.XmlWriterSettings] $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = $utf8
    $settings.Indent = $true
    [System.Xml.XmlWriter] $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
}

function Add-PackageMarker([string] $Path, [string] $Value) {
    [System.IO.Compression.ZipArchive] $archive = [System.IO.Compression.ZipFile]::Open(
        $Path,
        [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        [System.IO.Compression.ZipArchiveEntry] $entry = $archive.CreateEntry('filtrace-local-testing.txt')
        [System.IO.StreamWriter] $writer = [System.IO.StreamWriter]::new($entry.Open(), $utf8)
        try {
            $writer.Write($Value)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ToolState([string] $ToolPath) {
    if (-not (Test-Path -LiteralPath $ToolPath -PathType Container)) {
        return $null
    }

    [string] $json = Invoke-Dotnet -Arguments @(
        'tool', 'list', '--tool-path', $ToolPath, '--format', 'json') -Capture
    [object] $toolList = $json | ConvertFrom-Json -Depth 8
    [object[]] $matches = @($toolList.data | Where-Object packageId -IEQ 'klutzyninja.filtrace')
    Assert-True ($matches.Count -le 1) 'The isolated tool path contains duplicate Filtrace entries.'
    return $(if ($matches.Count -eq 0) { $null } else { $matches[0] })
}

function Copy-LocalPackages([string] $StatePath, [string] $SourceDirectory) {
    [string] $workspace = "$StatePath.workspace"
    $null = New-Item -ItemType Directory -Path $workspace -Force
    Write-Json (Join-Path $workspace '.filtrace-local-testing.json') ([ordered] @{
            schemaVersion = 1
            statePath = $StatePath
        })
    [string] $packageDirectory = Join-Path $workspace 'packages'
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
    [switch] $ManageCli,
    [string] $WorkflowPath = $workflow) {
    [System.Collections.Generic.List[string]] $arguments = @(
        '-NoProfile',
        '-File', $WorkflowPath,
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
    [string] $CliToolPath = '',
    [switch] $SkipCli,
    [string] $WorkflowPath = $workflow) {
    [System.Collections.Generic.List[string]] $arguments = @(
        '-NoProfile',
        '-File', $WorkflowPath,
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
    if ($SkipCli) {
        $arguments.Add('-SkipCli')
    }
    [string[]] $output = @(& (Get-Process -Id $PID).Path @arguments 2>&1)
    [int] $exitCode = $LASTEXITCODE
    $output | Out-Host
    Assert-True ($exitCode -ne 0) "Local Filtrace $Action unexpectedly succeeded."
    return $output -join [Environment]::NewLine
}

function Invoke-DefaultWorkflow(
    [string] $Action,
    [string] $Repository,
    [switch] $ManageCli) {
    [System.Collections.Generic.List[string]] $arguments = @(
        '-NoProfile',
        '-File', $workflow,
        '-Configuration', $Configuration,
        '-SkipBuild',
        '-SkipValidation'
    )
    if (-not $ManageCli) {
        $arguments.Add('-SkipCli')
    }
    if ($Action) {
        $arguments.Add('-Action')
        $arguments.Add($Action)
    }

    Push-Location $Repository
    try {
        & (Get-Process -Id $PID).Path @arguments 2>&1 | Out-Host
        [int] $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    Assert-True ($exitCode -eq 0) "Default local Filtrace $Action exited with code $exitCode."
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
    [string] $localFixturePackages = Join-Path $temporaryRoot 'local fixture packages'
    $null = New-Item -ItemType Directory -Path $localFixturePackages
    Get-ChildItem -LiteralPath $fixturePackages -File |
        Copy-Item -Destination $localFixturePackages
    [System.IO.FileInfo[]] $localCliPackages = @(
        Get-ChildItem -LiteralPath $localFixturePackages -File -Filter 'KlutzyNinja.Filtrace.*.nupkg' |
            Where-Object Name -NotLike 'KlutzyNinja.Filtrace.Mcp.*')
    Assert-True ($localCliPackages.Count -eq 1) `
        "Expected one mutable local CLI package; found $($localCliPackages.Count)."
    Add-PackageMarker $localCliPackages[0].FullName 'local package bytes'
    Assert-True (
        (Get-FileHash -LiteralPath $localCliPackages[0].FullName -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $cliPackages[0].FullName -Algorithm SHA256).Hash) `
        'The same-version local and baseline package bytes unexpectedly match.'

    # MCP JSON: reject a valid non-object root, and distinguish a read failure
    # from malformed JSON before any target configuration is changed.
    [string] $arrayMcpRoot = Join-Path $temporaryRoot 'array MCP root'
    [string] $arrayMcpConfig = Join-Path $arrayMcpRoot 'mcp.json'
    [string] $arrayMcpSkill = Join-Path $arrayMcpRoot 'skill/filtrace'
    [string] $arrayMcpState = Join-Path $arrayMcpRoot 'state.json'
    $null = New-Item -ItemType Directory -Path $arrayMcpRoot -Force
    [System.IO.File]::WriteAllText($arrayMcpConfig, '[]', $utf8)
    [string] $arrayMcpFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $arrayMcpConfig -SkillDestination $arrayMcpSkill `
        -StatePath $arrayMcpState -SkipCli
    Assert-True ($arrayMcpFailure -match 'configuration root must be a JSON object') `
        'A non-object MCP configuration root was not rejected.'
    Assert-True ([System.IO.File]::ReadAllText($arrayMcpConfig, $utf8) -ceq '[]') `
        'Non-object MCP root rejection changed the configuration file.'
    Assert-True (-not (Test-Path -LiteralPath $arrayMcpState)) `
        'Non-object MCP root rejection wrote rollback state.'
    Assert-True (-not (Test-Path -LiteralPath $arrayMcpSkill)) `
        'Non-object MCP root rejection changed the skill destination.'

    [string] $lockedMcpRoot = Join-Path $temporaryRoot 'locked MCP config'
    [string] $lockedMcpConfig = Join-Path $lockedMcpRoot 'mcp.json'
    [string] $lockedMcpSkill = Join-Path $lockedMcpRoot 'skill/filtrace'
    [string] $lockedMcpState = Join-Path $lockedMcpRoot 'state.json'
    Write-Json $lockedMcpConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [System.IO.FileStream] $lockedMcpStream = [System.IO.File]::Open(
        $lockedMcpConfig,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        [string] $lockedMcpFailure = Invoke-WorkflowFailure -Action Install `
            -McpConfigPath $lockedMcpConfig -SkillDestination $lockedMcpSkill `
            -StatePath $lockedMcpState -SkipCli
    }
    finally {
        $lockedMcpStream.Dispose()
    }
    Assert-True ($lockedMcpFailure -match 'configuration could not be read') `
        'An unreadable MCP configuration did not report a read failure.'
    Assert-True ($lockedMcpFailure -notmatch 'not valid JSON') `
        'An unreadable MCP configuration was mislabeled as malformed JSON.'
    Assert-True (-not (Test-Path -LiteralPath $lockedMcpState)) `
        'Unreadable MCP rejection wrote rollback state.'

    # Default scope: invoking from a consumer repository changes only that
    # repository, with rollback state keyed to its canonical path.
    [string] $consumerRoot = Join-Path $temporaryRoot 'consumer repository'
    $null = New-Item -ItemType Directory -Path (Join-Path $consumerRoot '.git') -Force
    [string] $consumerMcp = Join-Path $consumerRoot '.vscode/mcp.json'
    [string] $consumerSkill = Join-Path $consumerRoot '.agents/skills/filtrace'
    [string] $consumerState = Get-DefaultStatePath $consumerRoot
    [string] $consumerCli = Join-Path "$consumerState.workspace" $(if (
        [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        'tools/filtrace.exe'
    }
    else {
        'tools/filtrace'
    })
    Assert-True (-not (Test-Path -LiteralPath $consumerState)) `
        "Default consumer state unexpectedly existed before the test: '$consumerState'."
    Write-Json (Join-Path $consumerRoot 'global.json') ([ordered] @{
            sdk = [ordered] @{
                version = '1.0.0'
                rollForward = 'disable'
            }
        })
    Copy-LocalPackages $consumerState $localFixturePackages

    Invoke-DefaultWorkflow '' $consumerRoot -ManageCli
    Assert-True (Test-Path -LiteralPath $consumerMcp -PathType Leaf) `
        'Default install did not use the consumer repository MCP configuration.'
    Assert-True (Test-Path -LiteralPath (Join-Path $consumerSkill 'SKILL.md') -PathType Leaf) `
        'Default install did not vendor the skill into the consumer repository.'
    [object] $consumerLocalState = Read-Json $consumerState
    Assert-True ($consumerLocalState.schemaVersion -eq 4) 'Default install did not write schema version 4 state.'
    Assert-True ([string] $consumerLocalState.targetRepository -ceq $consumerRoot) `
        'Default install did not record the consumer repository.'
    Assert-True ([string] $consumerLocalState.mcp.path -ceq $consumerMcp) `
        'Default install recorded a non-project MCP path.'
    Assert-True ([string] $consumerLocalState.skill.destination -ceq $consumerSkill) `
        'Default install recorded a non-project skill path.'
    Assert-True ([bool] $consumerLocalState.cliManaged) `
        'Default install did not manage a repository-scoped CLI.'
    Assert-True ([string] $consumerLocalState.cliToolPath -ceq (Split-Path -Parent $consumerCli)) `
        'Default install did not record the manifest-owned CLI path.'
    Assert-True (Test-Path -LiteralPath $consumerCli -PathType Leaf) `
        'Default install did not create the manifest-owned CLI executable.'
    Assert-RestrictedFile $consumerMcp 'New project MCP configuration'
    Assert-RestrictedFile $consumerState 'New rollback manifest'

    Invoke-DefaultWorkflow 'restore' $consumerRoot -ManageCli
    Assert-True (-not (Test-Path -LiteralPath $consumerMcp)) `
        'Default restore left a project MCP file that did not exist before local mode.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $consumerRoot '.vscode'))) `
        'Default restore left an empty .vscode directory created by local mode.'
    Assert-True (-not (Test-Path -LiteralPath $consumerSkill)) `
        'Default restore left the project-local skill active.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $consumerRoot '.agents'))) `
        'Default restore left empty skill parent directories created by local mode.'
    Assert-True (-not (Test-Path -LiteralPath $consumerState)) `
        'Default restore left target-keyed state active.'
    Assert-True (-not (Test-Path -LiteralPath "$consumerState.workspace")) `
        'Default restore left the owned state workspace behind.'

    # Workspace ownership: a nonempty directory without the exact marker is never
    # claimed or cleaned as local-testing state.
    [string] $unownedRoot = Join-Path $temporaryRoot 'unowned workspace'
    [string] $unownedConfig = Join-Path $unownedRoot 'mcp.json'
    [string] $unownedSkill = Join-Path $unownedRoot '.agents/skills/filtrace'
    [string] $unownedState = Join-Path $unownedRoot 'state.json'
    [string] $unownedSentinel = Join-Path "$unownedState.workspace" 'keep.txt'
    Write-Json $unownedConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $unownedSentinel) -Force
    [System.IO.File]::WriteAllText($unownedSentinel, 'not owned', $utf8)
    [string] $unownedFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $unownedConfig -SkillDestination $unownedSkill `
        -StatePath $unownedState -SkipCli
    Assert-True ($unownedFailure -match 'Refusing to claim nonempty local-testing workspace') `
        'An unowned workspace failure was not actionable.'
    Assert-True ([System.IO.File]::ReadAllText($unownedSentinel, $utf8) -ceq 'not owned') `
        'The workflow changed data in an unowned workspace.'
    Assert-True (-not (Test-Path -LiteralPath $unownedState)) `
        'The workflow wrote state after refusing an unowned workspace.'
    Assert-True (-not (Test-Path -LiteralPath $unownedSkill)) `
        'The workflow changed the skill after refusing an unowned workspace.'

    # Concurrency: the StatePath lock rejects an overlapping owner before target
    # mutation, while an independent state key remains usable.
    [string] $lockRoot = Join-Path $temporaryRoot 'state lock'
    [string] $lockedConfig = Join-Path $lockRoot 'locked-mcp.json'
    [string] $lockedSkill = Join-Path $lockRoot 'locked-skill/filtrace'
    [string] $lockedState = Join-Path $lockRoot 'locked-state.json'
    [string] $independentConfig = Join-Path $lockRoot 'independent-mcp.json'
    [string] $independentSkill = Join-Path $lockRoot 'independent-skill/filtrace'
    [string] $independentState = Join-Path $lockRoot 'independent-state.json'
    Write-Json $lockedConfig ([ordered] @{
            servers = [ordered] @{ docs = [ordered] @{ type = 'http'; url = 'https://lock.invalid/mcp' } }
            inputs = @()
        })
    Write-Json $independentConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [byte[]] $lockedConfigBefore = [System.IO.File]::ReadAllBytes($lockedConfig)
    [string] $lockPath = Get-StateLockPath $lockedState
    [System.IO.FileStream] $heldLock = [System.IO.File]::Open(
        $lockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        [string] $lockFailure = Invoke-WorkflowFailure -Action Install `
            -McpConfigPath $lockedConfig -SkillDestination $lockedSkill `
            -StatePath $lockedState -SkipCli
        Assert-True ($lockFailure -match 'Another local Filtrace action is already using state') `
            'The overlapping StatePath failure was not actionable.'
        Assert-True (
            [System.Linq.Enumerable]::SequenceEqual(
                $lockedConfigBefore,
                [System.IO.File]::ReadAllBytes($lockedConfig))) `
            'An overlapping action changed MCP configuration before lock rejection.'
        Assert-True (-not (Test-Path -LiteralPath $lockedSkill)) `
            'An overlapping action changed the skill before lock rejection.'
        Assert-True (-not (Test-Path -LiteralPath $lockedState)) `
            'An overlapping action wrote state before lock rejection.'

        Invoke-Workflow 'Install' $independentConfig $independentSkill $independentState
        Invoke-Workflow 'Restore' $independentConfig $independentSkill $independentState
    }
    finally {
        $heldLock.Dispose()
    }
    Invoke-Workflow 'Install' $lockedConfig $lockedSkill $lockedState
    Invoke-Workflow 'Restore' $lockedConfig $lockedSkill $lockedState
    Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue

    # Legacy migration: schema version 2 can be restored, but cannot refresh the
    # old broad setup, and custom manifest siblings are no longer treated as owned.
    [string] $legacyRoot = Join-Path $temporaryRoot 'legacy state'
    [string] $legacyConfig = Join-Path $legacyRoot 'mcp.json'
    [string] $legacySkill = Join-Path $legacyRoot 'skill/filtrace'
    [string] $legacyState = Join-Path $legacyRoot 'state/state.json'
    [string] $legacySibling = Join-Path (Split-Path -Parent $legacyState) 'packages/keep.txt'
    $null = New-Item -ItemType Directory -Path $legacySkill -Force
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $legacySibling) -Force
    [System.IO.File]::WriteAllText((Join-Path $legacySkill 'SKILL.md'), 'legacy local skill', $utf8)
    [System.IO.File]::WriteAllText($legacySibling, 'legacy sibling', $utf8)
    Write-Json $legacyConfig ([ordered] @{
            servers = [ordered] @{
                docs = [ordered] @{ type = 'http'; url = 'https://legacy.invalid/mcp' }
                filtrace = [ordered] @{ type = 'stdio'; command = 'dotnet'; args = @('local.dll') }
            }
            inputs = @()
        })
    Write-Json $legacyState ([ordered] @{
            schemaVersion = 2
            createdUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
            status = 'local-active'
            cliManaged = $false
            cliToolPath = $null
            cli = $null
            mcp = [ordered] @{
                path = $legacyConfig
                serverExisted = $false
                server = $null
            }
            skill = [ordered] @{
                destination = $legacySkill
                existed = $false
            }
        })
    [byte[]] $legacyConfigBefore = [System.IO.File]::ReadAllBytes($legacyConfig)
    [string] $legacyInstallFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $legacyConfig -SkillDestination $legacySkill `
        -StatePath $legacyState -SkipCli
    Assert-True ($legacyInstallFailure -match 'Legacy global local-testing state must be restored') `
        'Legacy state refresh was not rejected with migration guidance.'
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $legacyConfigBefore,
            [System.IO.File]::ReadAllBytes($legacyConfig))) `
        'Rejected legacy refresh changed MCP configuration.'

    Invoke-Workflow 'Restore' $legacyConfig $legacySkill $legacyState
    [object] $legacyRestoredConfig = Read-Json $legacyConfig
    Assert-True ($null -eq (Get-Property $legacyRestoredConfig.servers 'filtrace')) `
        'Legacy restore left the local MCP server active.'
    Assert-True ($legacyRestoredConfig.servers.docs.url -ceq 'https://legacy.invalid/mcp') `
        'Legacy restore changed an unrelated MCP server.'
    Assert-True (-not (Test-Path -LiteralPath $legacySkill)) `
        'Legacy restore left the local skill active.'
    Assert-True (-not (Test-Path -LiteralPath $legacyState)) `
        'Legacy restore left the rollback manifest active.'
    Assert-True ([System.IO.File]::ReadAllText($legacySibling, $utf8) -ceq 'legacy sibling') `
        'Legacy restore removed an unrelated custom manifest sibling.'

    [string] $legacyScopedRoot = Join-Path $temporaryRoot 'legacy scoped state'
    [string] $legacyScopedConfig = Join-Path $legacyScopedRoot 'mcp.json'
    [string] $legacyScopedSkill = Join-Path $legacyScopedRoot 'skill/filtrace'
    [string] $legacyScopedState = Join-Path $legacyScopedRoot 'state.json'
    [string] $legacyScopedWorkspace = "$legacyScopedState.workspace"
    $null = New-Item -ItemType Directory -Path $legacyScopedSkill -Force
    $null = New-Item -ItemType Directory -Path $legacyScopedWorkspace -Force
    [System.IO.File]::WriteAllText(
        (Join-Path $legacyScopedSkill 'SKILL.md'),
        'legacy scoped local skill',
        $utf8)
    Write-Json $legacyScopedConfig ([ordered] @{
            servers = [ordered] @{
                docs = [ordered] @{ type = 'http'; url = 'https://legacy-scoped.invalid/mcp' }
                filtrace = [ordered] @{ type = 'stdio'; command = 'dotnet'; args = @('local.dll') }
            }
            inputs = @()
        })
    Write-Json (Join-Path $legacyScopedWorkspace '.filtrace-local-testing.json') ([ordered] @{
            schemaVersion = 1
            statePath = $legacyScopedState
        })
    Write-Json $legacyScopedState ([ordered] @{
            schemaVersion = 3
            createdUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
            status = 'local-active'
            targetRepository = $legacyScopedRoot
            workspace = $legacyScopedWorkspace
            cliManaged = $false
            cliToolPath = $null
            cli = $null
            mcp = [ordered] @{
                path = $legacyScopedConfig
                fileExisted = $true
                existingAncestor = $legacyScopedRoot
                serverExisted = $false
                server = $null
            }
            skill = [ordered] @{
                destination = $legacyScopedSkill
                existingAncestor = $legacyScopedRoot
                existed = $false
            }
        })
    [string] $legacyScopedInstallFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $legacyScopedConfig -SkillDestination $legacyScopedSkill `
        -StatePath $legacyScopedState -SkipCli
    Assert-True ($legacyScopedInstallFailure -match 'Legacy repository-scoped local-testing state must be restored') `
        'Legacy scoped state refresh was not rejected with migration guidance.'

    Invoke-Workflow 'Restore' $legacyScopedConfig $legacyScopedSkill $legacyScopedState
    [object] $legacyScopedRestoredConfig = Read-Json $legacyScopedConfig
    Assert-True ($null -eq (Get-Property $legacyScopedRestoredConfig.servers 'filtrace')) `
        'Legacy scoped restore left the local MCP server active.'
    Assert-True ($legacyScopedRestoredConfig.servers.docs.url -ceq 'https://legacy-scoped.invalid/mcp') `
        'Legacy scoped restore changed an unrelated MCP server.'
    Assert-True (-not (Test-Path -LiteralPath $legacyScopedSkill)) `
        'Legacy scoped restore left the local skill active.'
    Assert-True (-not (Test-Path -LiteralPath $legacyScopedState)) `
        'Legacy scoped restore left the rollback manifest active.'
    Assert-True (-not (Test-Path -LiteralPath $legacyScopedWorkspace)) `
        'Legacy scoped restore left the owned workspace active.'

    # Path containment: exercise destructive overlap cases against a disposable
    # workflow copy, then prove a sibling-prefix destination is not over-rejected.
    [string] $copiedRoot = Join-Path $temporaryRoot 'workflow copy'
    [string] $copiedWorkflow = Join-Path $copiedRoot 'tools/Use-LocalFiltrace.ps1'
    [string] $copiedSkillSource = Join-Path $copiedRoot '.agents/skills/filtrace'
    [string] $copiedMcpDll = Join-Path $copiedRoot "src/Filtrace.Mcp/bin/$Configuration/net10.0/Filtrace.Mcp.dll"
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $copiedWorkflow) -Force
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $copiedSkillSource) -Force
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $copiedMcpDll) -Force
    Copy-Item -LiteralPath $workflow -Destination $copiedWorkflow
    Copy-Item -LiteralPath $skillSource -Destination $copiedSkillSource -Recurse
    [System.IO.File]::WriteAllText($copiedMcpDll, 'test MCP assembly placeholder', $utf8)

    [string[]] $overlapDestinations = @(
        $copiedSkillSource,
        (Split-Path -Parent $copiedSkillSource),
        (Join-Path $copiedSkillSource 'nested-destination'))
    for ($overlapIndex = 0; $overlapIndex -lt $overlapDestinations.Count; $overlapIndex++) {
        [string] $overlapConfig = Join-Path $copiedRoot "overlap-$overlapIndex/mcp.json"
        [string] $overlapState = Join-Path $copiedRoot "overlap-$overlapIndex/state.json"
        Write-Json $overlapConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
        [string] $overlapFailure = Invoke-WorkflowFailure -Action Install `
            -McpConfigPath $overlapConfig -SkillDestination $overlapDestinations[$overlapIndex] `
            -StatePath $overlapState -SkipCli -WorkflowPath $copiedWorkflow
        Assert-True ($overlapFailure -match 'must not overlap') `
            "Overlap case $overlapIndex was not rejected before mutation."
        Assert-True (Test-Path -LiteralPath (Join-Path $copiedSkillSource 'SKILL.md') -PathType Leaf) `
            "Overlap case $overlapIndex changed the copied skill source."
        Assert-True (-not (Test-Path -LiteralPath $overlapState)) `
            "Overlap case $overlapIndex wrote rollback state."
    }

    [string] $managedOverlapRoot = Join-Path $copiedRoot 'managed-overlap'
    [string] $managedOverlapSkill = Join-Path $managedOverlapRoot 'skill/filtrace'
    [string] $stateInsideSkill = Join-Path $managedOverlapSkill 'state.json'
    [string] $stateOverlapConfig = Join-Path $managedOverlapRoot 'state-overlap-mcp.json'
    Write-Json $stateOverlapConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $stateOverlapFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $stateOverlapConfig -SkillDestination $managedOverlapSkill `
        -StatePath $stateInsideSkill -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($stateOverlapFailure -match 'SkillDestination and StatePath must not overlap') `
        'StatePath inside SkillDestination was not rejected before mutation.'
    Assert-True (-not (Test-Path -LiteralPath $managedOverlapSkill)) `
        'StatePath overlap rejection created the skill destination.'

    [string] $mcpInsideSkill = Join-Path $managedOverlapSkill 'mcp.json'
    [string] $mcpOverlapState = Join-Path $managedOverlapRoot 'mcp-overlap-state.json'
    Write-Json $mcpInsideSkill ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [byte[]] $mcpOverlapBytes = [System.IO.File]::ReadAllBytes($mcpInsideSkill)
    [string] $mcpOverlapFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $mcpInsideSkill -SkillDestination $managedOverlapSkill `
        -StatePath $mcpOverlapState -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($mcpOverlapFailure -match 'SkillDestination and McpConfigPath must not overlap') `
        'McpConfigPath inside SkillDestination was not rejected before mutation.'
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $mcpOverlapBytes,
            [System.IO.File]::ReadAllBytes($mcpInsideSkill))) `
        'MCP overlap rejection changed the existing MCP file.'
    Assert-True (-not (Test-Path -LiteralPath $mcpOverlapState)) `
        'MCP overlap rejection wrote rollback state.'

    [string] $aliasRoot = Join-Path $copiedRoot 'alias-consumer'
    [string] $agentsAlias = Join-Path $aliasRoot '.agents'
    $null = New-Item -ItemType Directory -Path $aliasRoot -Force
    [string] $linkType = if (
        [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        'Junction'
    }
    else {
        'SymbolicLink'
    }
    $null = New-Item `
        -ItemType $linkType `
        -Path $agentsAlias `
        -Target (Join-Path $copiedRoot '.agents')
    [string] $aliasSkill = Join-Path $agentsAlias 'skills/filtrace'
    [string] $aliasConfig = Join-Path $aliasRoot 'mcp.json'
    [string] $aliasState = Join-Path $aliasRoot 'state.json'
    Write-Json $aliasConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $aliasFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $aliasConfig -SkillDestination $aliasSkill `
        -StatePath $aliasState -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($aliasFailure -match 'Repository skill source and SkillDestination must not overlap') `
        'A linked SkillDestination aliasing the source was not rejected.'
    Assert-True (Test-Path -LiteralPath (Join-Path $copiedSkillSource 'SKILL.md') -PathType Leaf) `
        'Linked-path overlap rejection changed the copied skill source.'
    Assert-True (-not (Test-Path -LiteralPath $aliasState)) `
        'Linked-path overlap rejection wrote rollback state.'

    [string] $siblingConfig = Join-Path $copiedRoot 'sibling-prefix/mcp.json'
    [string] $siblingState = Join-Path $copiedRoot 'sibling-prefix/state.json'
    [string] $siblingSkill = "$copiedSkillSource-sibling"
    Write-Json $siblingConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    Invoke-Workflow -Action Install -McpConfigPath $siblingConfig `
        -SkillDestination $siblingSkill -StatePath $siblingState `
        -WorkflowPath $copiedWorkflow
    Assert-True (Test-Path -LiteralPath (Join-Path $siblingSkill 'SKILL.md') -PathType Leaf) `
        'A non-overlapping sibling-prefix destination was rejected.'
    Invoke-Workflow -Action Restore -McpConfigPath $siblingConfig `
        -SkillDestination $siblingSkill -StatePath $siblingState `
        -WorkflowPath $copiedWorkflow

    [string] $invalidConfig = Join-Path $copiedRoot 'invalid-action/mcp.json'
    [string] $invalidState = Join-Path $copiedRoot 'invalid-action/state.json'
    [string] $invalidSkill = Join-Path $copiedRoot 'invalid-action/skill/filtrace'
    Write-Json $invalidConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $invalidActionFailure = Invoke-WorkflowFailure -Action Unknown `
        -McpConfigPath $invalidConfig -SkillDestination $invalidSkill `
        -StatePath $invalidState -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($invalidActionFailure -match 'ValidateSet') `
        'An unrecognized Action failure was not produced by parameter validation.'
    Assert-True (-not (Test-Path -LiteralPath $invalidState)) `
        'An unrecognized Action wrote rollback state.'

    # Existing shipped setup: local install must preserve it, and restore must put
    # it back while retaining unrelated MCP changes made during local testing.
    [string] $existingRoot = Join-Path $temporaryRoot 'existing baseline'
    [string] $existingConfig = Join-Path $existingRoot 'mcp.json'
    [string] $existingSkill = Join-Path $existingRoot '.copilot/skills/filtrace'
    [string] $existingState = Join-Path $existingRoot 'state/local-state.json'
    [string] $existingStateParent = Split-Path -Parent $existingState
    [string[]] $unrelatedStateSiblings = @(
        (Join-Path $existingStateParent 'packages/keep.txt'),
        (Join-Path $existingStateParent 'skill-backup/keep.txt'),
        (Join-Path $existingStateParent 'cli-backup/keep.txt'),
        (Join-Path $existingStateParent 'local.nuget.config'),
        (Join-Path $existingStateParent 'restore.nuget.config'))
    foreach ($sibling in $unrelatedStateSiblings) {
        [string] $siblingParent = Split-Path -Parent $sibling
        $null = New-Item -ItemType Directory -Path $siblingParent -Force
        [System.IO.File]::WriteAllText($sibling, 'unrelated sibling', $utf8)
    }
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
    Set-TestRestrictiveFileSecurity $existingConfig
    [object] $existingConfigSecurity = Get-FileSecurityFingerprint $existingConfig

    Invoke-Workflow 'Install' $existingConfig $existingSkill $existingState
    Assert-True (Test-Path -LiteralPath $existingState -PathType Leaf) 'Install did not write reversible state.'
    [object] $localConfig = Read-Json $existingConfig
    Assert-True ($localConfig.servers.docs.url -ceq 'https://example.invalid/mcp') 'Install changed an unrelated MCP server.'
    Assert-True ($localConfig.servers.filtrace.command -ceq 'dotnet') 'Install did not select the local MCP DLL.'
    Assert-True (@($localConfig.servers.filtrace.args).Count -eq 1) 'Local MCP entry did not have exactly one DLL argument.'
    Assert-True (Test-Path -LiteralPath $localConfig.servers.filtrace.args[0] -PathType Leaf) 'Local MCP entry points to a missing DLL.'
    Assert-LocalSkill $existingSkill 'original overlay'
    Assert-True (
        (Get-FileSecurityFingerprint $existingConfig) -ceq $existingConfigSecurity) `
        'Install changed the existing MCP configuration security metadata.'

    [byte[]] $activeStateBytes = [System.IO.File]::ReadAllBytes($existingState)
    Invoke-Workflow 'Install' $existingConfig $existingSkill $existingState
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $activeStateBytes,
            [System.IO.File]::ReadAllBytes($existingState))) `
        'Refreshing local mode rewrote the original rollback manifest.'

    [string] $existingMarker = Join-Path "$existingState.workspace" '.filtrace-local-testing.json'
    [byte[]] $existingMarkerBytes = [System.IO.File]::ReadAllBytes($existingMarker)
    Remove-Item -LiteralPath $existingMarker -Force
    [string] $missingMarkerFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $existingConfig -SkillDestination $existingSkill `
        -StatePath $existingState -SkipCli
    Assert-True ($missingMarkerFailure -match 'Local-testing workspace marker does not exist') `
        'A missing active-state workspace marker did not fail closed.'
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $activeStateBytes,
            [System.IO.File]::ReadAllBytes($existingState))) `
        'Missing-marker rejection changed the rollback manifest.'
    [System.IO.File]::WriteAllBytes($existingMarker, $existingMarkerBytes)

    [string] $skillBackupFile = Join-Path "$existingState.workspace" 'skill-backup/SKILL.md'
    [byte[]] $skillBackupBytes = [System.IO.File]::ReadAllBytes($skillBackupFile)
    [System.IO.File]::WriteAllText($skillBackupFile, 'corrupt skill backup', $utf8)
    [string] $corruptSkillFailure = Invoke-WorkflowFailure -Action Restore `
        -McpConfigPath $existingConfig -SkillDestination $existingSkill `
        -StatePath $existingState
    Assert-True ($corruptSkillFailure -match 'skill backup hash changed') `
        'Corrupt skill backup failure was not actionable.'
    Assert-True ((Read-Json $existingState).status -ceq 'local-active') `
        'Skill backup preflight failure changed active state.'
    Assert-True (
        [System.IO.File]::ReadAllText((Join-Path $existingSkill 'SKILL.md'), $utf8) -cne
        'shipped skill core') `
        'Skill backup preflight failure restored corrupt prior content.'
    [System.IO.File]::WriteAllBytes($skillBackupFile, $skillBackupBytes)

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
    Assert-True (
        (Get-FileSecurityFingerprint $existingConfig) -ceq $existingConfigSecurity) `
        'Restore changed the existing MCP configuration security metadata.'
    foreach ($sibling in $unrelatedStateSiblings) {
        Assert-True (
            [System.IO.File]::ReadAllText($sibling, $utf8) -ceq 'unrelated sibling') `
            "Local setup changed unrelated state sibling '$sibling'."
    }

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

    Invoke-Workflow 'install' $absentConfig $absentSkill $absentState
    Assert-True (Test-Path -LiteralPath (Join-Path $absentSkill 'SKILL.md') -PathType Leaf) 'Install did not vendor the skill into an absent destination.'
    [System.IO.File]::WriteAllText((Join-Path $absentSkill 'overlay.md'), 'late overlay', $utf8)
    [string] $overlayCollision = "$absentState.restored-overlay.md"
    [System.IO.File]::WriteAllText($overlayCollision, 'unrelated retained file', $utf8)
    Invoke-Workflow 'restore' $absentConfig $absentSkill $absentState
    [object] $absentRestoredConfig = Read-Json $absentConfig
    Assert-True ($null -eq (Get-Property $absentRestoredConfig.servers 'filtrace')) 'Restore left a local MCP entry when none existed before.'
    Assert-True ($absentRestoredConfig.servers.docs.url -ceq 'https://example.invalid/mcp') 'Restore changed an unrelated MCP entry for the absent baseline.'
    Assert-True (-not (Test-Path -LiteralPath $absentSkill)) 'Restore left the locally vendored skill when none existed before.'
    Assert-True (-not (Test-Path -LiteralPath $absentState)) 'Restore left state for the absent baseline.'
    Assert-True ([System.IO.File]::ReadAllText($overlayCollision, $utf8) -ceq 'unrelated retained file') `
        'Restore overwrote an unrelated retained-overlay sibling.'
    [System.IO.FileInfo[]] $retainedOverlays = @(
        Get-ChildItem -LiteralPath (Split-Path -Parent $absentState) -File `
            -Filter 'local-state.json.restored-overlay.*.md')
    Assert-True ($retainedOverlays.Count -eq 1) `
        "Expected one collision-free retained overlay; found $($retainedOverlays.Count)."
    Assert-True ([System.IO.File]::ReadAllText($retainedOverlays[0].FullName, $utf8) -ceq 'late overlay') `
        'Collision-free retained overlay content changed.'
    Assert-RestrictedFile $retainedOverlays[0].FullName 'Retained overlay'

    # Cleanup retry: a committed cleanup state can resume with either the owned
    # workspace still present or already removed.
    foreach ($workspacePresent in @($true, $false)) {
        [string] $cleanupRoot = Join-Path $temporaryRoot "cleanup retry $workspacePresent"
        [string] $cleanupState = Join-Path $cleanupRoot 'state.json'
        [string] $cleanupWorkspace = "$cleanupState.workspace"
        if ($workspacePresent) {
            $null = New-Item -ItemType Directory -Path $cleanupWorkspace -Force
            Write-Json (Join-Path $cleanupWorkspace '.filtrace-local-testing.json') ([ordered] @{
                    schemaVersion = 1
                    statePath = $cleanupState
                })
            [System.IO.File]::WriteAllText(
                (Join-Path $cleanupWorkspace 'leftover.txt'),
                'leftover',
                $utf8)
        }
        Write-Json $cleanupState ([ordered] @{
                schemaVersion = 4
                createdUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
                status = 'cleanup-in-progress'
                targetRepository = $cleanupRoot
                workspace = $cleanupWorkspace
            })

        Invoke-Workflow -Action Restore `
            -McpConfigPath (Join-Path $cleanupRoot 'unused-mcp.json') `
            -SkillDestination (Join-Path $cleanupRoot 'unused-skill/filtrace') `
            -StatePath $cleanupState
        Assert-True (-not (Test-Path -LiteralPath $cleanupState)) `
            "Cleanup retry left state when workspacePresent=$workspacePresent."
        Assert-True (-not (Test-Path -LiteralPath $cleanupWorkspace)) `
            "Cleanup retry left the workspace when workspacePresent=$workspacePresent."
    }

    # CLI package installation and restoration use exact feed bytes when the
    # baseline and local package share one package ID and version.
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
    Copy-LocalPackages $cliState $localFixturePackages

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
    [System.IO.FileInfo[]] $installedPackages = @(
        Get-ChildItem -LiteralPath (Join-Path $cliToolPath '.store') -Recurse -File `
            -Filter "klutzyninja.filtrace.$fixtureVersion.nupkg")
    Assert-True ($installedPackages.Count -eq 1) `
        "Expected one locally installed CLI package; found $($installedPackages.Count)."
    Assert-True (
        (Get-FileHash -LiteralPath $installedPackages[0].FullName -Algorithm SHA256).Hash -ceq
        (Get-FileHash -LiteralPath $localCliPackages[0].FullName -Algorithm SHA256).Hash) `
        'Local CLI install did not use the byte-different package from its isolated source.'

    [string] $activeMcpConfig = [System.IO.File]::ReadAllText($cliConfig, $utf8)
    [byte[]] $baselinePackageBytes = [System.IO.File]::ReadAllBytes([string] $cliLocalState.cli.backupPackage)
    [System.IO.File]::WriteAllText([string] $cliLocalState.cli.backupPackage, 'corrupt package', $utf8)
    [string] $corruptBackupFailure = Invoke-WorkflowFailure -Action Restore -McpConfigPath $cliConfig `
        -SkillDestination $cliSkill -StatePath $cliState -CliToolPath $cliToolPath
    Assert-True ($corruptBackupFailure -match 'package backup hash changed') 'Corrupt CLI backup failure was not actionable.'
    Assert-True ((Read-Json $cliState).status -ceq 'local-active') 'CLI backup preflight failure changed active state.'
    [System.IO.File]::WriteAllBytes([string] $cliLocalState.cli.backupPackage, $baselinePackageBytes)

    Remove-Item -LiteralPath (Join-Path "$cliState.workspace" 'packages') -Recurse -Force
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
    [string] $emptyCliToolPath = Join-Path "$emptyCliState.workspace" 'tools'
    Write-Json $emptyCliConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    Copy-LocalPackages $emptyCliState $localFixturePackages
    Invoke-Workflow -Action Install -McpConfigPath $emptyCliConfig -SkillDestination $emptyCliSkill `
        -StatePath $emptyCliState -ManageCli
    Assert-True ($null -ne (Get-ToolState $emptyCliToolPath)) 'Local CLI was not installed into the empty tool path.'
    Assert-True ((Read-Json $emptyCliState).cliToolPath -ceq $emptyCliToolPath) `
        'Default CLI installation did not use the manifest-owned tool path.'
    Invoke-Workflow -Action Restore -McpConfigPath $emptyCliConfig -SkillDestination $emptyCliSkill `
        -StatePath $emptyCliState -ManageCli
    Assert-True ($null -eq (Get-ToolState $emptyCliToolPath)) 'Restore left a CLI that was absent from the baseline.'

    Write-Host 'Local Filtrace setup contract passed (repository scope, path safety, locking, exact CLI package restore, and failed-restore retry).'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}