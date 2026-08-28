#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

#Requires -Version 7.2

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
[bool] $hadOwnersRootOverride = Test-Path Env:FILTRACE_LOCAL_TESTING_OWNERS_ROOT
[string] $priorOwnersRootOverride = $env:FILTRACE_LOCAL_TESTING_OWNERS_ROOT
[string] $resourceOwnersRoot = Join-Path $temporaryRoot 'resource owners'
$env:FILTRACE_LOCAL_TESTING_OWNERS_ROOT = $resourceOwnersRoot

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

function Get-CaseVariant([string] $Name) {
    for ([int] $index = 0; $index -lt $Name.Length; $index++) {
        [char] $character = $Name[$index]
        if ($character -ge [char] 'a' -and $character -le [char] 'z') {
            return $Name.Substring(0, $index) +
                [char]::ToUpperInvariant($character) +
                $Name.Substring($index + 1)
        }
        if ($character -ge [char] 'A' -and $character -le [char] 'Z') {
            return $Name.Substring(0, $index) +
                [char]::ToLowerInvariant($character) +
                $Name.Substring($index + 1)
        }
    }

    return $null
}

function Get-PathComparison([string] $Path) {
    [string] $currentPath = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($currentPath)) {
        [System.IO.FileSystemInfo] $item = Get-Item `
            -LiteralPath $currentPath `
            -Force `
            -ErrorAction SilentlyContinue
        if ($null -ne $item -and $null -ne $item.Parent) {
            [string] $variant = Get-CaseVariant $item.Name
            if (-not [string]::IsNullOrWhiteSpace($variant)) {
                [System.IO.FileSystemInfo[]] $caseMatches = @(
                    Get-ChildItem -LiteralPath $item.Parent.FullName -Force |
                        Where-Object Name -IEQ $item.Name)
                if ($caseMatches.Count -gt 1) {
                    return [System.StringComparison]::Ordinal
                }
                if (Test-Path -LiteralPath (Join-Path $item.Parent.FullName $variant)) {
                    return [System.StringComparison]::OrdinalIgnoreCase
                }
                return [System.StringComparison]::Ordinal
            }
        }

        [string] $parentPath = Split-Path -Parent $currentPath
        if ([string]::IsNullOrWhiteSpace($parentPath) -or $parentPath -ceq $currentPath) {
            break
        }
        $currentPath = $parentPath
    }

    return $(if (Test-WindowsPlatform) {
            [System.StringComparison]::OrdinalIgnoreCase
        }
        else {
            [System.StringComparison]::Ordinal
        })
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

function Assert-RestrictedDirectory([string] $Path, [string] $Description) {
    Assert-True (Test-Path -LiteralPath $Path -PathType Container) `
        "$Description does not exist: '$Path'."
    if (Test-WindowsPlatform) {
        [System.Security.AccessControl.DirectorySecurity] $security = Get-Acl -LiteralPath $Path
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
        [System.IO.UnixFileMode]::UserWrite -bor
        [System.IO.UnixFileMode]::UserExecute)
    Assert-True (
        [int] [System.IO.File]::GetUnixFileMode($Path) -eq $expectedMode) `
        "$Description does not have Unix mode 0700."
}

function Get-PathIdentity([string] $Path) {
    [string] $identity = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($Path))
    if ((Get-PathComparison $identity) -eq [System.StringComparison]::OrdinalIgnoreCase) {
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

function Get-ResourceLockPath([string] $ResourcePath) {
    [string] $lockRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'filtrace-local-testing-resource-locks'
    $null = [System.IO.Directory]::CreateDirectory($lockRoot)
    [string] $resourceKey = "path:$(Get-PathIdentity $ResourcePath)"
    return Join-Path $lockRoot "$(Get-StableHash $resourceKey).lock"
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
    [string] $WorkflowPath = $workflow,
    [string] $TargetRepository = '') {
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
    if ($TargetRepository) {
        $arguments.Add('-TargetRepository')
        $arguments.Add($TargetRepository)
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
    [string] $WorkflowPath = $workflow,
    [string] $TargetRepository = '') {
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
    if ($TargetRepository) {
        $arguments.Add('-TargetRepository')
        $arguments.Add($TargetRepository)
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
    [switch] $ManageCli,
    [switch] $Build) {
    [System.Collections.Generic.List[string]] $arguments = @(
        '-NoProfile',
        '-File', $workflow,
        '-Configuration', $Configuration,
        '-SkipValidation'
    )
    if (-not $Build) {
        $arguments.Add('-SkipBuild')
    }
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

[string] $consumerStateForCleanup = ''
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

    [string] $caseRepository = Join-Path $temporaryRoot 'CaseRepository'
    $null = New-Item -ItemType Directory -Path $caseRepository
    [string] $caseVariantRepository = Join-Path $temporaryRoot 'caseRepository'
    [string] $caseState = Get-DefaultStatePath $caseRepository
    [string] $caseVariantState = Get-DefaultStatePath $caseVariantRepository
    if ((Get-PathComparison $caseRepository) -eq [System.StringComparison]::Ordinal) {
        $null = New-Item -ItemType Directory -Path $caseVariantRepository
        Assert-True ($caseState -cne $caseVariantState) `
            'Case-distinct repositories share state on a case-sensitive volume.'
    }
    else {
        Assert-True ($caseState -ceq $caseVariantState) `
            'Case aliases do not share state on a case-insensitive volume.'
    }

    if (Test-WindowsPlatform) {
        [string] $caseSensitiveRoot = Join-Path $temporaryRoot 'NTFS case sensitive'
        $null = New-Item -ItemType Directory -Path $caseSensitiveRoot
        [string[]] $caseSensitiveOutput = @(
            & fsutil.exe file setCaseSensitiveInfo $caseSensitiveRoot enable 2>&1)
        Assert-True ($LASTEXITCODE -eq 0) `
            "Could not enable NTFS case sensitivity: $($caseSensitiveOutput -join ' ')"
        [string] $caseSensitiveConfig = Join-Path $caseSensitiveRoot '.vscode/mcp.json'
        [string] $caseSensitiveSkill = Join-Path $caseSensitiveRoot '.agents/skills/filtrace'
        [string] $caseSensitiveState = Join-Path $caseSensitiveRoot '.local-testing/state.json'
        Invoke-Workflow -Action Install -McpConfigPath $caseSensitiveConfig `
            -SkillDestination $caseSensitiveSkill -StatePath $caseSensitiveState `
            -TargetRepository $caseSensitiveRoot
        Invoke-Workflow -Action Restore -McpConfigPath $caseSensitiveConfig `
            -SkillDestination $caseSensitiveSkill -StatePath $caseSensitiveState `
            -TargetRepository $caseSensitiveRoot
        Assert-True (-not (Test-Path -LiteralPath $caseSensitiveState)) `
            'Restore left state in an NTFS case-sensitive directory.'
    }

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
    $consumerStateForCleanup = $consumerState
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

    Invoke-DefaultWorkflow '' $consumerRoot -ManageCli -Build
    Assert-True (Test-Path -LiteralPath $consumerMcp -PathType Leaf) `
        'Default install did not use the consumer repository MCP configuration.'
    Assert-True (Test-Path -LiteralPath (Join-Path $consumerSkill 'SKILL.md') -PathType Leaf) `
        'Default install did not vendor the skill into the consumer repository.'
    [object] $consumerLocalState = Read-Json $consumerState
    Assert-True ($consumerLocalState.schemaVersion -eq 7) 'Default install did not write schema version 7 state.'
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
    Assert-RestrictedDirectory "$consumerState.workspace" 'Existing manifest-owned workspace'

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
    [object] $unownedSecurity = Get-FileSecurityFingerprint (Split-Path -Parent $unownedSentinel)
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
    Assert-True (
        (Get-FileSecurityFingerprint (Split-Path -Parent $unownedSentinel)) -ceq
        $unownedSecurity) `
        'The workflow changed permissions on an unowned workspace.'

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

    # Resource ownership: different StatePath values cannot concurrently or
    # sequentially claim the same MCP and skill resources.
    [string] $ownershipRoot = Join-Path $temporaryRoot 'resource ownership'
    [string] $ownershipStateRoot = Join-Path $temporaryRoot 'resource ownership states'
    [string] $ownershipConfig = Join-Path $ownershipRoot 'mcp.json'
    [string] $ownershipSkill = Join-Path $ownershipRoot 'skill/filtrace'
    [string] $firstOwnershipState = Join-Path $ownershipStateRoot 'first-state.json'
    [string] $secondOwnershipState = Join-Path $ownershipStateRoot 'second-state.json'
    Write-Json $ownershipConfig ([ordered] @{
            servers = [ordered] @{
                docs = [ordered] @{ type = 'http'; url = 'https://ownership.invalid/mcp' }
            }
            inputs = @()
        })

    [string] $resourceLockPath = Get-ResourceLockPath $ownershipConfig
    [System.IO.FileStream] $heldResourceLock = [System.IO.File]::Open(
        $resourceLockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        [string] $resourceLockFailure = Invoke-WorkflowFailure -Action Install `
            -McpConfigPath $ownershipConfig -SkillDestination $ownershipSkill `
            -StatePath $secondOwnershipState -SkipCli
    }
    finally {
        $heldResourceLock.Dispose()
    }
    Assert-True ($resourceLockFailure -match 'Another local Filtrace action is mutating resource') `
        'A different StatePath did not respect the active resource lock.'
    Assert-True (-not (Test-Path -LiteralPath $secondOwnershipState)) `
        'Resource-lock rejection wrote the second rollback manifest.'

    Invoke-Workflow 'Install' $ownershipConfig $ownershipSkill $firstOwnershipState
    Assert-RestrictedDirectory "$firstOwnershipState.workspace" 'New manifest-owned workspace'
    [byte[]] $firstOwnershipStateBytes = [System.IO.File]::ReadAllBytes($firstOwnershipState)

    [string] $secondCheckoutRoot = Join-Path $ownershipRoot 'second checkout'
    [string] $secondCheckoutWorkflow = Join-Path $secondCheckoutRoot 'tools/Use-LocalFiltrace.ps1'
    [string] $secondCheckoutSkill = Join-Path $secondCheckoutRoot '.agents/skills/filtrace'
    [string] $secondCheckoutMcpDll = Join-Path $secondCheckoutRoot "src/Filtrace.Mcp/bin/$Configuration/net10.0/Filtrace.Mcp.dll"
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $secondCheckoutWorkflow) -Force
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $secondCheckoutSkill) -Force
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $secondCheckoutMcpDll) -Force
    Copy-Item -LiteralPath $workflow -Destination $secondCheckoutWorkflow
    Copy-Item -LiteralPath $skillSource -Destination $secondCheckoutSkill -Recurse
    [System.IO.File]::WriteAllText($secondCheckoutMcpDll, 'second checkout MCP placeholder', $utf8)
    [string] $crossCheckoutFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $ownershipConfig -SkillDestination $ownershipSkill `
        -StatePath $secondOwnershipState -SkipCli -WorkflowPath $secondCheckoutWorkflow
    Assert-True ($crossCheckoutFailure -match 'owned by') `
        'A second Filtrace checkout bypassed durable resource ownership.'
    Assert-True (-not (Test-Path -LiteralPath $secondOwnershipState)) `
        'Cross-checkout ownership rejection wrote another rollback manifest.'

    [string] $ownershipFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $ownershipConfig -SkillDestination $ownershipSkill `
        -StatePath $secondOwnershipState -SkipCli
    Assert-True ($ownershipFailure -match 'owned by') `
        'A second StatePath was not rejected by durable resource ownership.'
    Assert-True (-not (Test-Path -LiteralPath $secondOwnershipState)) `
        'Resource ownership rejection wrote the second rollback manifest.'
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $firstOwnershipStateBytes,
            [System.IO.File]::ReadAllBytes($firstOwnershipState))) `
        'A rejected second owner changed the first rollback manifest.'
    Assert-True ((Read-Json $ownershipConfig).servers.filtrace.command -ceq 'dotnet') `
        'A rejected second owner changed the active MCP entry.'

    [string] $overlapOwnershipConfig = Join-Path $ownershipRoot 'overlap-mcp.json'
    [string] $overlapOwnershipSkill = Join-Path $ownershipSkill 'nested'
    [string] $overlapOwnershipState = Join-Path $ownershipRoot 'overlap-state.json'
    Write-Json $overlapOwnershipConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $overlapOwnershipFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $overlapOwnershipConfig -SkillDestination $overlapOwnershipSkill `
        -StatePath $overlapOwnershipState -SkipCli
    Assert-True ($overlapOwnershipFailure -match 'owned by') `
        'A descendant resource path was not rejected by durable ownership.'
    Assert-True (-not (Test-Path -LiteralPath $overlapOwnershipState)) `
        'Overlapping resource ownership wrote another rollback manifest.'

    [string] $rollbackAttackRoot = Join-Path $temporaryRoot 'rollback ownership attack'
    [string] $rollbackAttackConfig = Join-Path $rollbackAttackRoot 'mcp.json'
    [string] $rollbackAttackState = Join-Path $rollbackAttackRoot 'state.json'
    Write-Json $rollbackAttackConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $rollbackAttackFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $rollbackAttackConfig -SkillDestination $ownershipStateRoot `
        -StatePath $rollbackAttackState -SkipCli
    Assert-True ($rollbackAttackFailure -match 'owned by') `
        'A skill destination containing another manifest was not rejected.'
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $firstOwnershipStateBytes,
            [System.IO.File]::ReadAllBytes($firstOwnershipState))) `
        'Rollback-data ownership rejection changed the first manifest.'
    Assert-True (-not (Test-Path -LiteralPath $rollbackAttackState)) `
        'Rollback-data ownership rejection wrote another manifest.'

    Invoke-Workflow 'Restore' $ownershipConfig $ownershipSkill $firstOwnershipState
    Invoke-Workflow 'Install' $ownershipConfig $ownershipSkill $secondOwnershipState
    Invoke-Workflow 'Restore' $ownershipConfig $ownershipSkill $secondOwnershipState
    Assert-True ((Read-Json $ownershipConfig).servers.docs.url -ceq 'https://ownership.invalid/mcp') `
        'Sequential ownership changed an unrelated MCP entry.'
    Assert-True ($null -eq (Get-Property (Read-Json $ownershipConfig).servers 'filtrace')) `
        'Sequential ownership left local mode active after both restores.'

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

    [string] $legacyScopedWrongConfig = Join-Path $legacyScopedRoot 'wrong-mcp.json'
    Write-Json $legacyScopedWrongConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $legacyScopedMismatch = Invoke-WorkflowFailure -Action Restore `
        -McpConfigPath $legacyScopedWrongConfig -SkillDestination $legacyScopedSkill `
        -StatePath $legacyScopedState -SkipCli
    Assert-True ($legacyScopedMismatch -match 'state does not match this invocation') `
        'Legacy scoped path mismatch was not rejected before Restore.'
    [object[]] $legacyMismatchOwners = @(
        Get-ChildItem -LiteralPath $resourceOwnersRoot `
            -File -Filter '*.json' -ErrorAction SilentlyContinue |
            Where-Object {
                [string] (Read-Json $_.FullName).statePath -ceq $legacyScopedState
            })
    Assert-True ($legacyMismatchOwners.Count -eq 0) `
        'Legacy scoped path mismatch created resource ownership records.'
    Assert-True ((Read-Json $legacyScopedState).status -ceq 'local-active') `
        'Legacy scoped path mismatch changed active state.'

    [string] $legacyScopedConfigJson = [System.IO.File]::ReadAllText($legacyScopedConfig, $utf8)
    [System.IO.File]::WriteAllText($legacyScopedConfig, '{', $utf8)
    [string] $legacyScopedRestoreFailure = Invoke-WorkflowFailure -Action Restore `
        -McpConfigPath $legacyScopedConfig -SkillDestination $legacyScopedSkill `
        -StatePath $legacyScopedState -SkipCli
    Assert-True ($legacyScopedRestoreFailure -match 'not valid JSON') `
        'Legacy scoped Restore failure was not actionable.'
    Assert-True ((Read-Json $legacyScopedState).status -ceq 'restore-in-progress') `
        'Legacy scoped Restore failure did not retain retryable state.'
    [System.IO.File]::WriteAllText($legacyScopedConfig, $legacyScopedConfigJson, $utf8)
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

    [string] $schemaFiveRoot = Join-Path $temporaryRoot 'schema five state'
    [string] $schemaFiveConfig = Join-Path $schemaFiveRoot 'mcp.json'
    [string] $schemaFiveSkill = Join-Path $schemaFiveRoot 'skill/filtrace'
    [string] $schemaFiveState = Join-Path $schemaFiveRoot 'state.json'
    [string] $schemaFiveWorkspace = "$schemaFiveState.workspace"
    $null = New-Item -ItemType Directory -Path $schemaFiveSkill -Force
    $null = New-Item -ItemType Directory -Path $schemaFiveWorkspace -Force
    [System.IO.File]::WriteAllText((Join-Path $schemaFiveSkill 'SKILL.md'), 'schema five local skill', $utf8)
    Write-Json $schemaFiveConfig ([ordered] @{
            servers = [ordered] @{
                docs = [ordered] @{ type = 'http'; url = 'https://schema-five.invalid/mcp' }
                filtrace = [ordered] @{ type = 'stdio'; command = 'dotnet'; args = @('local.dll') }
            }
            inputs = @()
        })
    Write-Json (Join-Path $schemaFiveWorkspace '.filtrace-local-testing.json') ([ordered] @{
            schemaVersion = 1
            statePath = $schemaFiveState
        })
    Write-Json $schemaFiveState ([ordered] @{
            schemaVersion = 5
            createdUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
            status = 'local-active'
            targetRepository = $schemaFiveRoot
            workspace = $schemaFiveWorkspace
            cliManaged = $false
            cliToolPath = $null
            cli = $null
            resourceKeys = @(
                "path:$(Get-PathIdentity $schemaFiveConfig)",
                "path:$(Get-PathIdentity $schemaFiveSkill)")
            mcp = [ordered] @{
                path = $schemaFiveConfig
                fileExisted = $true
                existingAncestor = $schemaFiveRoot
                serverExisted = $false
                server = $null
            }
            skill = [ordered] @{
                destination = $schemaFiveSkill
                existingAncestor = $schemaFiveRoot
                existed = $false
                backupSha256 = $null
            }
        })
    [string] $schemaFiveInstallFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $schemaFiveConfig -SkillDestination $schemaFiveSkill `
        -StatePath $schemaFiveState -SkipCli
    Assert-True ($schemaFiveInstallFailure -match 'Legacy repository-scoped local-testing state must be restored') `
        'Schema-5 Install was not rejected with migration guidance.'
    Invoke-Workflow 'Restore' $schemaFiveConfig $schemaFiveSkill $schemaFiveState
    Assert-True (-not (Test-Path -LiteralPath $schemaFiveState)) `
        'Schema-5 Restore left the manifest active.'
    Assert-True (-not (Test-Path -LiteralPath $schemaFiveWorkspace)) `
        'Schema-5 Restore left the workspace active.'
    Assert-True (-not (Test-Path -LiteralPath $schemaFiveSkill)) `
        'Schema-5 Restore left the local skill active.'
    [object[]] $schemaFiveOwners = @(
        Get-ChildItem -LiteralPath $resourceOwnersRoot `
            -File -Filter '*.json' -ErrorAction SilentlyContinue |
            Where-Object {
                [string] (Read-Json $_.FullName).statePath -ceq $schemaFiveState
            })
    Assert-True ($schemaFiveOwners.Count -eq 0) `
        'Schema-5 Restore left migrated resource ownership active.'

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

    [string] $targetGuardRoot = Join-Path $copiedRoot 'target-guard'
    [string] $targetGuardRepository = Join-Path $targetGuardRoot 'consumer'
    [string] $targetGuardSentinel = Join-Path $targetGuardRepository 'keep.txt'
    [string] $targetGuardConfig = Join-Path $copiedRoot 'target-guard-mcp.json'
    $null = New-Item -ItemType Directory -Path $targetGuardRepository -Force
    [System.IO.File]::WriteAllText($targetGuardSentinel, 'consumer repository', $utf8)
    Write-Json $targetGuardConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    foreach ($dangerousSkill in @($targetGuardRepository, $targetGuardRoot)) {
        [string] $targetGuardState = Join-Path $copiedRoot "target-guard-$([guid]::NewGuid().ToString('N')).json"
        [string] $targetGuardFailure = Invoke-WorkflowFailure -Action Install `
            -McpConfigPath $targetGuardConfig -SkillDestination $dangerousSkill `
            -StatePath $targetGuardState -SkipCli -WorkflowPath $copiedWorkflow `
            -TargetRepository $targetGuardRepository
        Assert-True ($targetGuardFailure -match 'SkillDestination must not contain TargetRepository') `
            "Dangerous SkillDestination '$dangerousSkill' was not rejected."
        Assert-True ([System.IO.File]::ReadAllText($targetGuardSentinel, $utf8) -ceq 'consumer repository') `
            "Dangerous SkillDestination '$dangerousSkill' changed the target repository."
        Assert-True (-not (Test-Path -LiteralPath $targetGuardState)) `
            "Dangerous SkillDestination '$dangerousSkill' wrote rollback state."
    }

    [string] $equalWorkspaceState = Join-Path $targetGuardRoot 'equal-workspace-state.json'
    [string] $equalWorkspaceRepository = "$equalWorkspaceState.workspace"
    [string] $equalWorkspaceConfig = Join-Path $targetGuardRoot 'equal-workspace-mcp.json'
    [string] $equalWorkspaceSkill = Join-Path $targetGuardRoot 'equal-workspace-skill/filtrace'
    $null = New-Item -ItemType Directory -Path $equalWorkspaceRepository -Force
    Write-Json $equalWorkspaceConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $equalWorkspaceFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $equalWorkspaceConfig -SkillDestination $equalWorkspaceSkill `
        -StatePath $equalWorkspaceState -SkipCli -WorkflowPath $copiedWorkflow `
        -TargetRepository $equalWorkspaceRepository
    Assert-True ($equalWorkspaceFailure -match 'workspace must not contain TargetRepository') `
        'A workspace equal to TargetRepository was not rejected.'
    Assert-True (Test-Path -LiteralPath $equalWorkspaceRepository -PathType Container) `
        'Equal-workspace rejection removed TargetRepository.'
    Assert-True (-not (Test-Path -LiteralPath $equalWorkspaceState)) `
        'Equal-workspace rejection wrote rollback state.'

    [string] $containingWorkspaceState = Join-Path $targetGuardRoot 'containing-workspace-state.json'
    [string] $containingWorkspace = "$containingWorkspaceState.workspace"
    [string] $containedRepository = Join-Path $containingWorkspace 'consumer'
    [string] $containedSentinel = Join-Path $containedRepository 'keep.txt'
    [string] $containingWorkspaceConfig = Join-Path $targetGuardRoot 'containing-workspace-mcp.json'
    [string] $containingWorkspaceSkill = Join-Path $targetGuardRoot 'containing-workspace-skill/filtrace'
    $null = New-Item -ItemType Directory -Path $containedRepository -Force
    [System.IO.File]::WriteAllText($containedSentinel, 'contained repository', $utf8)
    Write-Json (Join-Path $containingWorkspace '.filtrace-local-testing.json') ([ordered] @{
            schemaVersion = 1
            statePath = $containingWorkspaceState
        })
    Write-Json $containingWorkspaceConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $containingWorkspaceFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $containingWorkspaceConfig -SkillDestination $containingWorkspaceSkill `
        -StatePath $containingWorkspaceState -SkipCli -WorkflowPath $copiedWorkflow `
        -TargetRepository $containedRepository
    Assert-True ($containingWorkspaceFailure -match 'workspace must not contain TargetRepository') `
        'A workspace containing TargetRepository was not rejected.'
    Assert-True ([System.IO.File]::ReadAllText($containedSentinel, $utf8) -ceq 'contained repository') `
        'Containing-workspace rejection changed TargetRepository.'
    Assert-True (-not (Test-Path -LiteralPath $containingWorkspaceState)) `
        'Containing-workspace rejection wrote rollback state.'

    [string] $nestedWorkspaceRepository = Join-Path $targetGuardRoot 'nested-workspace-repository'
    [string] $nestedWorkspaceSentinel = Join-Path $nestedWorkspaceRepository 'keep.txt'
    [string] $nestedWorkspaceConfig = Join-Path $nestedWorkspaceRepository '.vscode/mcp.json'
    [string] $nestedWorkspaceSkill = Join-Path $nestedWorkspaceRepository '.agents/skills/filtrace'
    [string] $nestedWorkspaceState = Join-Path $nestedWorkspaceRepository '.local-testing/state.json'
    $null = New-Item -ItemType Directory -Path $nestedWorkspaceRepository -Force
    [System.IO.File]::WriteAllText($nestedWorkspaceSentinel, 'nested workspace repository', $utf8)
    Write-Json $nestedWorkspaceConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    Invoke-Workflow -Action Install -McpConfigPath $nestedWorkspaceConfig `
        -SkillDestination $nestedWorkspaceSkill -StatePath $nestedWorkspaceState `
        -WorkflowPath $copiedWorkflow -TargetRepository $nestedWorkspaceRepository
    Invoke-Workflow -Action Restore -McpConfigPath $nestedWorkspaceConfig `
        -SkillDestination $nestedWorkspaceSkill -StatePath $nestedWorkspaceState `
        -WorkflowPath $copiedWorkflow -TargetRepository $nestedWorkspaceRepository
    Assert-True (
        [System.IO.File]::ReadAllText($nestedWorkspaceSentinel, $utf8) -ceq
        'nested workspace repository') `
        'A workspace below TargetRepository changed the repository sentinel.'

    [string] $sharedRootSkill = Join-Path $copiedRoot 'artifacts/local-testing/owners'
    [string] $sharedRootSentinel = Join-Path $sharedRootSkill 'keep.txt'
    [string] $sharedRootConfig = Join-Path $copiedRoot 'shared-root-mcp.json'
    [string] $sharedRootState = Join-Path $copiedRoot 'custom-state/state.json'
    $null = New-Item -ItemType Directory -Path $sharedRootSkill -Force
    [System.IO.File]::WriteAllText($sharedRootSentinel, 'shared registry', $utf8)
    Write-Json $sharedRootConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $sharedRootFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $sharedRootConfig -SkillDestination $sharedRootSkill `
        -StatePath $sharedRootState -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($sharedRootFailure -match 'SkillDestination and Shared local-testing root must not overlap') `
        'SkillDestination inside the shared local-testing root was not rejected.'
    Assert-True ([System.IO.File]::ReadAllText($sharedRootSentinel, $utf8) -ceq 'shared registry') `
        'Shared local-testing root rejection changed existing registry content.'
    Assert-True (-not (Test-Path -LiteralPath $sharedRootState)) `
        'Shared local-testing root rejection wrote rollback state.'

    [string] $sharedMcpPath = Join-Path $sharedRootSkill 'mcp.json'
    [string] $sharedMcpSkill = Join-Path $copiedRoot 'shared-mcp-skill/filtrace'
    [string] $sharedMcpState = Join-Path $copiedRoot 'shared-mcp-state/state.json'
    [string] $sharedMcpFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $sharedMcpPath -SkillDestination $sharedMcpSkill `
        -StatePath $sharedMcpState -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($sharedMcpFailure -match 'McpConfigPath and Shared local-testing root must not overlap') `
        'MCP configuration inside the shared local-testing root was not rejected.'
    Assert-True (-not (Test-Path -LiteralPath $sharedMcpState)) `
        'Shared-root MCP rejection wrote rollback state.'

    [string] $ownerRegistrySentinel = Join-Path $resourceOwnersRoot 'keep.txt'
    $null = New-Item -ItemType Directory -Path $resourceOwnersRoot -Force
    [System.IO.File]::WriteAllText($ownerRegistrySentinel, 'machine-wide registry', $utf8)

    [string] $registryOverlapConfig = Join-Path $copiedRoot 'registry-overlap-mcp.json'
    [string] $registryOverlapSkill = Join-Path $copiedRoot 'registry-overlap-skill/filtrace'
    Write-Json $registryOverlapConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [object[]] $registryOverlapCases = @(
        [pscustomobject] @{
            Name = 'skill'
            McpConfigPath = $registryOverlapConfig
            SkillDestination = $resourceOwnersRoot
            StatePath = Join-Path $copiedRoot 'registry-skill-state.json'
            CliToolPath = ''
            SkipCli = $true
            Expected = 'SkillDestination and Resource ownership registry must not overlap'
        },
        [pscustomobject] @{
            Name = 'mcp'
            McpConfigPath = Join-Path $resourceOwnersRoot 'mcp.json'
            SkillDestination = $registryOverlapSkill
            StatePath = Join-Path $copiedRoot 'registry-mcp-state.json'
            CliToolPath = ''
            SkipCli = $true
            Expected = 'McpConfigPath and Resource ownership registry must not overlap'
        },
        [pscustomobject] @{
            Name = 'cli'
            McpConfigPath = $registryOverlapConfig
            SkillDestination = $registryOverlapSkill
            StatePath = Join-Path $copiedRoot 'registry-cli-state.json'
            CliToolPath = $resourceOwnersRoot
            SkipCli = $false
            Expected = 'CliToolPath and Resource ownership registry must not overlap'
        })
    foreach ($registryOverlapCase in $registryOverlapCases) {
        [hashtable] $registryOverlapArguments = @{
            Action = 'Install'
            McpConfigPath = [string] $registryOverlapCase.McpConfigPath
            SkillDestination = [string] $registryOverlapCase.SkillDestination
            StatePath = [string] $registryOverlapCase.StatePath
            WorkflowPath = $copiedWorkflow
        }
        if ([bool] $registryOverlapCase.SkipCli) {
            $registryOverlapArguments.SkipCli = $true
        }
        else {
            $registryOverlapArguments.CliToolPath = [string] $registryOverlapCase.CliToolPath
        }
        [string] $registryOverlapFailure = Invoke-WorkflowFailure @registryOverlapArguments
        Assert-True ($registryOverlapFailure -match [regex]::Escape(
                [string] $registryOverlapCase.Expected)) `
            "The $($registryOverlapCase.Name) path was not rejected inside the resource registry."
        Assert-True (-not (Test-Path -LiteralPath $registryOverlapCase.StatePath)) `
            "The $($registryOverlapCase.Name) registry-overlap case wrote rollback state."
        Assert-True (
            [System.IO.File]::ReadAllText($ownerRegistrySentinel, $utf8) -ceq
            'machine-wide registry') `
            "The $($registryOverlapCase.Name) registry-overlap case changed registry content."
    }

    [string] $sharedStatePath = Join-Path $resourceOwnersRoot 'state.json'
    [string] $sharedStateConfig = Join-Path $copiedRoot 'shared-state-mcp.json'
    [string] $sharedStateSkill = Join-Path $copiedRoot 'shared-state-skill/filtrace'
    Write-Json $sharedStateConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $sharedStateFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $sharedStateConfig -SkillDestination $sharedStateSkill `
        -StatePath $sharedStatePath -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($sharedStateFailure -match 'StatePath and Resource ownership registry must not overlap') `
        'StatePath inside the resource ownership registry was not rejected.'
    Assert-True (-not (Test-Path -LiteralPath $sharedStatePath)) `
        'Owner-registry StatePath rejection wrote rollback state.'
    Assert-True ([System.IO.File]::ReadAllText($ownerRegistrySentinel, $utf8) -ceq 'machine-wide registry') `
        'Owner-registry StatePath rejection changed registry content.'

    [string] $sharedCliPath = Join-Path $sharedRootSkill 'tools'
    [string] $sharedCliConfig = Join-Path $copiedRoot 'shared-cli-mcp.json'
    [string] $sharedCliSkill = Join-Path $copiedRoot 'shared-cli-skill/filtrace'
    [string] $sharedCliState = Join-Path $copiedRoot 'shared-cli-state/state.json'
    Write-Json $sharedCliConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $sharedCliFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $sharedCliConfig -SkillDestination $sharedCliSkill `
        -StatePath $sharedCliState -CliToolPath $sharedCliPath `
        -WorkflowPath $copiedWorkflow
    Assert-True ($sharedCliFailure -match 'CliToolPath and Shared local-testing root must not overlap') `
        'External CLI path inside the shared local-testing root was not rejected.'
    Assert-True (-not (Test-Path -LiteralPath $sharedCliState)) `
        'Shared-root CLI rejection wrote rollback state.'
    Assert-True ([System.IO.File]::ReadAllText($sharedRootSentinel, $utf8) -ceq 'shared registry') `
        'Shared-root path rejection changed existing registry content.'

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

    if (-not (Test-WindowsPlatform)) {
        [string] $linkedMcpRoot = Join-Path $copiedRoot 'linked-mcp'
        [string] $linkedMcpTarget = Join-Path $linkedMcpRoot 'actual-mcp.json'
        [string] $linkedMcpConfig = Join-Path $linkedMcpRoot 'mcp.json'
        [string] $linkedMcpSkill = Join-Path $linkedMcpRoot 'skill/filtrace'
        [string] $linkedMcpState = Join-Path $linkedMcpRoot 'state.json'
        Write-Json $linkedMcpTarget ([ordered] @{ servers = [ordered] @{}; inputs = @() })
        $null = New-Item -ItemType SymbolicLink -Path $linkedMcpConfig -Target $linkedMcpTarget
        [byte[]] $linkedMcpTargetBytes = [System.IO.File]::ReadAllBytes($linkedMcpTarget)
        [string] $linkedMcpFailure = Invoke-WorkflowFailure -Action Install `
            -McpConfigPath $linkedMcpConfig -SkillDestination $linkedMcpSkill `
            -StatePath $linkedMcpState -SkipCli -WorkflowPath $copiedWorkflow
        Assert-True ($linkedMcpFailure -match 'configuration must not be a symbolic link') `
            'A linked MCP configuration was not rejected before baseline capture.'
        Assert-True ((Get-Item -LiteralPath $linkedMcpConfig -Force).LinkType -ceq 'SymbolicLink') `
            'Linked MCP rejection replaced the symbolic link.'
        Assert-True (
            [System.Linq.Enumerable]::SequenceEqual(
                $linkedMcpTargetBytes,
                [System.IO.File]::ReadAllBytes($linkedMcpTarget))) `
            'Linked MCP rejection changed the link target.'
        Assert-True (-not (Test-Path -LiteralPath $linkedMcpState)) `
            'Linked MCP rejection wrote rollback state.'

        [string] $linkedStateRoot = Join-Path $copiedRoot 'linked-state'
        [string] $linkedStateTarget = Join-Path $linkedStateRoot 'actual-state.json'
        [string] $linkedStatePath = Join-Path $linkedStateRoot 'state.json'
        [string] $linkedStateConfig = Join-Path $linkedStateRoot 'mcp.json'
        [string] $linkedStateSkill = Join-Path $linkedStateRoot 'skill/filtrace'
        Write-Json $linkedStateTarget ([ordered] @{ schemaVersion = 6 })
        Write-Json $linkedStateConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
        $null = New-Item -ItemType SymbolicLink -Path $linkedStatePath -Target $linkedStateTarget
        [byte[]] $linkedStateTargetBytes = [System.IO.File]::ReadAllBytes($linkedStateTarget)
        [string] $linkedStateFailure = Invoke-WorkflowFailure -Action Restore `
            -McpConfigPath $linkedStateConfig -SkillDestination $linkedStateSkill `
            -StatePath $linkedStatePath -SkipCli -WorkflowPath $copiedWorkflow
        Assert-True ($linkedStateFailure -match 'state must not be a symbolic link') `
            'A linked StatePath was not rejected before reading state.'
        Assert-True ((Get-Item -LiteralPath $linkedStatePath -Force).LinkType -ceq 'SymbolicLink') `
            'Linked StatePath rejection replaced the symbolic link.'
        Assert-True (
            [System.Linq.Enumerable]::SequenceEqual(
                $linkedStateTargetBytes,
                [System.IO.File]::ReadAllBytes($linkedStateTarget))) `
            'Linked StatePath rejection changed the link target.'
    }

    [string] $linkedSkillRoot = Join-Path $copiedRoot 'linked-skill'
    [string] $linkedSkillTarget = Join-Path $linkedSkillRoot 'actual-skill'
    [string] $linkedSkillDestination = Join-Path $linkedSkillRoot 'skill-link'
    [string] $linkedSkillConfig = Join-Path $linkedSkillRoot 'mcp.json'
    [string] $linkedSkillState = Join-Path $linkedSkillRoot 'state.json'
    $null = New-Item -ItemType Directory -Path $linkedSkillTarget -Force
    [string] $linkedSkillSentinel = Join-Path $linkedSkillTarget 'keep.txt'
    [System.IO.File]::WriteAllText($linkedSkillSentinel, 'linked skill target', $utf8)
    Write-Json $linkedSkillConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
    [string] $linkedSkillType = if (Test-WindowsPlatform) { 'Junction' } else { 'SymbolicLink' }
    $null = New-Item `
        -ItemType $linkedSkillType `
        -Path $linkedSkillDestination `
        -Target $linkedSkillTarget
    [string] $linkedSkillFailure = Invoke-WorkflowFailure -Action Install `
        -McpConfigPath $linkedSkillConfig -SkillDestination $linkedSkillDestination `
        -StatePath $linkedSkillState -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($linkedSkillFailure -match 'SkillDestination must not be a symbolic link') `
        'A linked SkillDestination was not rejected before baseline capture.'
    Assert-True ([System.IO.File]::ReadAllText($linkedSkillSentinel, $utf8) -ceq 'linked skill target') `
        'Linked SkillDestination rejection changed its target.'
    Assert-True (-not (Test-Path -LiteralPath $linkedSkillState)) `
        'Linked SkillDestination rejection wrote rollback state.'

    [string] $retargetRoot = Join-Path $copiedRoot 'retargeted-ancestor'
    [string] $retargetOriginal = Join-Path $retargetRoot 'original'
    [string] $retargetReplacement = Join-Path $retargetRoot 'replacement'
    [string] $retargetAlias = Join-Path $retargetRoot 'alias'
    [string] $retargetConfig = Join-Path $retargetAlias 'mcp.json'
    [string] $retargetSkill = Join-Path $retargetRoot 'skill/filtrace'
    [string] $retargetState = Join-Path $retargetRoot 'state.json'
    $null = New-Item -ItemType Directory -Path $retargetOriginal -Force
    $null = New-Item -ItemType Directory -Path $retargetReplacement -Force
    Write-Json (Join-Path $retargetOriginal 'mcp.json') ([ordered] @{
            servers = [ordered] @{
                docs = [ordered] @{ type = 'http'; url = 'https://original.invalid/mcp' }
            }
            inputs = @()
        })
    Write-Json (Join-Path $retargetReplacement 'mcp.json') ([ordered] @{
            servers = [ordered] @{
                sentinel = [ordered] @{ type = 'http'; url = 'https://replacement.invalid/mcp' }
            }
            inputs = @()
        })
    [string] $retargetLinkType = if (Test-WindowsPlatform) { 'Junction' } else { 'SymbolicLink' }
    $null = New-Item -ItemType $retargetLinkType -Path $retargetAlias -Target $retargetOriginal
    Invoke-Workflow 'Install' $retargetConfig $retargetSkill $retargetState `
        -SkipCli -WorkflowPath $copiedWorkflow
    Remove-Item -LiteralPath $retargetAlias -Force
    $null = New-Item -ItemType $retargetLinkType -Path $retargetAlias -Target $retargetReplacement
    [byte[]] $retargetReplacementBytes =
        [System.IO.File]::ReadAllBytes((Join-Path $retargetReplacement 'mcp.json'))
    [string] $retargetFailure = Invoke-WorkflowFailure -Action Restore `
        -McpConfigPath $retargetConfig -SkillDestination $retargetSkill `
        -StatePath $retargetState -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($retargetFailure -match 'resolved resource paths no longer match') `
        'A retargeted MCP ancestor was not rejected before restore.'
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $retargetReplacementBytes,
            [System.IO.File]::ReadAllBytes((Join-Path $retargetReplacement 'mcp.json')))) `
        'Retargeted-ancestor rejection changed the replacement MCP configuration.'
    Assert-True (Test-Path -LiteralPath $retargetState -PathType Leaf) `
        'Retargeted-ancestor rejection removed rollback state.'

    [object] $retargetSchemaSixState = Read-Json $retargetState
    $retargetSchemaSixState.schemaVersion = 6
    Write-Json $retargetState $retargetSchemaSixState
    [string] $retargetSchemaSixFailure = Invoke-WorkflowFailure -Action Restore `
        -McpConfigPath $retargetConfig -SkillDestination $retargetSkill `
        -StatePath $retargetState -SkipCli -WorkflowPath $copiedWorkflow
    Assert-True ($retargetSchemaSixFailure -match 'resolved resource paths no longer match') `
        'A schema-6 manifest did not reject a retargeted MCP ancestor.'
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            $retargetReplacementBytes,
            [System.IO.File]::ReadAllBytes((Join-Path $retargetReplacement 'mcp.json')))) `
        'Schema-6 retarget rejection changed the replacement MCP configuration.'
    Remove-Item -LiteralPath $retargetAlias -Force
    $null = New-Item -ItemType $retargetLinkType -Path $retargetAlias -Target $retargetOriginal
    Invoke-Workflow 'Restore' $retargetConfig $retargetSkill $retargetState `
        -SkipCli -WorkflowPath $copiedWorkflow

    [string[]] $nestedLinkKinds = if (Test-WindowsPlatform) {
        @('directory')
    }
    else {
        @('file', 'directory')
    }
    foreach ($nestedLinkKind in $nestedLinkKinds) {
        [string] $nestedLinkRoot = Join-Path $copiedRoot "nested-$nestedLinkKind-link"
        [string] $nestedLinkSkill = Join-Path $nestedLinkRoot 'skill/filtrace'
        [string] $nestedLinkConfig = Join-Path $nestedLinkRoot 'mcp.json'
        [string] $nestedLinkState = Join-Path $nestedLinkRoot 'state.json'
        [string] $nestedLinkTarget = Join-Path $nestedLinkRoot "external-$nestedLinkKind"
        [string] $nestedLinkPath = Join-Path $nestedLinkSkill "$nestedLinkKind-link"
        $null = New-Item -ItemType Directory -Path $nestedLinkSkill -Force
        [System.IO.File]::WriteAllText((Join-Path $nestedLinkSkill 'SKILL.md'), 'prior skill', $utf8)
        Write-Json $nestedLinkConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
        if ($nestedLinkKind -ceq 'file') {
            [System.IO.File]::WriteAllText($nestedLinkTarget, 'external file', $utf8)
            $null = New-Item -ItemType SymbolicLink -Path $nestedLinkPath -Target $nestedLinkTarget
        }
        else {
            $null = New-Item -ItemType Directory -Path $nestedLinkTarget -Force
            [System.IO.File]::WriteAllText((Join-Path $nestedLinkTarget 'keep.txt'), 'external directory', $utf8)
            $null = New-Item `
                -ItemType $(if (Test-WindowsPlatform) { 'Junction' } else { 'SymbolicLink' }) `
                -Path $nestedLinkPath `
                -Target $nestedLinkTarget
        }

        [string] $nestedLinkFailure = Invoke-WorkflowFailure -Action Install `
            -McpConfigPath $nestedLinkConfig -SkillDestination $nestedLinkSkill `
            -StatePath $nestedLinkState -SkipCli -WorkflowPath $copiedWorkflow
        Assert-True ($nestedLinkFailure -match 'Skill destination contains a symbolic link or junction') `
            "A nested $nestedLinkKind link was not rejected before skill backup."
        Assert-True (-not (Test-Path -LiteralPath $nestedLinkState)) `
            "Nested $nestedLinkKind link rejection wrote rollback state."
        if ($nestedLinkKind -ceq 'file') {
            Assert-True ([System.IO.File]::ReadAllText($nestedLinkTarget, $utf8) -ceq 'external file') `
                'Nested file-link rejection changed the external file.'
        }
        else {
            Assert-True (
                [System.IO.File]::ReadAllText((Join-Path $nestedLinkTarget 'keep.txt'), $utf8) -ceq
                'external directory') `
                'Nested directory-link rejection changed the external directory.'
        }
    }

    foreach ($reservedName in @('skill-backup', 'cli-backup', 'packages')) {
        [string] $reservedCliRoot = Join-Path $copiedRoot "reserved-cli-$reservedName"
        [string] $reservedCliConfig = Join-Path $reservedCliRoot 'mcp.json'
        [string] $reservedCliSkill = Join-Path $reservedCliRoot 'skill/filtrace'
        [string] $reservedCliState = Join-Path $reservedCliRoot 'state.json'
        [string] $reservedCliPath = Join-Path "$reservedCliState.workspace" $reservedName
        Write-Json $reservedCliConfig ([ordered] @{ servers = [ordered] @{}; inputs = @() })
        [string] $reservedCliFailure = Invoke-WorkflowFailure -Action Install `
            -McpConfigPath $reservedCliConfig -SkillDestination $reservedCliSkill `
            -StatePath $reservedCliState -CliToolPath $reservedCliPath `
            -WorkflowPath $copiedWorkflow
        Assert-True ($reservedCliFailure -match 'CliToolPath and .* must not overlap') `
            "CliToolPath overlap with '$reservedName' was not rejected."
        Assert-True (-not (Test-Path -LiteralPath $reservedCliState)) `
            "Reserved CLI overlap '$reservedName' wrote rollback state."
    }

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

    if (Test-WindowsPlatform) {
        [string] $cleanupLockPath = Join-Path "$existingState.workspace" 'cleanup-lock.txt'
        [System.IO.File]::WriteAllText($cleanupLockPath, 'locked cleanup file', $utf8)
        [System.IO.FileStream] $cleanupLock = [System.IO.File]::Open(
            $cleanupLockPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        try {
            [string] $cleanupFailure = Invoke-WorkflowFailure -Action Restore `
                -McpConfigPath $existingConfig -SkillDestination $existingSkill `
                -StatePath $existingState -SkipCli
        }
        finally {
            $cleanupLock.Dispose()
        }
        Assert-True ($cleanupFailure -match 'being used by another process') `
            'Locked workspace cleanup failure was not actionable.'
        Assert-True ((Read-Json $existingState).status -ceq 'cleanup-in-progress') `
            'Cleanup failure did not retain resumable cleanup state.'
        Assert-True (Test-Path -LiteralPath $existingMarker -PathType Leaf) `
            'Cleanup failure removed the ownership marker before fallible content.'
        Invoke-Workflow 'Restore' $existingConfig $existingSkill $existingState
    }
    else {
        Invoke-Workflow 'Restore' $existingConfig $existingSkill $existingState
    }
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
        [object[]] $cleanupOwners = @(
            Get-ChildItem -LiteralPath $resourceOwnersRoot `
                -File -Filter '*.json' -ErrorAction SilentlyContinue |
                Where-Object {
                    [string] (Read-Json $_.FullName).statePath -ceq $cleanupState
                })
        Assert-True ($cleanupOwners.Count -eq 0) `
            "Cleanup retry left resource ownership when workspacePresent=$workspacePresent."
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
    if (-not [string]::IsNullOrWhiteSpace($consumerStateForCleanup) -and
        (Test-Path -LiteralPath $consumerStateForCleanup -PathType Leaf)) {
        [string[]] $cleanupArguments = @(
            '-NoProfile',
            '-File', $workflow,
            '-Action', 'Restore',
            '-StatePath', $consumerStateForCleanup,
            '-SkipValidation')
        & (Get-Process -Id $PID).Path @cleanupArguments 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Could not restore test-owned state '$consumerStateForCleanup'; removing its isolated artifacts."
        }
    }
    [string] $ownersRoot = $resourceOwnersRoot
    if (Test-Path -LiteralPath $ownersRoot -PathType Container) {
        [System.StringComparison] $temporaryComparison = if (Test-WindowsPlatform) {
            [System.StringComparison]::OrdinalIgnoreCase
        }
        else {
            [System.StringComparison]::Ordinal
        }
        [string] $temporaryPrefix = "$([System.IO.Path]::GetFullPath($temporaryRoot))$([System.IO.Path]::DirectorySeparatorChar)"
        foreach ($ownerFile in Get-ChildItem -LiteralPath $ownersRoot -File -Filter '*.json') {
            try {
                [object] $owner = Read-Json $ownerFile.FullName
                [string] $ownerStatePath = [System.IO.Path]::GetFullPath([string] $owner.statePath)
                [bool] $isDefaultTestState = -not [string]::IsNullOrWhiteSpace($consumerStateForCleanup) -and
                    [string]::Equals(
                        $ownerStatePath,
                        [System.IO.Path]::GetFullPath($consumerStateForCleanup),
                        $temporaryComparison)
                if ($ownerStatePath.StartsWith($temporaryPrefix, $temporaryComparison) -or
                    $isDefaultTestState) {
                    Remove-Item -LiteralPath $ownerFile.FullName -Force
                }
            }
            catch {
                Write-Warning "Could not inspect test resource owner '$($ownerFile.FullName)': $($_.Exception.Message)"
            }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($consumerStateForCleanup)) {
        Remove-Item -LiteralPath "$consumerStateForCleanup.workspace" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $consumerStateForCleanup -Force -ErrorAction SilentlyContinue
    }
    if ($hadOwnersRootOverride) {
        $env:FILTRACE_LOCAL_TESTING_OWNERS_ROOT = $priorOwnersRootOverride
    }
    else {
        Remove-Item Env:FILTRACE_LOCAL_TESTING_OWNERS_ROOT -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
