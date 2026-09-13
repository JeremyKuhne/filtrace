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
  change that helps one model regresses another - a paired descriptive signal a second
  host would otherwise provide - and runs from different hosts/arms (whose token
  and call counts are not comparable) never pair silently. The verdict is the
  configured regression policy:

    - a success drop on any task/run            -> REGRESSION
    - token growth over the tolerance (15%)      -> REGRESSION
    - a run present on only one side             -> REJECT (cannot verify)
    - otherwise: higher success, fewer calls, or
      a token drop over 5%                        -> improved; smaller deltas neutral

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

function Test-FiniteNumber($Value) {
  if ($Value -isnot [byte] -and $Value -isnot [sbyte] -and
    $Value -isnot [short] -and $Value -isnot [ushort] -and
    $Value -isnot [int] -and $Value -isnot [uint] -and
    $Value -isnot [long] -and $Value -isnot [ulong] -and
    $Value -isnot [float] -and $Value -isnot [double] -and
    $Value -isnot [decimal]) {
    return $false
    }

  [double] $number = [double]$Value
  return -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)
}

function Get-ResultMedian([object[]] $Values) {
  [long[]] $sorted = @($Values | ForEach-Object { [long]$_ } | Sort-Object)
  [int] $count = $sorted.Count
  if ($count -eq 0) { return 0 }
  if ($count % 2) { return [int]$sorted[[int][math]::Floor($count / 2)] }
  return [int][math]::Round(([double]$sorted[$count / 2 - 1] + [double]$sorted[$count / 2]) / 2.0)
}

