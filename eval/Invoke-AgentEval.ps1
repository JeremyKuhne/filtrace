#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Live headless-agent arm for the filtrace eval harness (the LLM arm of M5).

.DESCRIPTION
  The deterministic gate (Invoke-Eval.ps1) runs each task's *canonical* tool
  sequence directly - no model - and is the cheap CI regression net. This runner
  is the other arm: it gives a real agent only the task's natural-language
  `prompt` and lets the model decide which filtrace commands to run, then scores
  whether it reached the right answer and at what cost. That is what catches a
  description, help text, or skill that reads well to a human but steers a model
  wrong - the surfaces the deterministic gate cannot see.

  It is meant to run locally / occasionally (it needs a model host and is
  non-deterministic), never in CI. The design's regression net stays the
  deterministic gate.

  ARM: cli. The harness mediates a ReAct loop - the model emits one action per
  turn (`RUN: <args>` or `ANSWER: <text>`), the harness runs `filtrace <args>`
  on its behalf and feeds back the JSON, until the model answers or hits the
  call budget. The MCP arm is captured statically in eval/mcp-qa.jsonl (the
  expected tool per task); driving a model through a live MCP client is a
  separate, heavier runner left as future work.

  HOST: ollama (implemented, local, no metered API). copilot / claude are
  recognized but not yet wired - the runner reports that and exits cleanly so
  the surface is ready when those CLIs are present.

  METRICS per (task, iteration): success (the final answer contains every
  `expect` substring, case-insensitive), calls (filtrace invocations), tokens
  (offline estimate of the tool output the agent consumed, via
  tools/Get-TokenEstimate.ps1 - the same accounting the deterministic gate
  uses), and wall-time. Results are written under eval/results/ and summarized
  as medians with a success rate.

.PARAMETER AgentHost
  The agent host. 'ollama' (default) is implemented; 'copilot' and 'claude' are
  recognized but not yet wired.

.PARAMETER Model
  The model name passed to the host. Defaults to 'gpt-oss:20b' for ollama.

.PARAMETER Tasks
  Task ids to run (default: every task whose os matches this machine). Accepts
  the smoke subset, e.g. -Tasks cpu-hotspot,gc-report.

