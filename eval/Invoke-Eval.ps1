#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Deterministic eval gate for the filtrace tool surface (the free, no-LLM arm of M5).

.DESCRIPTION
  Each eval task is a question an agent might ask, a committed fixture, and the
  canonical tool sequence an ideal agent would run to answer it. This harness runs
  that sequence directly - no LLM - and checks the three things the M5 design (the
  implementation plan's §10 / M5) cares about:

    success - the tool produced the right answer: every step exits 0 and the task's
              assertions hold.
    calls   - the canonical path is within the call budget (design goal G1, <= 6
              tool calls).
    tokens  - the output an agent would consume stays within budget (design goal
              G2), tracked against a committed baseline with a growth tolerance (the
              design's "> 15% token growth on a task fails" regression budget). Token
              cost is the offline estimate from tools/Get-TokenEstimate.ps1.

  This is the deterministic regression gate: it guards the tool's
  fitness-for-agent-use for free, in CI, on every PR. It does not score an agent's
  reasoning - the live headless-agent arms (Copilot CLI / local models) that do that
  are a later M5 slice. A green gate here means "the canonical path still answers the
  task, within the call and token budgets"; it is the cheap regression net under the
  expensive arms.

  Tasks live in eval/tasks/*.json; baselines in eval/baselines.json. Run from the
  repo root (the directory holding filtrace.slnx).

.PARAMETER Configuration
  The build configuration whose CLI binary to drive. Defaults to Release.

.PARAMETER Update
  Regenerate eval/baselines.json from this run (calls + tokens per passing task)
  instead of comparing against it. Use after adding a task or a deliberate change to
  the tool's output, then commit the new baseline.

.PARAMETER TokenGrowthTolerance
  The allowed fractional token growth over baseline before a task fails. Defaults to
  0.15 (the design's 15% regression budget).
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Update,
    [double]$TokenGrowthTolerance = 0.15
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cliDll = Join-Path $root "src/Filtrace/bin/$Configuration/net10.0/filtrace.dll"
$tasksDir = Join-Path $PSScriptRoot 'tasks'
$baselinePath = Join-Path $PSScriptRoot 'baselines.json'

# G1: an agent investigation should reach its answer in a handful of tool calls.
$MaxCalls = 6

if (-not (Test-Path $cliDll)) {
    throw "CLI binary not found at '$cliDll'. Build the solution first (dotnet build filtrace.slnx -c $Configuration)."
}
if (-not (Test-Path $tasksDir)) {
    throw "No tasks directory at '$tasksDir'."
}

# The shared offline token estimator (also used by the MCP schema-budget check).
. (Join-Path $root 'tools/Get-TokenEstimate.ps1')

$onWindows = [System.OperatingSystem]::IsWindows()

# Walk a dotted path (with optional [index] segments) into a parsed-JSON object,
# e.g. "result.rows[0].frame". Returns $null if any segment is missing.
function Get-JsonField {
    param($Object, [string]$Path)
    $current = $Object
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        if ($part -match '^(.+)\[(\d+)\]$') {
            $current = $current.($Matches[1])
            if ($null -eq $current) { return $null }
            $current = $current[[int]$Matches[2]]
        }
        else {
            $current = $current.$part
        }
    }
    return $current
}

# Evaluate one assertion against a step's parsed JSON and raw text. Returns a
# (ok, message) pair.
function Test-Assertion {
    param($Assert, $Json, [string]$Raw)

    if ($null -ne $Assert.topFrame) {
        $actual = Get-JsonField -Object $Json -Path 'result.rows[0].frame'
        $ok = $actual -eq $Assert.topFrame
        return @($ok, "topFrame expected '$($Assert.topFrame)', got '$actual'")
    }
    if ($null -ne $Assert.field) {
        $actual = Get-JsonField -Object $Json -Path $Assert.field
        $ok = "$actual" -eq "$($Assert.equals)"
        return @($ok, "$($Assert.field) expected '$($Assert.equals)', got '$actual'")
    }
    if ($null -ne $Assert.hintContains) {
        $hints = ($Json.hints -join "`n")
        $ok = $hints.Contains([string]$Assert.hintContains)
        return @($ok, "a hint should contain '$($Assert.hintContains)' (hints: $hints)")
    }
    if ($null -ne $Assert.jsonContains) {
        $ok = $Raw.Contains([string]$Assert.jsonContains)
        return @($ok, "output should contain '$($Assert.jsonContains)'")
    }
    return @($false, "assertion has no recognized check (topFrame / field+equals / hintContains / jsonContains)")
}

$taskFiles = Get-ChildItem -Path $tasksDir -Filter '*.json' | Sort-Object Name
if ($taskFiles.Count -eq 0) { throw "No task files found in $tasksDir." }

$baseline = if (Test-Path $baselinePath) { Get-Content $baselinePath -Raw | ConvertFrom-Json } else { $null }
$newBaseline = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$rows = [System.Collections.Generic.List[object]]::new()

foreach ($file in $taskFiles) {
    $task = Get-Content $file.FullName -Raw | ConvertFrom-Json
    $id = $task.id

    # OS-guarded tasks (e.g. reading an .etl needs the Windows-only ETW conversion).
    $os = if ($task.os) { $task.os } else { 'any' }
    if ($os -eq 'windows' -and -not $onWindows) {
        $rows.Add([pscustomobject]@{ Task = $id; Status = 'skip'; Calls = '-'; Tokens = '-'; Note = 'windows-only' })
        continue
    }

    $fixtureAbs = (Resolve-Path (Join-Path $root $task.fixture)).Path
    $stepCount = $task.steps.Count
    $totalTokens = 0
    $stepJson = @()
    $stepRaw = @()
    $taskFailed = $false
    $note = ''

    foreach ($step in $task.steps) {
        # Substitute the fixture path and always render JSON (the form an agent consumes).
        $stepArgs = @($step.args | ForEach-Object { $_ -replace '\{fixture\}', $fixtureAbs }) + @('--format', 'json')
        $out = & dotnet $cliDll @stepArgs 2>$null
        $exit = $LASTEXITCODE
        $raw = (($out | Out-String)).Trim()

        if ($exit -ne 0) {
            $taskFailed = $true
            $note = "step '$($step.args -join ' ')' exited $exit"
            break
        }

        $totalTokens += (Get-TokenEstimate -Text $raw)
        $stepRaw += $raw
        try { $stepJson += ($raw | ConvertFrom-Json) }
        catch { $taskFailed = $true; $note = "step output was not valid JSON"; break }
    }

    # Assertions (only when every step ran).
    if (-not $taskFailed) {
        foreach ($assert in @($task.assert)) {
            $stepIndex = if ($null -ne $assert.step) { [int]$assert.step } else { $stepCount - 1 }
            $res = Test-Assertion -Assert $assert -Json $stepJson[$stepIndex] -Raw $stepRaw[$stepIndex]
            if (-not $res[0]) { $taskFailed = $true; $note = $res[1]; break }
        }
    }

    # G1 call budget.
    if (-not $taskFailed -and $stepCount -gt $MaxCalls) {
        $taskFailed = $true
        $note = "$stepCount calls exceeds the $MaxCalls-call budget (G1)"
    }

    if ($taskFailed) {
        $failures.Add("[$id] $note")
        $rows.Add([pscustomobject]@{ Task = $id; Status = 'FAIL'; Calls = $stepCount; Tokens = $totalTokens; Note = $note })
        continue
    }

    # Token regression budget (G2), vs the committed baseline.
    $base = if ($baseline -and $baseline.tasks.$id) { $baseline.tasks.$id } else { $null }
    if (-not $Update) {
        if ($null -eq $base) {
            $failures.Add("[$id] no baseline (run eval/Invoke-Eval.ps1 -Update and commit eval/baselines.json)")
            $rows.Add([pscustomobject]@{ Task = $id; Status = 'FAIL'; Calls = $stepCount; Tokens = $totalTokens; Note = 'no baseline' })
            continue
        }
        $ceiling = [math]::Ceiling($base.tokens * (1 + $TokenGrowthTolerance))
        if ($totalTokens -gt $ceiling) {
            $failures.Add("[$id] $totalTokens tokens exceeds baseline $($base.tokens) +$([int]($TokenGrowthTolerance*100))% (= $ceiling)")
            $rows.Add([pscustomobject]@{ Task = $id; Status = 'FAIL'; Calls = $stepCount; Tokens = $totalTokens; Note = "> baseline $($base.tokens)" })
            continue
        }
        if ($stepCount -gt $base.calls) {
            $failures.Add("[$id] $stepCount calls regressed from baseline $($base.calls)")
            $rows.Add([pscustomobject]@{ Task = $id; Status = 'FAIL'; Calls = $stepCount; Tokens = $totalTokens; Note = "calls > baseline $($base.calls)" })
            continue
        }
    }

    $newBaseline[$id] = [ordered]@{ calls = $stepCount; tokens = $totalTokens }
    $delta = if ($base) { " (baseline $($base.tokens))" } else { '' }
    $rows.Add([pscustomobject]@{ Task = $id; Status = 'pass'; Calls = $stepCount; Tokens = $totalTokens; Note = "$($task.title)$delta" })
}

Write-Host ''
$rows | Format-Table -AutoSize Task, Status, Calls, Tokens, Note | Out-String | Write-Host

if ($Update) {
    $out = [ordered]@{ schemaVersion = 1; tasks = $newBaseline }
    ($out | ConvertTo-Json -Depth 5) | Set-Content -Path $baselinePath
    Write-Host "Wrote baselines for $($newBaseline.Count) task(s) to eval/baselines.json." -ForegroundColor Green
    exit 0
}

if ($failures.Count -gt 0) {
    Write-Host "Eval gate FAILED with $($failures.Count) issue(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Eval gate passed ($($newBaseline.Count) task(s))." -ForegroundColor Green
exit 0
