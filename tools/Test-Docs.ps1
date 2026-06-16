#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Drift check for the single-sourced knowledge layer (docs/ -> skill, README).

.DESCRIPTION
  Enforces the M4 knowledge-layer contract (docs/implementation-plan.md, milestone M4):

    1. docs/ is the single source of truth. The marked blocks
       (`<!-- filtrace:begin <id> -->` ... `<!-- filtrace:end <id> -->`) listed in the
       sync map below are embedded verbatim into their consumer surfaces (the
       shipped skill, the README); this check fails if any copy drifts from its
       source (line endings are normalized, so it is OS-agnostic). Blocks not in
       the map (e.g. `tools`) are reference-only and need no consumer copy.
    2. The shipped skill's YAML frontmatter is valid: `name` matches the skill
       directory, and `description` is present.
    3. Every CLI verb appears in the verb catalog, and every MCP tool appears in
       the tool catalog - so a newly added verb/tool cannot ship undocumented.
    4. Every relative link in a shipped skill file (.agents/skills/filtrace/)
       resolves to a path inside the skill directory - so no link dangles once
       the skill is packed into the NuGet package or vendored via
       `gh skill install`, both of which carry only the skill directory (issue #10).

  Run from the filtrace subtree root (the directory holding filtrace.slnx).

.PARAMETER Fix
  Rewrite each drifted consumer block from its docs/ source instead of failing.
  Use after editing a block in docs/ to refresh every embedded copy.
#>
[CmdletBinding()]
param(
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$failures = [System.Collections.Generic.List[string]]::new()
function Add-Failure([string]$message) { $failures.Add($message) }

# The block sync map: each marked block has one source-of-truth page in docs/ and
# the consumer surfaces that embed a verbatim copy.
$blocks = @(
    @{ Id = 'verbs'; Source = 'docs/workflow.md'; Consumers = @('.agents/skills/filtrace/SKILL.md') }
    @{ Id = 'traps'; Source = 'docs/traps.md'; Consumers = @('.agents/skills/filtrace/SKILL.md') }
    @{ Id = 'agents-snippet'; Source = 'docs/workflow.md'; Consumers = @('README.md') }
)

# Return the inner text of a marked block (newlines normalized to LF), or $null if
# the block is absent.
function Get-DocBlock {
    param([string]$Path, [string]$Id)
    if (-not (Test-Path $Path)) { return $null }
    $text = (Get-Content -LiteralPath $Path -Raw) -replace "`r`n", "`n"
    $escaped = [regex]::Escape($Id)
    $pattern = "(?s)<!-- filtrace:begin $escaped -->\n(.*?)\n[ \t]*<!-- filtrace:end $escaped -->"
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

# Replace the inner text of a marked block in a file, preserving the file's
# newline style (CRLF if the file contains any CRLF, otherwise LF).
function Set-DocBlock {
    param([string]$Path, [string]$Id, [string]$Content)
    $raw = Get-Content -LiteralPath $Path -Raw
    $newline = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
    $body = ($Content -replace "`r`n", "`n") -replace "`n", $newline
    $escaped = [regex]::Escape($Id)
    $pattern = "(?s)(<!-- filtrace:begin $escaped -->\r?\n).*?(\r?\n[ \t]*<!-- filtrace:end $escaped -->)"
    $replaced = [regex]::Replace($raw, $pattern, { param($m) $m.Groups[1].Value + $body + $m.Groups[2].Value })
    Set-Content -LiteralPath $Path -Value $replaced -NoNewline
}

# 1. Block drift (or -Fix).
foreach ($block in $blocks) {
    $sourcePath = Join-Path $root $block.Source
    $sourceText = Get-DocBlock -Path $sourcePath -Id $block.Id
    if ($null -eq $sourceText) {
        Add-Failure "Source block '$($block.Id)' not found in $($block.Source)."
        continue
    }
    foreach ($consumer in $block.Consumers) {
        $consumerPath = Join-Path $root $consumer
        $consumerText = Get-DocBlock -Path $consumerPath -Id $block.Id
        if ($null -eq $consumerText) {
            Add-Failure "Consumer block '$($block.Id)' not found in $consumer."
            continue
        }
        if ($consumerText -ne $sourceText) {
            if ($Fix) {
                Set-DocBlock -Path $consumerPath -Id $block.Id -Content $sourceText
                Write-Host "Fixed: '$($block.Id)' in $consumer refreshed from $($block.Source)."
            }
            else {
                Add-Failure "Block '$($block.Id)' in $consumer has drifted from $($block.Source). Run tools/Test-Docs.ps1 -Fix."
            }
        }
    }
}

# 2. Skill frontmatter: name matches the directory, description present.
$skillPath = Join-Path $root '.agents/skills/filtrace/SKILL.md'
if (-not (Test-Path $skillPath)) {
    Add-Failure 'Shipped skill .agents/skills/filtrace/SKILL.md is missing.'
}
else {
    $skillRaw = Get-Content -LiteralPath $skillPath -Raw
    if ($skillRaw -notmatch "(?s)^---\r?\n(.*?)\r?\n---") {
        Add-Failure '.agents/skills/filtrace/SKILL.md has no YAML frontmatter block.'
    }
    else {
        $frontmatter = $Matches[1]
        $skillName = if ($frontmatter -match '(?m)^name:\s*(\S+)\s*$') { $Matches[1] } else { $null }
        if ($skillName -ne 'filtrace') {
            Add-Failure "Skill 'name' is '$skillName'; it must be 'filtrace' to match the .agents/skills/filtrace/ directory."
        }
        if ($frontmatter -notmatch '(?m)^description:\s*\S') {
            Add-Failure 'Skill frontmatter has no non-empty description.'
        }
    }
}

# 3. Verb / tool completeness: every CLI verb is in the verb catalog, every MCP
# tool is in the tool catalog.
$verbsBlock = Get-DocBlock -Path (Join-Path $root 'docs/workflow.md') -Id 'verbs'
$verbs = @(Select-String -Path (Join-Path $root 'src/Filtrace/Cli/TraceCommands.cs') -Pattern '\[Command\("([^"]+)"\)\]' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($verbs.Count -eq 0) { Add-Failure 'No [Command(...)] verbs found in TraceCommands.cs.' }
foreach ($verb in $verbs) {
    if ($null -eq $verbsBlock -or $verbsBlock -notmatch "(?m)\b$([regex]::Escape($verb))\b") {
        Add-Failure "Verb '$verb' is not documented in the 'verbs' block of docs/workflow.md."
    }
}

$toolsBlock = Get-DocBlock -Path (Join-Path $root 'docs/workflow.md') -Id 'tools'
$tools = @(Select-String -Path (Join-Path $root 'src/Filtrace.Mcp/TraceTools.cs') -Pattern 'Name = "(trace_[a-z_]+)"' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($tools.Count -eq 0) { Add-Failure 'No trace_* MCP tools found in TraceTools.cs.' }
foreach ($tool in $tools) {
    if ($null -eq $toolsBlock -or $toolsBlock -notmatch "(?m)\b$([regex]::Escape($tool))\b") {
        Add-Failure "Tool '$tool' is not documented in the 'tools' block of docs/workflow.md."
    }
}

# 4. Skill link integrity: every relative link in a shipped skill file must
# resolve to a path inside the skill directory. A link that escapes the directory
# (e.g. ../../../docs/workflow.md) dangles once the skill is packed into the NuGet
# package or vendored via `gh skill install`, both of which carry only the skill
# directory (issue #10). External links (scheme://, mailto:), protocol-relative
# links, and pure anchors are exempt; a scheme must be at least two characters so a
# Windows drive letter (e.g. C:\path) is treated as a path, not a URL scheme.
$skillDir = Join-Path $root '.agents/skills/filtrace'
$skillDirFull = [System.IO.Path]::GetFullPath($skillDir)
$linkCount = 0
if (Test-Path $skillDir) {
    $linkPattern = '\[[^\]]*\]\(([^)\s]+)\)'
    foreach ($file in Get-ChildItem -LiteralPath $skillDir -Recurse -Filter *.md -File) {
        $name = [System.IO.Path]::GetRelativePath($root, $file.FullName) -replace '\\', '/'
        $content = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in [regex]::Matches($content, $linkPattern)) {
            $target = $match.Groups[1].Value
            # Exempt URLs (scheme: with a 2+ char scheme), protocol-relative (//host),
            # and pure anchors (#frag).
            if ($target -match '^(?:[a-z][a-z0-9+.-]+:|//|#)') { continue }
            $linkCount++
            $relative = ($target -split '#', 2)[0]
            if ([string]::IsNullOrEmpty($relative)) { continue }
            # A rooted target (drive-letter or leading separator, e.g. C:\x or /x) is not
            # an in-directory relative link and will not travel with the skill.
            if ([System.IO.Path]::IsPathRooted($relative)) {
                Add-Failure "Skill file '$name' links to '$target', which is an absolute path that will not resolve when the skill is packaged or vendored (issue #10). Use an absolute https URL or a path inside the skill directory."
                continue
            }
            $resolved = [System.IO.Path]::GetFullPath((Join-Path $file.DirectoryName $relative))
            $fromSkill = [System.IO.Path]::GetRelativePath($skillDirFull, $resolved)
            # `..` escapes only as a whole path segment (.. or ..<sep>), not as a leading
            # substring of a real name such as `..hidden`.
            if ($fromSkill -match '^\.\.([\\/]|$)') {
                Add-Failure "Skill file '$name' links to '$target', which escapes the skill directory and will dangle when the skill is packaged or vendored (issue #10). Use an absolute https URL or a path inside the skill directory."
            }
            elseif (-not (Test-Path -LiteralPath $resolved)) {
                Add-Failure "Skill file '$name' links to '$target', which does not resolve to an existing path."
            }
        }
    }
}

Write-Host "Checked $($blocks.Count) shared block(s), $($verbs.Count) verb(s), $($tools.Count) tool(s), $linkCount skill link(s)."

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Docs drift check FAILED with $($failures.Count) issue(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host 'Docs drift check passed.' -ForegroundColor Green
exit 0
