#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

#Requires -Version 7.2

<#
.SYNOPSIS
  Switch the Filtrace CLI, MCP server, and agent skill to this checkout.

.DESCRIPTION
    Install builds and packs the checkout, validates the local MCP server, installs
    the CLI into target-specific storage, points the target repository's VS Code
    Filtrace MCP entry directly at the built DLL, and vendors the Filtrace skill into
    that repository.

    Before changing anything, Install records the existing CLI, Filtrace MCP entry,
    and skill directory in target-keyed state under artifacts/local-testing. Repeated
    installs keep that original baseline while refreshing the local build and skill.

  Restore removes the local setup and restores the recorded baseline. It changes
  only the Filtrace MCP entry, so unrelated MCP configuration added while testing
  is retained.

.PARAMETER Action
  Install the local checkout or Restore the setup recorded by the first Install.

.PARAMETER Configuration
  Build configuration used for the local CLI and MCP server. Defaults to Release.

.PARAMETER TargetRepository
    Repository to configure. Defaults to the current working directory.

.PARAMETER McpConfigPath
    VS Code mcp.json to update. Defaults to .vscode/mcp.json in TargetRepository.

.PARAMETER SkillDestination
    Directory that receives the local Filtrace skill. Defaults to
    .agents/skills/filtrace in TargetRepository.

.PARAMETER StatePath
    Reversible-state manifest. Defaults to target-keyed ignored storage under this
    checkout's artifacts/local-testing directory.

.PARAMETER CliToolPath
    Optional dotnet tool directory. Defaults to isolated storage owned by StatePath.
    The global tool is never changed by a new repository-scoped setup.

.PARAMETER SkipBuild
  Reuse packages and binaries from a prior successful local install.

.PARAMETER SkipCli
  Switch only MCP and skill state. The CLI is neither recorded nor changed.

.PARAMETER SkipValidation
  Skip the local MCP protocol check. Intended for isolated contract tests after a
  validated build, not routine use.

.EXAMPLE
    D:\repos\filtrace\tools\Use-LocalFiltrace.ps1

.EXAMPLE
    ./tools/Use-LocalFiltrace.ps1 -TargetRepository ../consumer

.EXAMPLE
  ./tools/Use-LocalFiltrace.ps1 -Action Restore
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Install', 'Restore')]
    [string] $Action = 'Install',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $TargetRepository,
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

function Get-FullPath([string] $Path, [string] $Description) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Description must be a nonempty path."
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Resolve-PhysicalPath([string] $Path, [int] $Depth = 0) {
    if ($Depth -gt 64) {
        throw "Path contains too many symbolic-link levels: '$Path'."
    }

    [string] $fullPath = [System.IO.Path]::GetFullPath($Path)
    [string] $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    [string] $relativePath = $fullPath.Substring($rootPath.Length)
    [char[]] $separators = @(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    [string[]] $segments = $relativePath.Split(
        $separators,
        [System.StringSplitOptions]::RemoveEmptyEntries)
    [string] $currentPath = $rootPath
    for ([int] $index = 0; $index -lt $segments.Length; $index++) {
        [string] $candidate = Join-Path $currentPath $segments[$index]
        [System.IO.FileSystemInfo] $item = $null
        try {
            $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
        }
        catch [System.Management.Automation.ItemNotFoundException] {
            for (; $index -lt $segments.Length; $index++) {
                $currentPath = Join-Path $currentPath $segments[$index]
            }
            break
        }
        catch {
            throw "Path component could not be inspected: '$candidate'. $($_.Exception.Message)"
        }

        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            [System.IO.FileSystemInfo] $target = $item.ResolveLinkTarget($true)
            if ($null -eq $target) {
                throw "Symbolic-link target could not be resolved: '$candidate'."
            }
            $currentPath = Resolve-PhysicalPath $target.FullName ($Depth + 1)
        }
        else {
            $currentPath = $item.FullName
        }
    }

    return [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($currentPath))
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
    [string] $currentPath = Resolve-PhysicalPath $Path
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

                [string] $variantPath = Join-Path $item.Parent.FullName $variant
                if (Test-Path -LiteralPath $variantPath) {
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

function Test-PathsEqual([string] $First, [string] $Second) {
    [bool] $firstEmpty = [string]::IsNullOrWhiteSpace($First)
    [bool] $secondEmpty = [string]::IsNullOrWhiteSpace($Second)
    if ($firstEmpty -or $secondEmpty) {
        return $firstEmpty -and $secondEmpty
    }

    [string] $firstPhysical = Resolve-PhysicalPath $First
    [string] $secondPhysical = Resolve-PhysicalPath $Second
    return [string]::Equals(
        $firstPhysical,
        $secondPhysical,
        (Get-PathComparison $firstPhysical))
}

function Assert-PathIsNotLink([string] $Path, [string] $Description) {
    [System.IO.FileSystemInfo] $item = $null
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch [System.Management.Automation.ItemNotFoundException] {
        return
    }
    catch {
        throw "$Description could not be inspected: '$Path'. $($_.Exception.Message)"
    }

    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must not be a symbolic link or junction: '$Path'."
    }
}

function Assert-DirectoryContainsNoLinks([string] $Path, [string] $Description) {
    [System.IO.FileSystemInfo[]] $items = @(
        Get-ChildItem -LiteralPath $Path -Force -Recurse)
    foreach ($item in $items) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description contains a symbolic link or junction: '$($item.FullName)'."
        }
    }
}

function Get-PathIdentity([string] $Path) {
    [string] $identity = Resolve-PhysicalPath $Path
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

function Get-DirectoryFingerprint([string] $Path, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description does not exist: '$Path'."
    }

    [System.Collections.Generic.SortedDictionary[string, object]] $entries =
        [System.Collections.Generic.SortedDictionary[string, object]]::new(
            [System.StringComparer]::Ordinal)
    [System.IO.FileSystemInfo[]] $items = @(
        Get-ChildItem -LiteralPath $Path -Force -Recurse)
    foreach ($item in $items) {
        [string] $relativePath = ([System.IO.Path]::GetRelativePath($Path, $item.FullName)).Replace(
            [System.IO.Path]::DirectorySeparatorChar,
            [char] '/')
        [object] $entry = $null
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $entry = [pscustomobject] [ordered] @{
                path = $relativePath
                kind = 'link'
                target = [string] $item.LinkTarget
            }
        }
        elseif ($item -is [System.IO.DirectoryInfo]) {
            $entry = [pscustomobject] [ordered] @{
                path = $relativePath
                kind = 'directory'
            }
        }
        else {
            $entry = [pscustomobject] [ordered] @{
                path = $relativePath
                kind = 'file'
                length = [long] $item.Length
                sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            }
        }
        $entries.Add($relativePath, $entry)
    }

    [object[]] $manifest = @($entries.Values)
    [string] $serialized = ConvertTo-Json -InputObject $manifest -Depth 8 -Compress
    return Get-StableHash $serialized
}

