#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Validate filtrace's local and vendored agent skills.

.DESCRIPTION
  Runs the v0.10.0 bundled validator over every skill, applies strict portfolio
  policy to the provenance-bearing commons cores, checks their immutable pin and
  overlays, validates the local tool-shipped filtrace metadata, and resolves every
  relative Markdown link under .agents.

  -VerifyUpstream installs each commons core into an isolated temporary repository
  at the expected pin and compares every core file byte-for-byte (overlay.md is the
  only allowed local addition).

  -ReferenceValidation also runs the pinned agentskills.io reference validator.
#>
[CmdletBinding()]
param(
    [string]$ExpectedPin = 'v0.10.0',
    [switch]$VerifyUpstream,
    [switch]$ReferenceValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$skillsRoot = Join-Path $root '.agents/skills'
$validator = Join-Path $skillsRoot 'manage-skills/scripts/Validate-Skills.ps1'
$expectedCommons = @(
    'address-pr-feedback',
    'agent-files-review',
    'create-pr',
    'manage-skills',
    'performance-testing',
    'pre-pr-self-review',
    'security-review'
)
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$message) {
    $failures.Add($message)
}

function Get-Metadata([string]$skillPath) {
    [string[]] $lines = Get-Content -LiteralPath $skillPath
    [bool] $insideFrontmatter = $false
    [bool] $insideMetadata = $false
    [System.Collections.Specialized.OrderedDictionary] $metadata = [ordered]@{}

    foreach ($line in $lines) {
        if ($line -eq '---') {
            if (-not $insideFrontmatter) {
                $insideFrontmatter = $true
                continue
            }

            break
        }

        if (-not $insideFrontmatter) { continue }
        if ($line -eq 'metadata:') {
            $insideMetadata = $true
            continue
        }

        if ($insideMetadata -and $line -match '^\s+([a-z0-9-]+):\s*(.+?)\s*$') {
            [string] $value = $Matches[2]
            if (($value.StartsWith("'") -and $value.EndsWith("'")) -or
                ($value.StartsWith('"') -and $value.EndsWith('"'))) {
                $value = $value[1..($value.Length - 2)] -join ''
            }

            $metadata[$Matches[1]] = $value
        }
        elseif ($insideMetadata -and $line -match '^\S') {
            $insideMetadata = $false
        }
    }

    return $metadata
}

function Invoke-SkillValidator([string]$skillDirectory, [switch]$Strict) {
    [string[]] $arguments = @('-NoProfile', '-File', $validator, $skillDirectory)
    if ($Strict) { $arguments += '-RequirePortfolioMetadata' }

    & pwsh @arguments
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "Skill validator failed for '$skillDirectory'."
    }
}

function Test-PortfolioValue(
    [System.Collections.IDictionary]$metadata,
    [string]$skillName,
    [string]$key,
    [string]$expected) {
    [string] $actual = if ($metadata.Contains($key)) { [string]$metadata[$key] } else { '' }
    if ($actual -cne $expected) {
        Add-Failure "$skillName metadata.$key is '$actual'; expected '$expected'."
    }
}

function Test-Overlay([string]$skillName) {
    [string] $overlay = Join-Path $skillsRoot "$skillName/overlay.md"
    if (-not (Test-Path -LiteralPath $overlay -PathType Leaf)) {
        Add-Failure "$skillName is missing overlay.md."
        return
    }

    [string] $text = Get-Content -LiteralPath $overlay -Raw
    if ($text -notmatch "(?m)^core:\s*$([regex]::Escape($skillName))\s*$") {
        Add-Failure "$skillName overlay core does not match the skill name."
    }
    if ($text -notmatch "(?m)^core-pin:\s*$([regex]::Escape($ExpectedPin))\s*$") {
        Add-Failure "$skillName overlay core-pin is not '$ExpectedPin'."
    }
}

function Test-Links([string]$directory) {
    foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.md') {
        [bool] $insideFence = $false
        [int] $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            $lineNumber++
            if ($line -match '^\s*(```|~~~)') {
                $insideFence = -not $insideFence
                continue
            }
            if ($insideFence) { continue }

            [string] $withoutInlineCode = [regex]::Replace($line, '`[^`]*`', '')
            foreach ($match in [regex]::Matches($withoutInlineCode, '\[[^\]]*\]\(([^)]+)\)')) {
                [string] $target = $match.Groups[1].Value.Trim()
                if ($target -match '^(?:[a-z][a-z0-9+.-]+:|//|#)') { continue }
                [string] $pathPart = ($target -split '#', 2)[0]
                if ([string]::IsNullOrEmpty($pathPart)) { continue }
                if ([System.IO.Path]::IsPathRooted($pathPart)) {
                    Add-Failure "$([System.IO.Path]::GetRelativePath($root, $file.FullName)):$lineNumber has rooted link '$target'."
                    continue
                }

                [string] $resolved = [System.IO.Path]::GetFullPath((Join-Path $file.DirectoryName $pathPart))
                if (-not (Test-Path -LiteralPath $resolved)) {
                    Add-Failure "$([System.IO.Path]::GetRelativePath($root, $file.FullName)):$lineNumber has broken link '$target'."
                }
            }
        }
    }
}