.PARAMETER N
  Iterations per task (the design's N). Defaults to 1 for a quick sample; raise
  for a real measurement (medians get meaningful around N = 5-10).

.PARAMETER MaxSteps
  Per-attempt filtrace call budget (the design's G1). Defaults to 6.

.PARAMETER Configuration
  The build configuration whose CLI binary the agent drives. Defaults to Release.

.PARAMETER OllamaUrl
  The Ollama chat endpoint. Defaults to http://localhost:11434/api/chat.

.PARAMETER OutDir
  Where to write the results JSON. Defaults to eval/results.

.EXAMPLE
  ./eval/Invoke-AgentEval.ps1 -Tasks cpu-hotspot,gc-report -N 1
  A quick two-task sample against the default local model.
#>
[CmdletBinding()]
param(
    [ValidateSet('ollama', 'copilot', 'claude')]
    [string]$AgentHost = 'ollama',
    [string]$Model = 'gpt-oss:20b',
    [string[]]$Tasks,
    [int]$N = 1,
    [int]$MaxSteps = 6,
    [string]$Configuration = 'Release',
    [string]$OllamaUrl = 'http://localhost:11434/api/chat',
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cliDll = Join-Path $root "src/Filtrace/bin/$Configuration/net10.0/filtrace.dll"
$tasksDir = Join-Path $PSScriptRoot 'tasks'
$commandsFile = Join-Path $root 'src/Filtrace/Cli/TraceCommands.cs'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'results' }

if (-not (Test-Path $cliDll)) {
    throw "CLI binary not found at '$cliDll'. Build first: dotnet build filtrace.slnx -c $Configuration."
}

. (Join-Path $root 'tools/Get-TokenEstimate.ps1')

$onWindows = [System.OperatingSystem]::IsWindows()

# Invoking via `pwsh -File ... -Tasks a,b` binds a single literal "a,b" rather than
# a two-element array; normalize by splitting any comma-joined elements.
if ($Tasks) {
    $Tasks = @($Tasks | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# The verb allowlist is the source of truth in the CLI: every [Command("name")].
# The agent may only ever invoke one of these as the first token, so a model
# response can never reach a shell - the harness execs the filtrace dll directly.
$verbs = @(Select-String -Path $commandsFile -Pattern '\[Command\("([^"]+)"\)\]' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($verbs.Count -eq 0) { throw "No [Command(...)] verbs found in $commandsFile." }

# Split a model-supplied argument string into tokens, honoring double quotes.
# Whitespace separated; "quoted segments" keep their spaces.
function Split-ArgString {
    param([string]$Text)
    $tokens = [System.Collections.Generic.List[string]]::new()
    foreach ($m in [regex]::Matches($Text.Trim(), '"([^"]*)"|(\S+)')) {
        if ($m.Groups[1].Success) { $tokens.Add($m.Groups[1].Value) }
        else { $tokens.Add($m.Groups[2].Value) }
    }
    # Return the elements to the pipeline; every call site wraps with @() so a
    # one- or zero-element result stays an array. (A unary-comma return would
    # double-wrap under @() and make $tokens[0] the whole inner array.)
    return $tokens.ToArray()
}

# Run one filtrace command on the agent's behalf. Returns (ok, output) where ok
# is $false for a rejected verb or a non-zero exit. The trace placeholder
# <TRACE> is substituted with the real fixture path; --format json is forced.
function Invoke-FiltraceForAgent {
    param([string]$ArgString, [string]$FixtureAbs)
    # Models often wrap the command in backticks and prefix the launcher
    # ("filtrace" / "dotnet filtrace.dll") even when told to give bare args;
    # strip both so a well-intentioned command is not spuriously rejected.
    $clean = $ArgString.Trim().Trim('`').Trim()
    $tokens = @(Split-ArgString -Text $clean)
    while ($tokens.Count -gt 0 -and $tokens[0] -match '^(?i)(filtrace(\.dll|\.exe)?|dotnet|\./filtrace)$') {
        $tokens = @($tokens | Select-Object -Skip 1)
    }
    if ($tokens.Count -eq 0) { return @($false, 'empty command') }
    $verb = $tokens[0]
    if ($verbs -notcontains $verb) {
        return @($false, "rejected: '$verb' is not a filtrace verb (allowed: $($verbs -join ', '))")
    }
    # The harness always renders JSON, so drop any model-supplied output-format
    # flag (--format <x> or a bare --json) to avoid a conflicting duplicate.
    $kept = [System.Collections.Generic.List[string]]::new()
    for ($k = 0; $k -lt $tokens.Count; $k++) {
        if ($tokens[$k] -eq '--format') { $k++; continue }
        if ($tokens[$k] -eq '--json') { continue }
        $kept.Add($tokens[$k])
    }
    $resolved = @($kept | ForEach-Object { $_ -replace '<TRACE>', $FixtureAbs }) + @('--format', 'json')
    # Merge stderr so a usage error (bad flag, unknown option) flows back to the
    # model as text it can read and correct - the same signal a real agent sees.
    $out = & dotnet $cliDll @resolved 2>&1
    $raw = (($out | Out-String)).Trim()
    if ($LASTEXITCODE -ne 0) { return @($false, "filtrace exited $LASTEXITCODE`n$raw") }
    return @($true, $raw)
}

# --- Host adapters: send a message list, return the assistant's text. ---------

function Invoke-OllamaChat {
    param([object[]]$Messages)
    $body = @{
        model    = $Model
        messages = $Messages
        stream   = $false
        options  = @{ temperature = 0 }
    } | ConvertTo-Json -Depth 8
    try {
        $resp = Invoke-RestMethod -Uri $OllamaUrl -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 300
    }
    catch {
        throw "Ollama request failed ($OllamaUrl): $($_.Exception.Message). Is 'ollama serve' running and '$Model' pulled?"
    }
    $content = $resp.message.content
    if ([string]::IsNullOrWhiteSpace($content) -and $resp.message.thinking) { $content = $resp.message.thinking }
    return [string]$content
}

function Send-AgentMessages {
    param([object[]]$Messages)
    switch ($AgentHost) {
        'ollama' { return Invoke-OllamaChat -Messages $Messages }
        default { throw "Host '$AgentHost' is recognized but not yet wired. Use -AgentHost ollama, or add an adapter." }
    }
}

# Pull the first actionable directive (RUN: / ANSWER:) out of a model reply,
# ignoring any surrounding reasoning or markdown fences.
function Get-AgentAction {
    param([string]$Reply)
    foreach ($line in ($Reply -split "`n")) {
        $t = $line.Trim().TrimStart('>', '*', '-', ' ', '`')
        if ($t -match '^(?i)RUN:\s*(.+)$') { return @('run', $Matches[1].Trim()) }
        if ($t -match '^(?i)ANSWER:\s*(.+)$') { return @('answer', $Matches[1].Trim()) }
    }
    return @('none', '')
}

$systemPrompt = @"
You are a .NET performance analyst. You investigate one captured trace using the
`filtrace` command-line tool. Refer to the trace as the literal token <TRACE>;
always use <TRACE> as the trace path.

Respond with EXACTLY ONE line each turn, either:
  RUN: <filtrace args>     to run `filtrace <args>` and see its JSON output
  ANSWER: <final answer>   when you can answer the question

Available verbs (each takes <TRACE> as the first argument): $($verbs -join ', ').
Give only the arguments - do not prefix the line with `filtrace`, and do not add
any --format or --json flag (output is already JSON). Example: RUN: cpu <TRACE> --top 5
Base every claim on tool output - do not invent frames or numbers. If a command
returns an error, read it and try a corrected command. Keep going until you can
answer.
"@

# --- Task selection -----------------------------------------------------------

$allTaskFiles = Get-ChildItem -Path $tasksDir -Filter '*.json' | Sort-Object Name
$selected = [System.Collections.Generic.List[object]]::new()
foreach ($file in $allTaskFiles) {
    $task = Get-Content $file.FullName -Raw | ConvertFrom-Json
    if ($Tasks -and ($Tasks -notcontains $task.id)) { continue }
    $os = if ($task.os) { $task.os } else { 'any' }
    if ($os -eq 'windows' -and -not $onWindows) { continue }
    $selected.Add($task)
}
if ($selected.Count -eq 0) { throw "No matching tasks (filter: $($Tasks -join ', '))." }

Write-Host "Live agent eval: host=$AgentHost model=$Model arm=cli tasks=$($selected.Count) N=$N" -ForegroundColor Cyan

# --- The ReAct loop -----------------------------------------------------------

$iterRecords = [System.Collections.Generic.List[object]]::new()
$rows = [System.Collections.Generic.List[object]]::new()

foreach ($task in $selected) {
    $fixtureAbs = (Resolve-Path (Join-Path $root $task.fixture)).Path
    $expect = @($task.expect)
    $successes = 0
    $callsList = [System.Collections.Generic.List[int]]::new()
    $tokensList = [System.Collections.Generic.List[int]]::new()
    $msList = [System.Collections.Generic.List[int]]::new()

    for ($i = 1; $i -le $N; $i++) {
        $messages = @(
            @{ role = 'system'; content = $systemPrompt },
            @{ role = 'user'; content = "Question: $($task.prompt)" }
        )
        $calls = 0
        $tokens = 0
        $answer = $null
        $note = ''
        $transcript = [System.Collections.Generic.List[object]]::new()
        $sw = [System.Diagnostics.Stopwatch]::StartNew()

        # +2 turns of slack so a final ANSWER after MaxSteps RUNs is still read.
        for ($turn = 0; $turn -lt ($MaxSteps + 2); $turn++) {
            $reply = Send-AgentMessages -Messages $messages
            $action = Get-AgentAction -Reply $reply
            $messages += @{ role = 'assistant'; content = $reply }

            if ($action[0] -eq 'answer') { $answer = $action[1]; break }
            elseif ($action[0] -eq 'run') {
                if ($calls -ge $MaxSteps) { $note = "exceeded $MaxSteps-call budget (G1)"; break }
                $calls++
                $res = Invoke-FiltraceForAgent -ArgString $action[1] -FixtureAbs $fixtureAbs
                $output = [string]$res[1]
                if ($res[0]) { $tokens += [int](Get-TokenEstimate -Text $output) }
                $transcript.Add([pscustomobject]@{
                        cmd = [string]$action[1]; ok = [bool]$res[0]
                        info = if (-not $res[0]) { $output.Substring(0, [math]::Min(160, $output.Length)) } else { '' }
                    })
                $clip = if ($output.Length -gt 4000) { $output.Substring(0, 4000) + ' ...[truncated]' } else { $output }
                $messages += @{ role = 'user'; content = "OUTPUT:`n$clip" }
            }
            else {
                $messages += @{ role = 'user'; content = 'Respond with exactly one line starting with RUN: or ANSWER:.' }
            }
        }
        $sw.Stop()

        $ok = $false
        if ($answer) {
            $ok = $true
            foreach ($e in $expect) { if ($answer -notmatch [regex]::Escape($e)) { $ok = $false } }
            if (-not $ok -and -not $note) { $note = 'answer missing expected content' }
        }
        elseif (-not $note) { $note = 'no answer produced' }

        if ($ok) { $successes++ }
        $callsList.Add($calls); $tokensList.Add($tokens); $msList.Add([int]$sw.ElapsedMilliseconds)
        $iterRecords.Add([pscustomobject]@{
                task = $task.id; iteration = $i; success = $ok; calls = $calls
                tokens = $tokens; wallMs = [int]$sw.ElapsedMilliseconds
                answer = $answer; note = $note; transcript = $transcript
            })
        $tag = if ($ok) { 'ok ' } else { 'MISS' }
        Write-Host ("  [{0}] {1} iter {2}/{3}: calls={4} tokens={5} {6}ms {7}" -f $tag, $task.id, $i, $N, $calls, $tokens, [int]$sw.ElapsedMilliseconds, $note)
    }

    # Median helper over a small int list.
    function Get-Median([System.Collections.Generic.List[int]]$v) {
        $s = @($v | Sort-Object); $n = $s.Count
        if ($n -eq 0) { return 0 }
        if ($n % 2) { return $s[[int][math]::Floor($n / 2)] }
        return [int][math]::Round(($s[$n / 2 - 1] + $s[$n / 2]) / 2.0)
    }

    $rows.Add([pscustomobject]@{
            Task = $task.id; 'Success%' = [int]([math]::Round(100.0 * $successes / $N))
            MedCalls = (Get-Median $callsList); MedTokens = (Get-Median $tokensList); MedMs = (Get-Median $msList)
        })
}

Write-Host ''
$rows | Format-Table -AutoSize | Out-String | Write-Host

# --- Persist results ----------------------------------------------------------

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$safeModel = ($Model -replace '[^\w.-]', '_')
$resultPath = Join-Path $OutDir "$AgentHost-$safeModel-$stamp.json"
$payload = [ordered]@{
    schemaVersion = 1
    host          = $AgentHost
    model         = $Model
    arm           = 'cli'
    n             = $N
    maxSteps      = $MaxSteps
    timestamp     = (Get-Date).ToString('o')
    summary       = $rows
    iterations    = $iterRecords
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($resultPath, (($payload | ConvertTo-Json -Depth 6) + "`n"), $utf8)
Write-Host "Wrote results for $($selected.Count) task(s) to $resultPath" -ForegroundColor Green