function Get-DefaultStatePath([string] $Repository) {
    [string] $identityHash = Get-StableHash (Get-PathIdentity $Repository)
    return Join-Path $root "artifacts/local-testing/repositories/$identityHash/state.json"
}

function Get-StateWorkspacePath([string] $ManifestPath) {
    return "$ManifestPath.workspace"
}

function Test-PathWithin(
    [string] $Candidate,
    [string] $Container,
    [System.StringComparison] $Comparison) {
    if ($Candidate.Equals($Container, $Comparison)) { return $true }

    [string] $prefix = $Container
    if (-not $prefix.EndsWith([System.IO.Path]::DirectorySeparatorChar) -and
        -not $prefix.EndsWith([System.IO.Path]::AltDirectorySeparatorChar)) {
        $prefix += [System.IO.Path]::DirectorySeparatorChar
    }
    return $Candidate.StartsWith($prefix, $Comparison)
}

function Assert-PathsDoNotOverlap(
    [string] $First,
    [string] $FirstDescription,
    [string] $Second,
    [string] $SecondDescription) {
    [string] $firstPhysical = Resolve-PhysicalPath $First
    [string] $secondPhysical = Resolve-PhysicalPath $Second
    if ((Test-PathWithin $firstPhysical $secondPhysical (Get-PathComparison $secondPhysical)) -or
        (Test-PathWithin $secondPhysical $firstPhysical (Get-PathComparison $firstPhysical))) {
        throw "$FirstDescription and $SecondDescription must not overlap: '$First' and '$Second'."
    }
}

function Enter-StateLock([string] $ManifestPath) {
    [string] $lockRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'filtrace-local-testing-locks'
    $null = [System.IO.Directory]::CreateDirectory($lockRoot)
    [string] $lockName = "$(Get-StableHash (Get-PathIdentity $ManifestPath)).lock"
    [string] $lockPath = Join-Path $lockRoot $lockName
    try {
        return [System.IO.File]::Open(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch [System.IO.IOException] {
        throw "Another local Filtrace action is already using state '$ManifestPath'."
    }
}

function Get-ResourceKeys(
    [string] $McpPath,
    [string] $SkillPath,
    [bool] $CliManaged,
    [string] $CliPath,
    [string] $ManifestPath,
    [string] $WorkspacePath) {
    [System.Collections.Generic.SortedSet[string]] $keys =
        [System.Collections.Generic.SortedSet[string]]::new(
            [System.StringComparer]::Ordinal)
    [void] $keys.Add("path:$(Get-PathIdentity $McpPath)")
    [void] $keys.Add("path:$(Get-PathIdentity $SkillPath)")
    [void] $keys.Add("path:$(Get-PathIdentity $ManifestPath)")
    [void] $keys.Add("path:$(Get-PathIdentity $WorkspacePath)")
    if ($CliManaged) {
        [void] $keys.Add($(if ([string]::IsNullOrWhiteSpace($CliPath)) {
                    'cli:global'
                }
                else {
                    "path:$(Get-PathIdentity $CliPath)"
                }))
    }

    return ,([string[]] $keys)
}

function Test-ResourceKeysEqual([string[]] $First, [string[]] $Second) {
    [string[]] $firstKeys = @($First | Sort-Object -CaseSensitive)
    [string[]] $secondKeys = @($Second | Sort-Object -CaseSensitive)
    if ($firstKeys.Count -ne $secondKeys.Count) { return $false }

    for ([int] $index = 0; $index -lt $firstKeys.Count; $index++) {
        if (-not [string]::Equals(
                $firstKeys[$index],
                $secondKeys[$index],
                [System.StringComparison]::Ordinal)) {
            return $false
        }
    }

    return $true
}

function Enter-ResourceLocks([string[]] $ResourceKeys) {
    [string] $lockRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'filtrace-local-testing-resource-locks'
    $null = [System.IO.Directory]::CreateDirectory($lockRoot)
    [System.Collections.Generic.List[System.IO.FileStream]] $locks =
        [System.Collections.Generic.List[System.IO.FileStream]]::new()
    try {
        foreach ($resourceKey in $ResourceKeys | Sort-Object -CaseSensitive -Unique) {
            [string] $lockPath = Join-Path $lockRoot "$(Get-StableHash $resourceKey).lock"
            try {
                [System.IO.FileStream] $lock = [System.IO.File]::Open(
                    $lockPath,
                    [System.IO.FileMode]::OpenOrCreate,
                    [System.IO.FileAccess]::ReadWrite,
                    [System.IO.FileShare]::None)
                $locks.Add($lock)
            }
            catch [System.IO.IOException] {
                throw "Another local Filtrace action is mutating resource '$resourceKey'."
            }
        }

        return ,$locks.ToArray()
    }
    catch {
        foreach ($lock in $locks) { $lock.Dispose() }
        throw
    }
}

function Get-ResourceOwnersRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:FILTRACE_LOCAL_TESTING_OWNERS_ROOT)) {
        return [System.IO.Path]::GetFullPath($env:FILTRACE_LOCAL_TESTING_OWNERS_ROOT)
    }
    if (Test-WindowsPlatform) {
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            throw 'LOCALAPPDATA is not set; resource ownership cannot be persisted.'
        }
        return Join-Path $env:LOCALAPPDATA 'Filtrace/local-testing/owners'
    }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) {
        if ([string]::IsNullOrWhiteSpace($HOME)) {
            throw 'HOME is not set; resource ownership cannot be persisted.'
        }
        return Join-Path $HOME 'Library/Application Support/Filtrace/local-testing/owners'
    }

    [string] $stateRoot = if ([string]::IsNullOrWhiteSpace($env:XDG_STATE_HOME)) {
        if ([string]::IsNullOrWhiteSpace($HOME)) {
            throw 'HOME is not set; resource ownership cannot be persisted.'
        }
        Join-Path $HOME '.local/state'
    }
    else {
        $env:XDG_STATE_HOME
    }
    return Join-Path $stateRoot 'filtrace/local-testing/owners'
}

function Get-ResourceOwnerPath([string] $ResourceKey) {
    return Join-Path (Get-ResourceOwnersRoot) "$(Get-StableHash $ResourceKey).json"
}

