#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Lints the filtrace CLI help surface as a build artifact.

.DESCRIPTION
  Enforces the M2 help contract (docs/filtrace-implementation-plan.md, milestone M2):

    1. Every [Command] verb in the CLI is listed in the top-level help.
    2. Each verb's `--help` succeeds, shows a Usage line, and stays within the
       per-verb line budget (so help never grows into an unscannable wall).
    3. The README documents every verb with a runnable example and carries the
       canonical workflow - examples live in the README because ConsoleAppFramework
       generates the per-verb `--help` from XML docs and has no examples section.

  Run from the filtrace subtree root (the directory holding filtrace.slnx).

.PARAMETER Configuration
  The build configuration whose CLI binary to lint. Defaults to Release.

.PARAMETER MaxVerbHelpLines
  The per-verb `--help` line budget. Defaults to 60.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [int]$MaxVerbHelpLines = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$commandsFile = Join-Path $root 'src/Filtrace/Cli/TraceCommands.cs'
$readmeFile = Join-Path $root 'README.md'
$cliDll = Join-Path $root "src/Filtrace/bin/$Configuration/net10.0/filtrace.dll"

$failures = [System.Collections.Generic.List[string]]::new()
function Add-Failure([string]$message) { $failures.Add($message) }

if (-not (Test-Path $cliDll)) {
    throw "CLI binary not found at '$cliDll'. Build the solution first (dotnet build filtrace.slnx -c $Configuration)."
}

# The verb set is the source of truth: every [Command("name")] in TraceCommands.
# @(...) forces an array so a single-verb surface does not collapse to a string
# (which would make foreach iterate characters).
$verbs = @(Select-String -Path $commandsFile -Pattern '\[Command\("([^"]+)"\)\]' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($verbs.Count -eq 0) { throw "No [Command(...)] verbs found in $commandsFile." }
Write-Host "Linting help for $($verbs.Count) verbs: $($verbs -join ', ')"

# 1. Top-level help lists every verb. If the CLI itself fails to run, fail with a
# focused message rather than letting every verb check cascade into noise.
$topHelp = (& dotnet $cliDll 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Top-level help ('dotnet filtrace.dll') exited with code $LASTEXITCODE.`n$topHelp"
}
foreach ($verb in $verbs) {
    if ($topHelp -notmatch "(?m)^\s+$([regex]::Escape($verb))\s") {
        Add-Failure "Top-level help does not list the '$verb' verb."
    }
}

$scopeVerbs = [ordered]@{
    process = [System.Collections.Generic.List[string]]::new()
    root = [System.Collections.Generic.List[string]]::new()
    benchmark = [System.Collections.Generic.List[string]]::new()
}

# 2. Per-verb help: succeeds, has a Usage line, stays within the line budget, and
# records the implemented scope surface for the documentation inventory check.
foreach ($verb in $verbs) {
    $verbHelp = (& dotnet $cliDll $verb --help 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "'$verb --help' exited with code $LASTEXITCODE."
    }
    if ($verbHelp -notmatch '(?m)^Usage:') {
        Add-Failure "'$verb --help' has no Usage: line."
    }
    # Out-String appends a trailing newline; trim it so the count reflects the
    # actually rendered lines rather than overcounting by one.
    $lineCount = ($verbHelp.TrimEnd("`r", "`n") -split "`n").Count
    if ($lineCount -gt $MaxVerbHelpLines) {
        Add-Failure "'$verb --help' is $lineCount lines (budget $MaxVerbHelpLines)."
    }
    foreach ($scope in $scopeVerbs.Keys) {
        if ($verbHelp -match "(?m)(?:^|\s)--$scope(?:\s|,|$)") {
            $scopeVerbs[$scope].Add($verb)
        }
    }
}

# 3. README documents every verb with a runnable example and carries the workflow.
$readme = Get-Content $readmeFile -Raw
if ($readme -notmatch '(?im)workflow') {
    Add-Failure "README has no 'Workflow' section."
}
foreach ($verb in $verbs) {
    # A documented example is a `filtrace <verb> ...` invocation somewhere in the README.
    if ($readme -notmatch "filtrace $([regex]::Escape($verb))(\s|``)") {
        Add-Failure "README has no 'filtrace $verb' example."
    }
}

$scopeBlockMatch = [regex]::Match(
    $readme,
    '(?s)<!-- filtrace:begin scopes -->\r?\n(.*?)\r?\n<!-- filtrace:end scopes -->')
if (-not $scopeBlockMatch.Success) {
    Add-Failure "README has no synchronized 'scopes' block."
}
else {
    $scopeBlock = $scopeBlockMatch.Groups[1].Value
    $scopeSections = [ordered]@{
        process = [regex]::Match(
            $scopeBlock,
            '(?s)- \*\*Named process:\*\*(.*?)(?=\r?\n- \*\*Root subtree:\*\*)').Groups[1].Value
        root = [regex]::Match(
            $scopeBlock,
            '(?s)- \*\*Root subtree:\*\*(.*?)(?=\r?\n- \*\*BenchmarkDotNet workload:\*\*)').Groups[1].Value
        benchmark = [regex]::Match(
            $scopeBlock,
            '(?s)- \*\*BenchmarkDotNet workload:\*\*(.*)$').Groups[1].Value
    }
    foreach ($scope in $scopeVerbs.Keys) {
        if ([string]::IsNullOrWhiteSpace($scopeSections[$scope])) {
            Add-Failure "README scope inventory has no '$scope' section."
            continue
        }
        foreach ($verb in $scopeVerbs[$scope]) {
            $token = '`' + $verb + '`'
            if (-not $scopeSections[$scope].Contains($token, [StringComparison]::Ordinal)) {
                Add-Failure "CLI verb '$verb' implements --$scope but is absent from the scope inventory."
            }
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Help lint FAILED with $($failures.Count) issue(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host 'Help lint passed.' -ForegroundColor Green
exit 0