function Assert-ResultPayload($Payload, [string] $Path) {
  [string[]] $requiredRoot = @('schemaVersion', 'host', 'arm', 'label', 'timestamp', 'summary')
  [string[]] $rootMembers = @($Payload.PSObject.Properties.Name)
  foreach ($member in $requiredRoot) {
    if ($rootMembers -notcontains $member) { throw "Result '$Path' is missing '$member'." }
  }

  if ($Payload.schemaVersion -isnot [long] -and $Payload.schemaVersion -isnot [int]) {
    throw "Result '$Path' has a non-integer schemaVersion."
  }
  [int] $schemaVersion = [int]$Payload.schemaVersion
  if ($schemaVersion -notin @(2, 3)) { throw "Result '$Path' has unsupported schemaVersion $schemaVersion." }
  foreach ($member in @('host', 'arm', 'label')) {
    if ($Payload.$member -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Payload.$member)) {
      throw "Result '$Path' has an invalid '$member'."
    }
  }
  [datetime] $parsedTimestamp = [datetime]::MinValue
  if ($Payload.timestamp -is [datetime]) {
    $parsedTimestamp = [datetime]$Payload.timestamp
  }
  elseif ($Payload.timestamp -isnot [string] -or
    -not [datetime]::TryParse([string]$Payload.timestamp, [ref]$parsedTimestamp)) {
    throw "Result '$Path' has an invalid timestamp."
  }

  [object[]] $summary = @($Payload.summary)
  if ($summary.Count -eq 0) { throw "Result '$Path' has an empty summary." }
  [System.Collections.Generic.HashSet[string]] $tasks = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  foreach ($row in $summary) {
    [string[]] $rowMembers = @($row.PSObject.Properties.Name)
    foreach ($member in @('Task', 'Success%', 'MedCalls', 'MedTokens', 'MedMs')) {
      if ($rowMembers -notcontains $member) { throw "Result '$Path' summary row is missing '$member'." }
    }
    if ($row.Task -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$row.Task)) {
      throw "Result '$Path' has an invalid summary task."
    }
    if (-not $tasks.Add([string]$row.Task)) { throw "Result '$Path' repeats summary task '$($row.Task)'." }
    foreach ($member in @('Success%', 'MedCalls', 'MedTokens', 'MedMs')) {
      if (-not (Test-FiniteNumber $row.$member)) { throw "Result '$Path' has an invalid '$member' value." }
    }
    [double] $success = [double]$row.'Success%'
    if ($success -lt 0 -or $success -gt 100 -or $success -ne [math]::Truncate($success)) {
      throw "Result '$Path' has an out-of-range Success% value."
    }
    foreach ($member in @('MedCalls', 'MedTokens', 'MedMs')) {
      if ([double]$row.$member -lt 0 -or [double]$row.$member -gt [int]::MaxValue -or
        [double]$row.$member -ne [math]::Truncate([double]$row.$member)) {
        throw "Result '$Path' has an out-of-range '$member' value."
      }
    }
  }

  if ($schemaVersion -eq 2) {
    if ($rootMembers -notcontains 'model' -or $Payload.model -isnot [string] -or
      [string]::IsNullOrWhiteSpace([string]$Payload.model)) {
      throw "Legacy result '$Path' has an invalid model."
    }
    return
  }

  foreach ($member in @('model', 'n', 'maxSteps', 'iterations')) {
    if ($rootMembers -notcontains $member) { throw "Schema-v3 result '$Path' is missing '$member'." }
  }
  [string[]] $modelMembers = @($Payload.model.PSObject.Properties.Name)
  foreach ($member in @('requested', 'expected', 'observed', 'observedDistinct', 'verified')) {
    if ($modelMembers -notcontains $member) { throw "Schema-v3 result '$Path' model is missing '$member'." }
  }
  [bool] $defaultCopilotMcpModel = $null -eq $Payload.model.requested -and
    [string]::Equals([string]$Payload.host, 'copilot', [StringComparison]::Ordinal) -and
    [string]::Equals([string]$Payload.arm, 'mcp', [StringComparison]::Ordinal)
  [bool] $requestedModelMatches = $Payload.model.requested -is [string] -and
    -not [string]::IsNullOrWhiteSpace([string]$Payload.model.requested) -and
    [string]::Equals([string]$Payload.model.requested, [string]$Payload.model.observed, [StringComparison]::Ordinal)
  [bool] $strictCopilotCliArm = [string]::Equals([string]$Payload.host, 'copilot', [StringComparison]::Ordinal) -and
    ([string]::Equals([string]$Payload.arm, 'cli', [StringComparison]::Ordinal) -or
      [string]::Equals([string]$Payload.arm, 'cli-skill', [StringComparison]::Ordinal))
  [bool] $expectedModelMatches = ($null -eq $Payload.model.expected -and -not $strictCopilotCliArm) -or
    ($Payload.model.expected -is [string] -and
      -not [string]::IsNullOrWhiteSpace([string]$Payload.model.expected) -and
      [string]::Equals([string]$Payload.model.expected, [string]$Payload.model.observed, [StringComparison]::Ordinal))
  [object[]] $observedDistinct = @($Payload.model.observedDistinct)
  if ($Payload.model.observed -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Payload.model.observed) -or
    $Payload.model.verified -isnot [bool] -or -not $Payload.model.verified -or
    (-not $defaultCopilotMcpModel -and -not $requestedModelMatches) -or -not $expectedModelMatches -or
    $observedDistinct.Count -ne 1 -or $observedDistinct[0] -isnot [string] -or
    -not [string]::Equals([string]$observedDistinct[0], [string]$Payload.model.observed, [StringComparison]::Ordinal)) {
    throw "Schema-v3 result '$Path' does not contain verified exact model identity."
  }
  if ($Payload.n -isnot [long] -and $Payload.n -isnot [int] -or [long]$Payload.n -lt 1 -or [long]$Payload.n -gt 1000 -or
    $Payload.maxSteps -isnot [long] -and $Payload.maxSteps -isnot [int] -or [long]$Payload.maxSteps -lt 1 -or [long]$Payload.maxSteps -gt 1000) {
    throw "Schema-v3 result '$Path' has invalid iteration bounds."
  }

  [object[]] $iterations = @($Payload.iterations)
  if ($iterations.Count -ne ($tasks.Count * [int]$Payload.n)) {
    throw "Schema-v3 result '$Path' has an invalid iteration count."
  }
  foreach ($row in $summary) {
    [string[]] $rowMembers = @($row.PSObject.Properties.Name)
    if ($rowMembers -notcontains 'MedHelpCalls' -or -not (Test-FiniteNumber $row.MedHelpCalls) -or
      [double]$row.MedHelpCalls -lt 0 -or [double]$row.MedHelpCalls -gt [int]::MaxValue -or
      [double]$row.MedHelpCalls -ne [math]::Truncate([double]$row.MedHelpCalls)) {
      throw "Schema-v3 result '$Path' has an invalid 'MedHelpCalls' value."
    }
  }
  [System.Collections.Generic.HashSet[string]] $iterationKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  foreach ($iteration in $iterations) {
    [string[]] $iterationMembers = @($iteration.PSObject.Properties.Name)
    foreach ($member in @('task', 'iteration', 'success', 'calls', 'helpCalls', 'tokens', 'wallMs', 'observedModel', 'observedModels')) {
      if ($iterationMembers -notcontains $member) { throw "Schema-v3 result '$Path' iteration is missing '$member'." }
    }
    if ($iteration.task -isnot [string] -or -not $tasks.Contains([string]$iteration.task) -or
      $iteration.iteration -isnot [long] -and $iteration.iteration -isnot [int] -or
      [long]$iteration.iteration -lt 1 -or [long]$iteration.iteration -gt [long]$Payload.n -or
      $iteration.success -isnot [bool]) {
      throw "Schema-v3 result '$Path' has an invalid iteration identity."
    }
    [string] $iterationKey = "$($iteration.task)/$($iteration.iteration)"
    if (-not $iterationKeys.Add($iterationKey)) { throw "Schema-v3 result '$Path' repeats iteration '$iterationKey'." }
    foreach ($member in @('calls', 'helpCalls', 'tokens', 'wallMs')) {
      if (-not (Test-FiniteNumber $iteration.$member) -or [double]$iteration.$member -lt 0 -or
        [double]$iteration.$member -gt [int]::MaxValue -or
        [double]$iteration.$member -ne [math]::Truncate([double]$iteration.$member)) {
        throw "Schema-v3 result '$Path' has an invalid iteration '$member'."
      }
    }
    [object[]] $observedModels = @($iteration.observedModels)
    if ($iteration.observedModel -isnot [string] -or
      -not [string]::Equals([string]$iteration.observedModel, [string]$Payload.model.observed, [StringComparison]::Ordinal) -or
      $observedModels.Count -ne 1 -or $observedModels[0] -isnot [string] -or
      -not [string]::Equals([string]$observedModels[0], [string]$Payload.model.observed, [StringComparison]::Ordinal)) {
      throw "Schema-v3 result '$Path' iteration model identity does not match the report."
    }
  }

  foreach ($row in $summary) {
    [object[]] $taskIterations = @($iterations | Where-Object {
        [string]::Equals([string]$_.task, [string]$row.Task, [StringComparison]::Ordinal)
      })
    if ($taskIterations.Count -ne [int]$Payload.n) {
      throw "Schema-v3 result '$Path' does not contain every iteration for task '$($row.Task)'."
    }
    [int] $successCount = @($taskIterations | Where-Object { $_.success }).Count
    $expectedSummary = [ordered]@{
      'Success%' = [int][math]::Round(100.0 * $successCount / [int]$Payload.n)
      MedCalls = Get-ResultMedian -Values @($taskIterations | ForEach-Object { $_.calls })
      MedHelpCalls = Get-ResultMedian -Values @($taskIterations | ForEach-Object { $_.helpCalls })
      MedTokens = Get-ResultMedian -Values @($taskIterations | ForEach-Object { $_.tokens })
      MedMs = Get-ResultMedian -Values @($taskIterations | ForEach-Object { $_.wallMs })
    }
    foreach ($member in $expectedSummary.Keys) {
      if ([long]$row.$member -ne [long]$expectedSummary[$member]) {
        throw "Schema-v3 result '$Path' summary '$member' does not match task '$($row.Task)' iterations."
      }
    }
  }
}