function Enter-ResourceRegistryLock {
    [string] $lockRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'filtrace-local-testing-resource-locks'
    $null = [System.IO.Directory]::CreateDirectory($lockRoot)
    [string] $lockPath = Join-Path $lockRoot 'registry.lock'
    try {
        return [System.IO.File]::Open(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch [System.IO.IOException] {
        throw 'Another local Filtrace action is updating resource ownership.'
    }
}

function Test-ResourceKeysOverlap([string] $First, [string] $Second) {
    if ($First -ceq $Second) { return $true }
    if (-not $First.StartsWith('path:', [System.StringComparison]::Ordinal) -or
        -not $Second.StartsWith('path:', [System.StringComparison]::Ordinal)) {
        return $false
    }

    [string] $firstPath = $First.Substring('path:'.Length)
    [string] $secondPath = $Second.Substring('path:'.Length)
    return (Test-PathWithin $firstPath $secondPath (Get-PathComparison $secondPath)) -or
        (Test-PathWithin $secondPath $firstPath (Get-PathComparison $firstPath))
}

function Get-ResourceOwners {
    [string] $ownersRoot = Get-ResourceOwnersRoot
    if (-not (Test-Path -LiteralPath $ownersRoot -PathType Container)) { return @() }

    [object[]] $owners = @(
        Get-ChildItem -LiteralPath $ownersRoot -File -Filter '*.json' |
            ForEach-Object {
                Read-JsonFile $_.FullName 'Local-testing resource ownership'
            })
    return $owners
}

function Claim-ResourceOwnership([string[]] $ResourceKeys, [string] $ManifestPath) {
    [System.IO.FileStream] $registryLock = Enter-ResourceRegistryLock
    [System.Collections.Generic.List[string]] $created =
        [System.Collections.Generic.List[string]]::new()
    try {
        [object[]] $owners = @(Get-ResourceOwners)
        foreach ($resourceKey in $ResourceKeys) {
            foreach ($owner in $owners) {
                if ((Test-ResourceKeysOverlap $resourceKey ([string] $owner.resourceKey)) -and
                    -not (Test-PathsEqual ([string] $owner.statePath) $ManifestPath)) {
                    throw "Local-testing resource '$resourceKey' overlaps '$($owner.resourceKey)', owned by state '$($owner.statePath)'."
                }
            }

            [string] $ownerPath = Get-ResourceOwnerPath $resourceKey
            if (Test-Path -LiteralPath $ownerPath -PathType Leaf) {
                [object] $owner = Read-JsonFile $ownerPath 'Local-testing resource ownership'
                if ($owner.schemaVersion -ne 1 -or
                    -not (Test-PathsEqual ([string] $owner.statePath) $ManifestPath)) {
                    throw "Local-testing resource '$resourceKey' is owned by state '$($owner.statePath)'."
                }
                continue
            }

            Write-JsonFile $ownerPath ([ordered] @{
                    schemaVersion = 1
                    resourceKey = $resourceKey
                    statePath = $ManifestPath
                })
            $created.Add($ownerPath)
        }

        return ,$created.ToArray()
    }
    catch {
        foreach ($ownerPath in $created) {
            Remove-Item -LiteralPath $ownerPath -Force -ErrorAction SilentlyContinue
        }
        throw
    }
    finally {
        $registryLock.Dispose()
    }
}

function Assert-ResourceOwnership([string[]] $ResourceKeys, [string] $ManifestPath) {
    [System.IO.FileStream] $registryLock = Enter-ResourceRegistryLock
    try {
        foreach ($resourceKey in $ResourceKeys) {
            [string] $ownerPath = Get-ResourceOwnerPath $resourceKey
            if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf)) {
                throw "Local-testing resource ownership is missing for '$resourceKey'."
            }
            [object] $owner = Read-JsonFile $ownerPath 'Local-testing resource ownership'
            if ($owner.schemaVersion -ne 1 -or
                -not (Test-PathsEqual ([string] $owner.statePath) $ManifestPath)) {
                throw "Local-testing resource '$resourceKey' is owned by state '$($owner.statePath)'."
            }
        }
    }
    finally {
        $registryLock.Dispose()
    }
}

function Remove-ResourceOwnership([string[]] $ResourceKeys, [string] $ManifestPath) {
    [System.IO.FileStream] $registryLock = Enter-ResourceRegistryLock
    try {
        foreach ($resourceKey in $ResourceKeys) {
            [string] $ownerPath = Get-ResourceOwnerPath $resourceKey
            if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf)) { continue }
            [object] $owner = Read-JsonFile $ownerPath 'Local-testing resource ownership'
            if ($owner.schemaVersion -ne 1 -or
                -not (Test-PathsEqual ([string] $owner.statePath) $ManifestPath)) {
                throw "Local-testing resource '$resourceKey' is owned by state '$($owner.statePath)'."
            }
            Remove-Item -LiteralPath $ownerPath -Force
        }
    }
    finally {
        $registryLock.Dispose()
    }
}

function Read-JsonFile([string] $Path, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description does not exist: '$Path'."
    }

    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Path -Force
    if ($file.Length -gt 4MB) {
        throw "$Description is larger than the 4 MB safety limit: '$Path'."
    }

    [string] $json = ''
    try {
        $json = [System.IO.File]::ReadAllText($file.FullName, $utf8)
    }
    catch {
        throw "$Description could not be read: $($_.Exception.Message)"
    }

    try {
        return ConvertFrom-Json -InputObject $json -Depth 32 -NoEnumerate
    }
    catch {
        throw "$Description is not valid JSON: $($_.Exception.Message)"
    }
}