function Test-UpstreamMirror {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Add-Failure "Cannot verify upstream skill mirrors: 'gh' is not available."
        return
    }

    [string] $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace-skills-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    try {
        Push-Location $temporaryRoot
        try {
            git init -q
            foreach ($skillName in $expectedCommons) {
                gh skill install JeremyKuhne/agent-skills "skills/$skillName" --pin $ExpectedPin --agent github-copilot --scope project
                if ($LASTEXITCODE -ne 0) {
                    Add-Failure "Failed to install upstream $skillName at $ExpectedPin."
                    continue
                }
            }
        }
        finally {
            Pop-Location
        }

        foreach ($skillName in $expectedCommons) {
            [string] $localDirectory = Join-Path $skillsRoot $skillName
            [string] $upstreamDirectory = Join-Path $temporaryRoot ".agents/skills/$skillName"
            if (-not (Test-Path -LiteralPath $upstreamDirectory)) { continue }

            [string[]] $localFiles = @(
                Get-ChildItem -LiteralPath $localDirectory -Recurse -File |
                    Where-Object { $_.FullName -cne (Join-Path $localDirectory 'overlay.md') } |
                    ForEach-Object { [System.IO.Path]::GetRelativePath($localDirectory, $_.FullName) } |
                    Sort-Object)
            [string[]] $upstreamFiles = @(
                Get-ChildItem -LiteralPath $upstreamDirectory -Recurse -File |
                    ForEach-Object { [System.IO.Path]::GetRelativePath($upstreamDirectory, $_.FullName) } |
                    Sort-Object)

            if (Compare-Object $localFiles $upstreamFiles) {
                Add-Failure "$skillName vendored file list differs from $ExpectedPin."
                continue
            }

            foreach ($relativePath in $upstreamFiles) {
                [string] $localHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $localDirectory $relativePath)).Hash
                [string] $upstreamHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $upstreamDirectory $relativePath)).Hash
                if ($localHash -cne $upstreamHash) {
                    Add-Failure "$skillName/$relativePath differs from the $ExpectedPin artifact."
                }
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
    throw "Vendored validator not found: $validator"
}

[string[]] $skillPaths = @(
    Get-ChildItem -LiteralPath $skillsRoot -Directory |
        Sort-Object Name |
        ForEach-Object FullName)
[string[]] $expectedSkillNames = @('filtrace') + $expectedCommons
foreach ($skillPath in $skillPaths) {
    [string] $skillName = Split-Path -Leaf $skillPath
    if ($expectedSkillNames -cnotcontains $skillName) {
        Add-Failure "Unexpected skill directory '$skillName'. Add it to the reviewed portfolio or remove it."
    }

    Invoke-SkillValidator $skillPath
}

foreach ($skillName in $expectedCommons) {
    [string] $skillDirectory = Join-Path $skillsRoot $skillName
    [string] $skillPath = Join-Path $skillDirectory 'SKILL.md'
    if (-not (Test-Path -LiteralPath $skillPath -PathType Leaf)) {
        Add-Failure "Expected commons skill '$skillName' is missing."
        continue
    }

    Invoke-SkillValidator $skillDirectory -Strict
    [System.Collections.IDictionary] $metadata = Get-Metadata $skillPath
    Test-PortfolioValue $metadata $skillName 'github-path' "skills/$skillName"
    Test-PortfolioValue $metadata $skillName 'github-pinned' $ExpectedPin
    Test-PortfolioValue $metadata $skillName 'github-ref' "refs/tags/$ExpectedPin"
    Test-PortfolioValue $metadata $skillName 'github-repo' 'https://github.com/JeremyKuhne/agent-skills'
    [string] $treeSha = if ($metadata.Contains('github-tree-sha')) { [string]$metadata['github-tree-sha'] } else { '' }
    if ($treeSha -cnotmatch '^[0-9a-f]{40}$') {
        Add-Failure "$skillName metadata.github-tree-sha is missing or invalid."
    }
    Test-Overlay $skillName
}

[System.Collections.IDictionary] $filtraceMetadata = Get-Metadata (Join-Path $skillsRoot 'filtrace/SKILL.md')
Test-PortfolioValue $filtraceMetadata 'filtrace' 'portability' 'repo-specific'
Test-PortfolioValue $filtraceMetadata 'filtrace' 'applicability' 'tool-shipped'
Test-PortfolioValue $filtraceMetadata 'filtrace' 'binding' 'none'
Test-PortfolioValue $filtraceMetadata 'filtrace' 'risk' 'local-write'
Test-PortfolioValue $filtraceMetadata 'filtrace' 'maturity' 'stable'
Test-PortfolioValue $filtraceMetadata 'filtrace' 'requires' 'none'
Test-PortfolioValue $filtraceMetadata 'filtrace' 'related' 'performance-testing'

Test-Links (Join-Path $root '.agents')

if ($ReferenceValidation) {
    if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
        Add-Failure "Cannot run reference validation: 'npx' is not available."
    }
    else {
        foreach ($skillPath in $skillPaths) {
            & npx --yes skills-ref@0.1.5 validate $skillPath
            if ($LASTEXITCODE -ne 0) {
                Add-Failure "skills-ref validation failed for '$(Split-Path -Leaf $skillPath)'."
            }
        }
    }
}

if ($VerifyUpstream) { Test-UpstreamMirror }

if ($failures.Count -gt 0) {
    Write-Host "Agent skill validation failed with $($failures.Count) issue(s):" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host "Agent skill validation passed ($($skillPaths.Count) skills; $($expectedCommons.Count) commons cores pinned to $ExpectedPin)." -ForegroundColor Green
exit 0