function Get-LabelFilePattern([string] $Label) {
  [string] $safeLabel = $Label -replace '[^\w.-]', '_'
  return "-$([regex]::Escape($safeLabel))-\d{8}-\d{6}-\d{3}\.json$"
}

# Load only generated top-level result files for the requested labels. Any matching
# file that cannot be parsed or validated makes the comparison unverifiable.
[string[]] $labelPatterns = @((Get-LabelFilePattern $Baseline), (Get-LabelFilePattern $Candidate))
$all = Get-ChildItem -LiteralPath $ResultsDir -File -Filter '*.json' | Where-Object {
  [string] $name = $_.Name
  @($labelPatterns | Where-Object { $name -match $_ }).Count -gt 0
} | ForEach-Object {
  [string] $path = $_.FullName
  try { $payload = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
  catch { throw "Unreadable matching result '$path': $($_.Exception.Message)" }
  Assert-ResultPayload -Payload $payload -Path $path
  if ($payload.label -notin @($Baseline, $Candidate)) {
    throw "Result '$path' filename label does not match payload label '$($payload.label)'."
  }
  [string] $model = if ([int]$payload.schemaVersion -eq 2) { [string]$payload.model } else { [string]$payload.model.observed }
  [pscustomobject]@{
    key       = ('{0}/{1}/{2}' -f $payload.host, $payload.arm, $model)
    label     = [string]$payload.label
    timestamp = $payload.timestamp
    summary   = $payload.summary
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