function Test-WindowsPlatform {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Set-RestrictiveFileSecurity([string] $Path) {
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

    [System.IO.UnixFileMode] $mode =
        [System.IO.UnixFileMode]::UserRead -bor
        [System.IO.UnixFileMode]::UserWrite
    [System.IO.File]::SetUnixFileMode($Path, $mode)
}

function Copy-FileSecurity([string] $Source, [string] $Destination) {
    if (Test-WindowsPlatform) {
        [System.Security.AccessControl.FileSecurity] $security = Get-Acl -LiteralPath $Source
        Set-Acl -LiteralPath $Destination -AclObject $security
        return
    }

    [System.IO.UnixFileMode] $mode = [System.IO.File]::GetUnixFileMode($Source)
    [System.IO.File]::SetUnixFileMode($Destination, $mode)
}

function New-SecureEmptyFile([string] $Path, [string] $SecuritySource = '') {
    [System.IO.FileStreamOptions] $options = [System.IO.FileStreamOptions]::new()
    $options.Mode = [System.IO.FileMode]::CreateNew
    $options.Access = [System.IO.FileAccess]::Write
    $options.Share = [System.IO.FileShare]::None
    if (-not (Test-WindowsPlatform)) {
        $options.UnixCreateMode =
            [System.IO.UnixFileMode]::UserRead -bor
            [System.IO.UnixFileMode]::UserWrite
    }

    [System.IO.FileStream] $stream = [System.IO.FileStream]::new($Path, $options)
    $stream.Dispose()
    if (-not [string]::IsNullOrWhiteSpace($SecuritySource)) {
        Copy-FileSecurity $SecuritySource $Path
    }
    elseif (Test-WindowsPlatform) {
        Set-RestrictiveFileSecurity $Path
    }
}

function Write-NewSecureBytes([string] $Path, [byte[]] $Bytes) {
    [bool] $created = $false
    try {
        New-SecureEmptyFile $Path
        $created = $true
        [System.IO.File]::WriteAllBytes($Path, $Bytes)
    }
    catch {
        if ($created) {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Write-RetainedOverlay([string] $ManifestPath, [byte[]] $Bytes) {
    [string] $basePath = "$ManifestPath.restored-overlay.md"
    [string[]] $candidates = @(
        $basePath,
        "$ManifestPath.restored-overlay.$([guid]::NewGuid().ToString('N')).md")
    foreach ($candidate in $candidates) {
        try {
            Write-NewSecureBytes $candidate $Bytes
            return $candidate
        }
        catch {
            if (-not (Test-Path -LiteralPath $candidate)) { throw }
        }
    }

    throw "Could not select a collision-free retained overlay path beside '$ManifestPath'."
}

function Write-JsonFile([string] $Path, [object] $Value) {
    [string] $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $directory -Force
    }

    [string] $temporaryPath = Join-Path $directory ".$([System.IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [string] $json = ConvertTo-Json -InputObject $Value -Depth 32
        [string] $securitySource = if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $Path
        }
        else {
            ''
        }
        New-SecureEmptyFile $temporaryPath $securitySource
        [System.IO.File]::WriteAllText($temporaryPath, "$json`n", $utf8)
        [System.IO.File]::Move($temporaryPath, $Path, $true)
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-StateWorkspaceMarkerPath([string] $Workspace) {
    return Join-Path $Workspace '.filtrace-local-testing.json'
}

function Assert-StateWorkspace([string] $Workspace, [string] $ManifestPath) {
    if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) {
        throw "The local-testing workspace is missing: '$Workspace'."
    }

    [string] $markerPath = Get-StateWorkspaceMarkerPath $Workspace
    [object] $marker = Read-JsonFile $markerPath 'Local-testing workspace marker'
    if ($marker.schemaVersion -ne 1 -or
        -not (Test-PathsEqual ([string] $marker.statePath) $ManifestPath)) {
        throw "The local-testing workspace marker does not own '$Workspace'."
    }
}

function Initialize-StateWorkspace([string] $Workspace, [string] $ManifestPath) {
    if (Test-Path -LiteralPath $Workspace -PathType Leaf) {
        throw "The local-testing workspace is a file: '$Workspace'."
    }

    [string] $markerPath = Get-StateWorkspaceMarkerPath $Workspace
    if (Test-Path -LiteralPath $Workspace -PathType Container) {
        if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            Assert-StateWorkspace $Workspace $ManifestPath
            return
        }

        if (@(Get-ChildItem -LiteralPath $Workspace -Force).Count -ne 0) {
            throw "Refusing to claim nonempty local-testing workspace without an ownership marker: '$Workspace'."
        }
    }
    else {
        $null = New-Item -ItemType Directory -Path $Workspace -Force
    }

    Write-JsonFile $markerPath ([ordered] @{
            schemaVersion = 1
            statePath = $ManifestPath
        })
}

function Remove-StateWorkspace([string] $Workspace, [string] $ManifestPath) {
    Assert-StateWorkspace $Workspace $ManifestPath
    [string] $markerPath = Get-StateWorkspaceMarkerPath $Workspace
    Get-ChildItem -LiteralPath $Workspace -Force |
        Where-Object FullName -CNE $markerPath |
        Remove-Item -Recurse -Force
    Remove-Item -LiteralPath $markerPath -Force
    Remove-Item -LiteralPath $Workspace -Force
}

function Get-Property([object] $Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    return $Object.PSObject.Properties[$Name]
}

function Get-NearestExistingAncestor([string] $Path) {
    [string] $current = Split-Path -Parent $Path
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current -PathType Container) {
            return $current
        }
        if (Test-Path -LiteralPath $current) {
            throw "A required parent path is not a directory: '$current'."
        }

        [string] $parent = Split-Path -Parent $current
        if ($parent -ceq $current) { break }
        $current = $parent
    }

    throw "No existing parent directory was found for '$Path'."
}

