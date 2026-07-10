#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Compare two labeled agent-eval runs (baseline vs candidate) and report the delta.

.DESCRIPTION
  The tuning loop (M5) changes a surface the live runner presents - an MCP tool
  description/server instruction or CLI command/output behavior - and asks whether
  the change helped without regressing. SKILL.md is not measured by the current
  runner because its Copilot arm disables custom instructions and its Ollama arm
  supplies a generated command protocol. The workflow is: score a baseline, edit
  a measured surface, rebuild, score a candidate, then run this to compare:

    ./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models claude-opus-4.6,gpt-5.2 -N 5 -Label baseline
    # ... edit a [Description] in TraceTools.cs; dotnet build src/Filtrace.Mcp -c Release ...
    ./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models claude-opus-4.6,gpt-5.2 -N 5 -Label candidate
    ./eval/Compare-EvalRuns.ps1 -Baseline baseline -Candidate candidate

  Runs are paired by full identity (host/arm/model), so the report shows whether a
  change that helps one model regresses another - the overfitting signal a second
  host would otherwise provide - and runs from different hosts/arms (whose token
  and call counts are not comparable) never pair silently. The verdict is the
  design's regression budget, which also serves as the noise threshold:

    - a success drop on any task/run            -> REGRESSION
    - token growth over the tolerance (15%)      -> REGRESSION
    - a run present on only one side             -> REJECT (cannot verify)
    - otherwise: higher success, fewer calls, or
      a token drop beyond noise (> 5%)           -> improved; smaller deltas neutral

  Exit code is 1 if any regression or unpaired run is found (so it can gate a
  tuning round), else 0.

.PARAMETER Baseline
  The label of the baseline runs (as passed to Invoke-AgentEval.ps1 -Label).

.PARAMETER Candidate
  The label of the candidate runs.

.PARAMETER ResultsDir
  Where the result JSON files live. Defaults to eval/results.

.PARAMETER TokenGrowthTolerance
  Allowed fractional token growth before a task counts as a regression. Defaults
  to 0.15 (the design's budget).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Candidate,
    [string]$ResultsDir,
    [double]$TokenGrowthTolerance = 0.15
)

$ErrorActionPreference = 'Stop'
if (-not $ResultsDir) { $ResultsDir = Join-Path $PSScriptRoot 'results' }
if (-not (Test-Path $ResultsDir)) { throw "No results directory at '$ResultsDir'. Run Invoke-AgentEval.ps1 -Label first." }

# Load every labeled result payload (host/arm/model, label, timestamp, summary).
$all = Get-ChildItem -Path $ResultsDir -Filter '*.json' | ForEach-Object {
    $f = $_.FullName
    try { $p = Get-Content $f -Raw | ConvertFrom-Json }
    catch { Write-Warning "Skipping unreadable result '$f': $($_.Exception.Message)"; return }
    if ($p.label) {
        [pscustomobject]@{
            key       = ('{0}/{1}/{2}' -f $p.host, $p.arm, $p.model)
            label     = [string]$p.label
            timestamp = $p.timestamp
            summary   = $p.summary
        }
    }
}

# The latest payload per (label, run) where run = host/arm/model.
function Get-LatestRun([string]$Label) {
    @($all | Where-Object { $_.label -eq $Label }) | Group-Object key | ForEach-Object {
        $_.Group | Sort-Object { [datetime]$_.timestamp } | Select-Object -Last 1
    }
}

$base = @(Get-LatestRun $Baseline)
$cand = @(Get-LatestRun $Candidate)
if ($base.Count -eq 0) { throw "No runs labeled '$Baseline' in $ResultsDir." }
if ($cand.Count -eq 0) { throw "No runs labeled '$Candidate' in $ResultsDir." }

# Index a payload's summary rows by task id.
function Get-TaskMap($Payload) {
    $m = @{}
    foreach ($row in $Payload.summary) { $m[[string]$row.Task] = $row }
    return $m
}

$rows = [System.Collections.Generic.List[object]]::new()
$regressions = 0
$improvements = 0
$unpaired = 0

# Pair on the full run identity (host/arm/model) so runs from different hosts or
# arms - whose token/call counts are not comparable - never pair silently.
$runs = @(@($base.key) + @($cand.key) | Sort-Object -Unique)
foreach ($run in $runs) {
    $b = $base | Where-Object { $_.key -eq $run } | Select-Object -First 1
    $c = $cand | Where-Object { $_.key -eq $run } | Select-Object -First 1
    if (-not $b) { $rows.Add([pscustomobject]@{ Run = $run; Task = '(all)'; Success = '-'; Calls = '-'; Tokens = '-'; Verdict = 'no baseline' }); $unpaired++; continue }
    if (-not $c) { $rows.Add([pscustomobject]@{ Run = $run; Task = '(all)'; Success = '-'; Calls = '-'; Tokens = '-'; Verdict = 'no candidate' }); $unpaired++; continue }

    $bm = Get-TaskMap $b
    $cm = Get-TaskMap $c
    foreach ($t in @(@($bm.Keys) + @($cm.Keys) | Sort-Object -Unique)) {
        $br = $bm[$t]; $cr = $cm[$t]
        if (-not $br -or -not $cr) {
            $rows.Add([pscustomobject]@{ Run = $run; Task = $t; Success = '-'; Calls = '-'; Tokens = '-'; Verdict = 'missing task' })
            $unpaired++
            continue
        }
        $ds = [int]$cr.'Success%' - [int]$br.'Success%'
        $dc = [int]$cr.MedCalls - [int]$br.MedCalls
        $bt = [double]$br.MedTokens; $ct = [double]$cr.MedTokens
        $dtFrac = if ($bt -gt 0) { ($ct - $bt) / $bt } else { 0 }

        $verdict = 'neutral'
        if ($ds -lt 0) { $verdict = 'REGRESSION'; $regressions++ }
        elseif ($dtFrac -gt $TokenGrowthTolerance) { $verdict = 'REGRESSION'; $regressions++ }
        elseif ($ds -gt 0 -or $dtFrac -lt -0.05 -or $dc -lt 0) { $verdict = 'improved'; $improvements++ }

        $sign = if ($dtFrac -ge 0) { '+' } else { '' }
        $rows.Add([pscustomobject]@{
                Run     = $run
                Task    = $t
                Success = ('{0}->{1}' -f $br.'Success%', $cr.'Success%')
                Calls   = ('{0}->{1}' -f $br.MedCalls, $cr.MedCalls)
                Tokens  = ('{0}->{1} ({2}{3}%)' -f $br.MedTokens, $cr.MedTokens, $sign, [int][math]::Round($dtFrac * 100))
                Verdict = $verdict
            })
    }
}

Write-Host ''
Write-Host "Baseline '$Baseline' vs candidate '$Candidate' ($($runs.Count) run(s)):"
$rows | Format-Table -AutoSize | Out-String | Write-Host

if ($regressions -gt 0 -or $unpaired -gt 0) {
    Write-Host "Verdict: REJECT - $regressions regression(s), $unpaired unpaired run/task(s), $improvements improvement(s)." -ForegroundColor Red
    exit 1
}
Write-Host "Verdict: ACCEPT - 0 regressions, $improvements improvement(s)." -ForegroundColor Green
exit 0
