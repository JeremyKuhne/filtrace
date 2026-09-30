#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Lints the filtrace CLI help surface as a build artifact.

.DESCRIPTION
  Enforces the CLI help contract (docs/design.md, "Measures of success"):

     1. Every [Command] verb is listed in top-level help; removed commands
        are rejected, including their help requests.
     2. Top-level help does not exceed the pre-VN4 line or character baseline.
     3. Each verb's `--help` succeeds, shows a Usage line, and stays within the
       per-verb line budget (so help never grows into an unscannable wall).
     4. The README documents every canonical verb with a runnable example, teaches
        no removed command, and carries the
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

# The command attributes are the source of truth; every registered verb is public.
# @(...) forces an array so a single-verb surface does not collapse to a string
# (which would make foreach iterate characters).
$commandsSource = Get-Content -LiteralPath $commandsFile -Raw
$verbs = @([regex]::Matches($commandsSource, '\[Command\("([^"]+)"\)\]') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$removedCommands = @(
    'alloc', 'clean', 'convert', 'cpu', 'diskio', 'exceptions', 'gcstats',
    'heatmap', 'jitstats', 'lines', 'threadpool', 'threadtime'
)
if ($commandsSource -match '\[Hidden\]') {
    Add-Failure 'The CLI registers a hidden command instead of a canonical verb.'
}
if ($verbs.Count -eq 0) { throw "No [Command(...)] verbs found in $commandsFile." }
Write-Host "Linting help for $($verbs.Count) canonical verbs: $($verbs -join ', ')"

# 1. Top-level help lists every canonical command. If the CLI itself fails to run, fail with a
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
foreach ($command in $removedCommands) {
    if ($verbs -contains $command -or $topHelp -match "(?m)^\s+$([regex]::Escape($command))\s") {
        Add-Failure "Removed command '$command' is still registered."
    }

    $rejectedHelp = (& dotnet $cliDll $command --help 2>&1 | Out-String)
    $rejectedExit = $LASTEXITCODE
    if ($rejectedExit -ne 1 -or $rejectedHelp -notmatch [regex]::Escape($command)) {
        Add-Failure "Removed command '$command --help' did not report a usage error."
    }
}

$topHelpLines = ($topHelp.TrimEnd("`r", "`n") -split "`n").Count
if ($topHelpLines -gt 27) {
    Add-Failure "Top-level help is $topHelpLines lines; the VN4 baseline is 27."
}
if ($topHelp.Length -gt 2171) {
    Add-Failure "Top-level help is $($topHelp.Length) characters; the VN4 baseline is 2171."
}

$scopeVerbs = [ordered]@{
    process = [System.Collections.Generic.List[string]]::new()
    root = [System.Collections.Generic.List[string]]::new()
    benchmark = [System.Collections.Generic.List[string]]::new()
}

# 3. Per-verb help: succeeds, has a Usage line, stays within the line budget, and
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

foreach ($cpuHelpVerb in @('rank', 'classify')) {
    $cpuHelp = (& dotnet $cliDll $cpuHelpVerb --help 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "'$cpuHelpVerb --help' failed while checking CPU weight terminology."
        continue
    }
    if ($cpuHelp -notmatch 'trace-recorded sample intervals' -or $cpuHelp -notmatch 'raw sample counts') {
        Add-Failure "'$cpuHelpVerb --help' does not explain interval-derived milliseconds and raw sample counts."
    }
    if ($cpuHelp -match '(?i)(?:each|every)\s+sample\s+(?:weighs|as)\s+1 ms|CPU self-time') {
        Add-Failure "'$cpuHelpVerb --help' still presents CPU weights as fixed 1 ms time."
    }
}

# 4. README documents every canonical verb with a runnable example and carries the workflow.
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
foreach ($command in $removedCommands) {
    if ($readme -match "filtrace $([regex]::Escape($command))(\s|``)") {
        Add-Failure "README teaches removed command 'filtrace $command' as a runnable command."
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