function Remove-EmptyCreatedAncestors(
    [string] $Path,
    [string] $ExistingAncestor,
    [System.StringComparison] $Comparison) {
    [string] $current = Split-Path -Parent $Path
    while (-not [string]::IsNullOrWhiteSpace($current) -and
        -not (Test-PathsEqual $current $ExistingAncestor)) {
        if (-not (Test-Path -LiteralPath $current -PathType Container) -or
            @(Get-ChildItem -LiteralPath $current -Force).Count -ne 0) {
            return
        }

        Remove-Item -LiteralPath $current -Force
        $current = Split-Path -Parent $current
    }
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
        [System.IO.FileInfo] $mcpFile = Get-Item -LiteralPath $Path -Force
        if (($mcpFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "VS Code MCP configuration must not be a symbolic link or junction: '$Path'."
        }
        Read-JsonFile $Path 'VS Code MCP configuration'
    }
    else {
        [pscustomobject] [ordered] @{
            servers = [pscustomobject] @{}
            inputs = @()
        }
    }

    if ($null -eq $config -or
        $config.GetType() -ne [System.Management.Automation.PSCustomObject]) {
        throw "VS Code MCP configuration root must be a JSON object: '$Path'."
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

function Remove-EmptyMcpConfig([string] $Path) {
    [object] $config = Read-JsonFile $Path 'VS Code MCP configuration'
    [System.Management.Automation.PSPropertyInfo] $serversProperty = Get-Property $config 'servers'
    [System.Management.Automation.PSPropertyInfo] $inputsProperty = Get-Property $config 'inputs'
    [object[]] $otherProperties = @($config.PSObject.Properties | Where-Object {
            $_.Name -cne 'servers' -and $_.Name -cne 'inputs'
        })
    [int] $serverCount = if ($null -eq $serversProperty -or $null -eq $serversProperty.Value) {
        0
    }
    else {
        @($serversProperty.Value.PSObject.Properties).Count
    }
    [int] $inputCount = if ($null -eq $inputsProperty -or $null -eq $inputsProperty.Value) {
        0
    }
    else {
        @($inputsProperty.Value).Count
    }
    if ($serverCount -eq 0 -and $inputCount -eq 0 -and $otherProperties.Count -eq 0) {
        Remove-Item -LiteralPath $Path -Force
        return $true
    }

    return $false
}

function Invoke-Dotnet([string[]] $Arguments, [switch] $Capture) {
    Push-Location $root
    try {
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
    finally {
        Pop-Location
    }
}

function Get-CliScopeArguments {
    if ($CliToolPath) {
        return @('--tool-path', $CliToolPath)
    }

    return @('--global')
}

function Get-CliState {
    if ($CliToolPath -and
        -not (Test-Path -LiteralPath $CliToolPath -PathType Container)) {
        return [pscustomobject] [ordered] @{
            installed = $false
            version = $null
        }
    }

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

function Write-LocalNuGetConfig(
    [string] $Path,
    [string] $PackageDirectory) {
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

function Invoke-ToolInstall(
    [string] $PackageDirectory,
    [string] $Version,
    [string] $Workspace) {
    [string] $operationId = [guid]::NewGuid().ToString('N')
    [string] $configPath = Join-Path $Workspace "nuget-$operationId.config"
    Write-LocalNuGetConfig $configPath $PackageDirectory

    try {
        [string[]] $arguments = @('tool', 'install') + @(Get-CliScopeArguments) + @(
            '--configfile', $configPath,
            '--version', $Version,
            'KlutzyNinja.Filtrace')
        Invoke-Dotnet -Arguments $arguments
    }
    finally {
        Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
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

[string] $normalizedAction = if ($Action -ieq 'Install') { 'Install' } else { 'Restore' }
$TargetRepository = Get-FullPath $(if ($PSBoundParameters.ContainsKey('TargetRepository')) {
        $TargetRepository
    }
    else {
        $PWD.Path
    }) 'Target repository'
[System.StringComparison] $pathComparison = Get-PathComparison $TargetRepository
[string] $defaultStatePath = Get-DefaultStatePath $TargetRepository
[string] $legacyStatePath = Join-Path $root 'artifacts/local-testing/state.json'
$StatePath = Get-FullPath $(if ($PSBoundParameters.ContainsKey('StatePath')) {
        $StatePath
    }
    elseif ((Test-PathsEqual $TargetRepository $root) -and
        (Test-Path -LiteralPath $legacyStatePath -PathType Leaf)) {
        $legacyStatePath
    }
    else {
        $defaultStatePath
    }) 'State path'

[System.IO.FileStream] $stateLock = Enter-StateLock $StatePath
try {
    Assert-PathIsNotLink $StatePath 'Local-testing state'
    [object] $state = if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
        Read-JsonFile $StatePath 'Local-testing state'
    }
    else {
        $null
    }
    if ($null -ne $state -and $state.schemaVersion -notin @(2, 3, 4, 5, 6, 7)) {
        throw "Local-testing state has unsupported schema version '$($state.schemaVersion)': '$StatePath'."
    }

    if ($null -ne $state -and $state.schemaVersion -ge 3 -and
        -not $PSBoundParameters.ContainsKey('TargetRepository')) {
        $TargetRepository = Get-FullPath ([string] $state.targetRepository) 'Recorded target repository'
        $pathComparison = Get-PathComparison $TargetRepository
    }
    if ($normalizedAction -ceq 'Install' -and
        -not (Test-Path -LiteralPath $TargetRepository -PathType Container)) {
        throw "Target repository does not exist: '$TargetRepository'."
    }

    [string] $ownedStateWorkspace = Get-StateWorkspacePath $StatePath
    [bool] $legacyState = $null -ne $state -and $state.schemaVersion -eq 2
    [bool] $legacyScopedState = $null -ne $state -and $state.schemaVersion -in @(3, 4, 5, 6)
    if ($legacyState -and $normalizedAction -ceq 'Install') {
        throw "Legacy global local-testing state must be restored before starting a repository-scoped install: '$StatePath'."
    }
    if ($legacyScopedState -and $normalizedAction -ceq 'Install') {
        throw "Legacy repository-scoped local-testing state must be restored before starting a new install: '$StatePath'."
    }
    [string] $stateWorkspace = if ($legacyState) {
        Split-Path -Parent $StatePath
    }
    else {
        $ownedStateWorkspace
    }
    if ($null -ne $state -and -not $legacyState) {
        if (-not (Test-PathsEqual ([string] $state.workspace) $ownedStateWorkspace)) {
            throw "Recorded local-testing workspace does not match '$StatePath'."
        }
        if ($state.status -cne 'cleanup-in-progress') {
            Assert-StateWorkspace $stateWorkspace $StatePath
        }
    }

    $McpConfigPath = Get-FullPath $(if ($PSBoundParameters.ContainsKey('McpConfigPath')) {
            $McpConfigPath
        }
        elseif ($null -ne $state) {
            [string] $state.mcp.path
        }
        else {
            Join-Path $TargetRepository '.vscode/mcp.json'
        }) 'MCP configuration path'
    $SkillDestination = Get-FullPath $(if ($PSBoundParameters.ContainsKey('SkillDestination')) {
            $SkillDestination
        }
        elseif ($null -ne $state) {
            [string] $state.skill.destination
        }
        else {
            Join-Path $TargetRepository '.agents/skills/filtrace'
        }) 'Skill destination'

    Assert-PathIsNotLink $McpConfigPath 'VS Code MCP configuration'
    Assert-PathIsNotLink $SkillDestination 'SkillDestination'

    [string] $targetRepositoryPhysical = Resolve-PhysicalPath $TargetRepository
    [string] $skillDestinationPhysical = Resolve-PhysicalPath $SkillDestination
    [string] $stateWorkspacePhysical = Resolve-PhysicalPath $stateWorkspace
    if (Test-PathWithin `
        $targetRepositoryPhysical `
        $skillDestinationPhysical `
        (Get-PathComparison $skillDestinationPhysical)) {
        throw "SkillDestination must not contain TargetRepository: '$SkillDestination'."
    }
    if (Test-PathWithin `
        $targetRepositoryPhysical `
        $stateWorkspacePhysical `
        (Get-PathComparison $stateWorkspacePhysical)) {
        throw "Local-testing workspace must not contain TargetRepository: '$stateWorkspace'."
    }
    [string] $localTestingRoot = Join-Path $root 'artifacts/local-testing'
    [string] $resourceOwnersRoot = Get-ResourceOwnersRoot
    Assert-PathsDoNotOverlap `
        $SkillDestination 'SkillDestination' `
        $localTestingRoot 'Shared local-testing root'
    Assert-PathsDoNotOverlap `
        $McpConfigPath 'McpConfigPath' `
        $localTestingRoot 'Shared local-testing root'
    Assert-PathsDoNotOverlap `
        $SkillDestination 'SkillDestination' `
        $resourceOwnersRoot 'Resource ownership registry'
    Assert-PathsDoNotOverlap `
        $McpConfigPath 'McpConfigPath' `
        $resourceOwnersRoot 'Resource ownership registry'
    Assert-PathsDoNotOverlap `
        $StatePath 'StatePath' `
        $resourceOwnersRoot 'Resource ownership registry'
    Assert-PathsDoNotOverlap `
        $stateWorkspace 'Local-testing workspace' `
        $resourceOwnersRoot 'Resource ownership registry'

    [bool] $cliManaged = if ($null -ne $state -and
        -not $PSBoundParameters.ContainsKey('SkipCli')) {
        [bool] $state.cliManaged
    }
    else {
        -not [bool] $SkipCli
    }
    if (-not $cliManaged -and $PSBoundParameters.ContainsKey('CliToolPath')) {
        throw 'CliToolPath cannot be combined with SkipCli.'
    }
    $CliToolPath = if (-not $cliManaged) {
        $null
    }
    elseif ($PSBoundParameters.ContainsKey('CliToolPath')) {
        Get-FullPath $CliToolPath 'CLI tool path'
    }
    elseif ($null -ne $state) {
        if ([string]::IsNullOrWhiteSpace([string] $state.cliToolPath)) {
            $null
        }
        else {
            Get-FullPath ([string] $state.cliToolPath) 'Recorded CLI tool path'
        }
    }
    else {
        Join-Path $stateWorkspace 'tools'
    }
    if ($cliManaged -and -not $legacyState -and
        [string]::IsNullOrWhiteSpace($CliToolPath)) {
        throw 'Repository-scoped state must record an isolated CLI tool path.'
    }

    [string] $skillSourceFull = [System.IO.Path]::GetFullPath($skillSource)
    Assert-PathsDoNotOverlap `
        $skillSourceFull 'Repository skill source' `
        $SkillDestination 'SkillDestination'
    Assert-PathsDoNotOverlap `
        $skillSourceFull 'Repository skill source' `
        $StatePath 'StatePath'
    Assert-PathsDoNotOverlap `
        $skillSourceFull 'Repository skill source' `
        $stateWorkspace 'Local-testing workspace'
    Assert-PathsDoNotOverlap `
        $skillSourceFull 'Repository skill source' `
        $McpConfigPath 'McpConfigPath'
    Assert-PathsDoNotOverlap `
        $SkillDestination 'SkillDestination' `
        $StatePath 'StatePath'
    Assert-PathsDoNotOverlap `
        $SkillDestination 'SkillDestination' `
        $stateWorkspace 'Local-testing workspace'
    Assert-PathsDoNotOverlap `
        $SkillDestination 'SkillDestination' `
        $McpConfigPath 'McpConfigPath'
    Assert-PathsDoNotOverlap `
        $StatePath 'StatePath' `
        $McpConfigPath 'McpConfigPath'
    Assert-PathsDoNotOverlap `
        $stateWorkspace 'Local-testing workspace' `
        $McpConfigPath 'McpConfigPath'

    [string] $workspaceMarker = Get-StateWorkspaceMarkerPath $stateWorkspace
    [string] $skillBackup = Join-Path $stateWorkspace 'skill-backup'
    [string] $cliBackup = Join-Path $stateWorkspace 'cli-backup'
    [string] $packageDirectory = Join-Path $stateWorkspace 'packages'

    [bool] $cliOwnedByWorkspace = $CliToolPath -and
        (Test-PathWithin `
            (Resolve-PhysicalPath $CliToolPath) `
            (Resolve-PhysicalPath $stateWorkspace) `
            (Get-PathComparison $stateWorkspace))
    if ($CliToolPath) {
        Assert-PathsDoNotOverlap `
            $skillSourceFull 'Repository skill source' `
            $CliToolPath 'CliToolPath'
        Assert-PathsDoNotOverlap `
            $SkillDestination 'SkillDestination' `
            $CliToolPath 'CliToolPath'
        Assert-PathsDoNotOverlap `
            $McpConfigPath 'McpConfigPath' `
            $CliToolPath 'CliToolPath'
        foreach ($reservedPath in ([ordered] @{
                'Workspace marker' = $workspaceMarker
                'Skill backup' = $skillBackup
                'CLI backup' = $cliBackup
                'Package directory' = $packageDirectory
            }).GetEnumerator()) {
            Assert-PathsDoNotOverlap `
                $CliToolPath 'CliToolPath' `
                ([string] $reservedPath.Value) ([string] $reservedPath.Key)
        }
        if (-not $cliOwnedByWorkspace) {
            Assert-PathsDoNotOverlap `
                $CliToolPath 'CliToolPath' `
                $localTestingRoot 'Shared local-testing root'
            Assert-PathsDoNotOverlap `
                $StatePath 'StatePath' `
                $CliToolPath 'CliToolPath'
            Assert-PathsDoNotOverlap `
                $stateWorkspace 'Local-testing workspace' `
                $CliToolPath 'CliToolPath'
            Assert-PathsDoNotOverlap `
                $CliToolPath 'CliToolPath' `
                $resourceOwnersRoot 'Resource ownership registry'
        }
    }

    [string] $stateDirectory = Split-Path -Parent $StatePath
    [string] $mcpDll = Join-Path $root "src/Filtrace.Mcp/bin/$Configuration/net10.0/Filtrace.Mcp.dll"

    if ($null -ne $state -and $state.status -cne 'cleanup-in-progress' -and (
            ($state.schemaVersion -ge 3 -and
                -not (Test-PathsEqual ([string] $state.targetRepository) $TargetRepository)) -or
            -not (Test-PathsEqual ([string] $state.mcp.path) $McpConfigPath) -or
            -not (Test-PathsEqual ([string] $state.skill.destination) $SkillDestination) -or
            -not (Test-PathsEqual ([string] $state.cliToolPath) ([string] $CliToolPath)) -or
            [bool] $state.cliManaged -ne $cliManaged)) {
        throw "Existing local-testing state does not match this invocation: '$StatePath'."
    }

    [string[]] $currentResourceKeys = Get-ResourceKeys `
        $McpConfigPath `
        $SkillDestination `
        $cliManaged `
        $CliToolPath `
        $StatePath `
        $stateWorkspace
    [string[]] $resourceKeys = if ($null -ne $state -and $state.schemaVersion -in @(6, 7)) {
        [string[]] $state.resourceKeys
    }
    elseif ($null -ne $state -and $state.schemaVersion -eq 5) {
        [System.Collections.Generic.SortedSet[string]] $migratedKeys =
            [System.Collections.Generic.SortedSet[string]]::new(
                [string[]] $state.resourceKeys,
                [System.StringComparer]::Ordinal)
        [void] $migratedKeys.Add("path:$(Get-PathIdentity $StatePath)")
        [void] $migratedKeys.Add("path:$(Get-PathIdentity $stateWorkspace)")
        [string[]] $migratedKeys
    }
    else {
        $currentResourceKeys
    }
    if ($null -ne $state -and $state.schemaVersion -in @(6, 7) -and
        -not (Test-ResourceKeysEqual $resourceKeys $currentResourceKeys)) {
        throw "Existing local-testing state's resolved resource paths no longer match its recorded ownership. Restore the original path targets before retrying: '$StatePath'."
    }
    [System.IO.FileStream[]] $resourceLocks = Enter-ResourceLocks $resourceKeys
    [bool] $statePersisted = $null -ne $state
    [bool] $ownershipClaimed = $false
    [bool] $workspaceInitialized = $false
    try {
        [string[]] $lockedResourceKeys = Get-ResourceKeys `
            $McpConfigPath `
            $SkillDestination `
            $cliManaged `
            $CliToolPath `
            $StatePath `
            $stateWorkspace
        if (-not (Test-ResourceKeysEqual $resourceKeys $lockedResourceKeys)) {
            throw "Local-testing resource paths changed while acquiring locks. Restore the original path targets before retrying: '$StatePath'."
        }
        if ($null -ne $state -and $state.schemaVersion -eq 7 -and
            $state.status -cne 'cleanup-in-progress') {
            Assert-ResourceOwnership $resourceKeys $StatePath
        }
        elseif ($null -ne $state -and $state.schemaVersion -ne 7) {
            $null = Claim-ResourceOwnership $resourceKeys $StatePath
            $ownershipClaimed = $true
        }
        if ($null -ne $state -and $state.status -ceq 'cleanup-in-progress') {
            if (Test-Path -LiteralPath $stateWorkspace -PathType Container) {
                [string] $cleanupMarker = Get-StateWorkspaceMarkerPath $stateWorkspace
                if (Test-Path -LiteralPath $cleanupMarker -PathType Leaf) {
                    Remove-StateWorkspace $stateWorkspace $StatePath
                }
                elseif (@(Get-ChildItem -LiteralPath $stateWorkspace -Force).Count -eq 0) {
                    Remove-Item -LiteralPath $stateWorkspace -Force
                }
                else {
                    throw "Cleanup workspace is nonempty but its ownership marker is missing: '$stateWorkspace'."
                }
            }
            if ($state.schemaVersion -in @(5, 6, 7) -or $ownershipClaimed) {
                Remove-ResourceOwnership $resourceKeys $StatePath
                $ownershipClaimed = $false
            }
            Remove-Item -LiteralPath $StatePath -Force
            Write-Host "Filtrace local-mode cleanup completed for '$TargetRepository'."
            return
        }

        if ($null -ne $state -and $normalizedAction -ceq 'Install' -and
        $state.status -ceq 'restore-in-progress') {
        throw "Restore is already in progress for '$StatePath'. Run -Action Restore."
    }

        if ($normalizedAction -ceq 'Install') {
        if ($null -eq $state) {
            $null = Claim-ResourceOwnership $resourceKeys $StatePath
            $ownershipClaimed = $true
            Initialize-StateWorkspace $stateWorkspace $StatePath
            $workspaceInitialized = $true
        }

        if (-not $SkipBuild) {
            Invoke-Dotnet -Arguments @(
                'build', $solution,
                '--configuration', $Configuration)
            if (Test-Path -LiteralPath $packageDirectory) {
                Remove-Item -LiteralPath $packageDirectory -Recurse -Force
            }
            $null = New-Item -ItemType Directory -Path $packageDirectory -Force
            Invoke-Dotnet -Arguments @(
                'pack', $solution,
                '--configuration', $Configuration,
                '--no-build',
                '--output', $packageDirectory)
        }

        if (-not (Test-Path -LiteralPath $mcpDll -PathType Leaf)) {
            throw "Local MCP binary was not found: '$mcpDll'. Run without -SkipBuild."
        }
        if (-not $SkipValidation) {
            Push-Location $root
            try {
                & (Join-Path $root 'tools/Test-McpServer.ps1') -Configuration $Configuration
                if ($LASTEXITCODE -ne 0) {
                    throw "Local MCP validation exited with code $LASTEXITCODE."
                }
            }
            finally {
                Pop-Location
            }
        }

        [object] $localPackage = if ($cliManaged) {
            Get-LocalCliPackage $packageDirectory
        }
        else {
            $null
        }

        if ($null -eq $state) {
            if (Test-Path -LiteralPath $skillBackup) {
                Remove-Item -LiteralPath $skillBackup -Recurse -Force
            }

            [bool] $mcpFileExisted = Test-Path -LiteralPath $McpConfigPath -PathType Leaf
            [string] $mcpExistingAncestor = Get-NearestExistingAncestor $McpConfigPath
            [string] $skillExistingAncestor = Get-NearestExistingAncestor $SkillDestination
            [object] $mcpConfig = Get-McpConfig $McpConfigPath
            [object] $servers = (Get-Property $mcpConfig 'servers').Value
            [System.Management.Automation.PSPropertyInfo] $priorServer = Get-Property $servers 'filtrace'
            if (Test-Path -LiteralPath $SkillDestination -PathType Leaf) {
                throw "Skill destination is a file, not a directory: '$SkillDestination'."
            }
            [bool] $priorSkillExists = Test-Path -LiteralPath $SkillDestination -PathType Container
            [string] $skillBackupSha256 = $null
            if ($priorSkillExists) {
                Assert-DirectoryContainsNoLinks $SkillDestination 'Skill destination'
                Copy-Item -LiteralPath $SkillDestination -Destination $skillBackup -Recurse -Force
                $skillBackupSha256 = Get-DirectoryFingerprint `
                    $skillBackup `
                    'Recorded skill backup'
            }

            [object] $priorCli = if ($cliManaged) { Get-CliState } else { $null }
            if ($cliOwnedByWorkspace -and [bool] $priorCli.installed) {
                throw 'An existing CLI cannot be preserved inside the manifest-owned workspace.'
            }
            if ($cliManaged) {
                Backup-CliPackage $priorCli $cliBackup
            }
            $state = [pscustomobject] [ordered] @{
                schemaVersion = 7
                createdUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
                status = 'baseline-recorded'
                targetRepository = $TargetRepository
                workspace = $stateWorkspace
                cliManaged = $cliManaged
                cliToolPath = $CliToolPath
                cli = $priorCli
                resourceKeys = $resourceKeys
                mcp = [pscustomobject] [ordered] @{
                    path = $McpConfigPath
                    fileExisted = $mcpFileExisted
                    existingAncestor = $mcpExistingAncestor
                    serverExisted = $null -ne $priorServer
                    server = if ($null -eq $priorServer) { $null } else { $priorServer.Value }
                }
                skill = [pscustomobject] [ordered] @{
                    destination = $SkillDestination
                    existingAncestor = $skillExistingAncestor
                    existed = $priorSkillExists
                    backupSha256 = $skillBackupSha256
                }
            }
            Write-JsonFile $StatePath $state
            $statePersisted = $true
        }

        if ($cliManaged) {
            Remove-CliIfInstalled
            Invoke-ToolInstall $packageDirectory ([string] $localPackage.version) $stateWorkspace
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

        Write-Host "Filtrace local mode is active for '$TargetRepository' ($Configuration)."
        if ($cliManaged) {
            [string] $cliExecutable = if ($CliToolPath) {
                Join-Path $CliToolPath $(if (
                    [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
                    'filtrace.exe'
                }
                else {
                    'filtrace'
                })
            }
            else {
                'filtrace (global legacy setup)'
            }
            Write-Host "  CLI: $cliExecutable"
        }
        Write-Host "  MCP: $McpConfigPath -> $mcpDll"
        Write-Host "  Skill: $SkillDestination"
        Write-Host "  Restore from target repository: $PSScriptRoot/Use-LocalFiltrace.ps1 -Action Restore"
            return
        }

        if ($null -eq $state) {
        throw "No local-testing state was found at '$StatePath'. Nothing can be restored automatically."
    }
        if ([bool] $state.skill.existed -and
        -not (Test-Path -LiteralPath $skillBackup -PathType Container)) {
        throw "Recorded skill backup is missing: '$skillBackup'."
    }
        if ([bool] $state.skill.existed -and $state.schemaVersion -in @(4, 5, 6, 7)) {
        [string] $expectedSkillBackupSha256 = [string] $state.skill.backupSha256
        if ([string]::IsNullOrWhiteSpace($expectedSkillBackupSha256)) {
            throw "Recorded skill backup hash is missing: '$skillBackup'."
        }
        [string] $actualSkillBackupSha256 = Get-DirectoryFingerprint `
            $skillBackup `
            'Recorded skill backup'
        if ($actualSkillBackupSha256 -cne $expectedSkillBackupSha256) {
            throw "Recorded skill backup hash changed: '$skillBackup'."
        }
    }
        if ($cliManaged) {
        Assert-CliPackage $state.cli
    }

        $state.status = 'restore-in-progress'
        Write-JsonFile $StatePath $state

        if ($cliManaged) {
        Remove-CliIfInstalled
        if ([bool] $state.cli.installed) {
            [string] $backupPackageDirectory = Split-Path -Parent ([string] $state.cli.backupPackage)
            Invoke-ToolInstall $backupPackageDirectory ([string] $state.cli.version) $stateWorkspace
            [object] $restoredPackage = Get-InstalledCliPackage ([string] $state.cli.version)
            if ((Get-FileHash -LiteralPath $restoredPackage.path -Algorithm SHA256).Hash -cne
                [string] $state.cli.backupSha256) {
                throw 'The restored CLI package bytes do not match the recorded baseline package.'
            }
        }
    }

        Set-McpServer $McpConfigPath ([bool] $state.mcp.serverExisted) $state.mcp.server
    if ($state.schemaVersion -ge 3 -and -not [bool] $state.mcp.fileExisted -and
        (Remove-EmptyMcpConfig $McpConfigPath)) {
        Remove-EmptyCreatedAncestors `
            $McpConfigPath `
            ([string] $state.mcp.existingAncestor) `
            $pathComparison
    }

        [byte[]] $currentOverlay = Get-OverlayBytes $SkillDestination
    if ([bool] $state.skill.existed) {
        Copy-Skill $skillBackup $SkillDestination $currentOverlay
    }
    elseif (Test-Path -LiteralPath $SkillDestination) {
        if ($null -ne $currentOverlay) {
            [string] $retainedOverlay = Write-RetainedOverlay $StatePath $currentOverlay
            Write-Warning "The pre-local setup had no skill. The current overlay was retained at '$retainedOverlay'."
        }
        Remove-Item -LiteralPath $SkillDestination -Recurse -Force
    }
    if ($state.schemaVersion -ge 3 -and -not [bool] $state.skill.existed) {
        Remove-EmptyCreatedAncestors `
            $SkillDestination `
            ([string] $state.skill.existingAncestor) `
            $pathComparison
    }

        if ($legacyState) {
        if (Test-PathsEqual $StatePath $legacyStatePath) {
            Remove-Item -LiteralPath $skillBackup -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $cliBackup -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $packageDirectory -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath (Join-Path $stateDirectory 'local.nuget.config') -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath (Join-Path $stateDirectory 'restore.nuget.config') -Force -ErrorAction SilentlyContinue
        }
        else {
            if ([bool] $state.skill.existed) {
                Remove-Item -LiteralPath $skillBackup -Recurse -Force -ErrorAction SilentlyContinue
            }
            if ($cliManaged -and [bool] $state.cli.installed) {
                Remove-Item -LiteralPath $cliBackup -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        if ($ownershipClaimed) {
            Remove-ResourceOwnership $resourceKeys $StatePath
            $ownershipClaimed = $false
        }
        Remove-Item -LiteralPath $StatePath -Force
    }
        else {
            $state.status = 'cleanup-in-progress'
            Write-JsonFile $StatePath $state
            Remove-StateWorkspace $stateWorkspace $StatePath
            if ($state.schemaVersion -in @(5, 6, 7) -or $ownershipClaimed) {
                Remove-ResourceOwnership $resourceKeys $StatePath
                $ownershipClaimed = $false
            }
            Remove-Item -LiteralPath $StatePath -Force
        }

        Write-Host "Filtrace local mode was removed from '$TargetRepository' and the recorded setup was restored."
    }
    catch {
        # Existing manifests retain ownership after failure so only their retry can continue.
        if (-not $statePersisted) {
            if ($ownershipClaimed) {
                Remove-ResourceOwnership $resourceKeys $StatePath
            }
            if ($workspaceInitialized -and
                (Test-Path -LiteralPath $stateWorkspace -PathType Container)) {
                Remove-StateWorkspace $stateWorkspace $StatePath
            }
        }
        throw
    }
    finally {
        foreach ($resourceLock in $resourceLocks) { $resourceLock.Dispose() }
    }
}
finally {
    $stateLock.Dispose()
}
