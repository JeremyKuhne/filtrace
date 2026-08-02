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
    whether it reached the right answer and at what cost. That catches MCP
    descriptions/server instructions and CLI command/output regressions - surfaces
    the deterministic gate cannot see. It does not evaluate SKILL.md: the Copilot arm
    deliberately disables custom instructions, and the Ollama arm receives a generated
    command protocol rather than repository customizations.

  It is meant to run locally / occasionally (it needs a model host and is
  non-deterministic), never in CI. The design's regression net stays the
  deterministic gate.

  ARMS: the ollama host runs the cli arm (the harness mediates a ReAct loop - the
  model emits one action per turn, `RUN: <args>` or `ANSWER: <text>`, the harness
  runs `filtrace <args>` on its behalf and feeds back the JSON). The copilot host
  runs the mcp arm (the agent drives the filtrace MCP server's trace_* tools
  itself, exercising the tool descriptions the cli arm never touches). Driving a
  model through a live MCP client without an agent host is a separate runner left
  as future work; eval/mcp-qa.jsonl is its seed.

  HOSTS: ollama (local, no metered API) and copilot (the GitHub Copilot CLI - the
  production target; metered, needs `copilot login`). claude is recognized but not
  yet wired - the runner reports that and exits cleanly.

    METRICS per (task, iteration): success (the final answer contains every
    `expect` substring, case-insensitive; every expected operation was called, no
    forbidden operation was; on the Copilot arm every expected MCP tool also
    completed successfully), calls (filtrace invocations), tokens
  (offline estimate of the tool output the agent consumed, via
  tools/Get-TokenEstimate.ps1 - the same accounting the deterministic gate
  uses), and wall-time. Results are written under eval/results/ and summarized
  as medians with a success rate.

  On the MCP arm the token figure is also broken down per response, because the
  same payload is currently carried twice on the wire (text content and structured
  content) and the transport experiment needs those measured apart: textTokens,
  structuredTokens, and wireTokens (the complete result object). The
  client-visible value - what the host actually placed in the model's context - is
  recorded only when the host reports it; Copilot's transcript exposes session
  usage rather than per-call context, so hostUsage is captured instead of a
  fabricated per-call number.

.PARAMETER AgentHost
  The agent host. 'ollama' (the cli arm) and 'copilot' (the mcp arm) are
  implemented; 'claude' is recognized but not yet wired.

.PARAMETER Model
  The model name passed to the host. Defaults to 'gpt-oss:20b' for ollama; for
  copilot, omit it to use the CLI's default model (pass one to pin it).

.PARAMETER Models
  Run the matrix across several models in one invocation (evaluator diversity for
  the tuning loop), e.g. -Models claude-opus-4.6,gpt-5.2. Overrides -Model; one
  result file is written per model.

.PARAMETER Label
  A label stamped into each result file's name and payload (e.g. baseline or
  candidate) so Compare-EvalRuns.ps1 can pair a candidate run against its baseline.

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

.PARAMETER McpDll
  An explicit Filtrace.Mcp.dll to serve the mcp arm, overriding the Configuration
  build output. This is how a transport or surface variant published under
  artifacts/ is measured against the baseline without editing committed tasks.

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
    [string[]]$Models,
    [string[]]$Tasks,
    [ValidateRange(1, [int]::MaxValue)]
    [int]$N = 1,
    [int]$MaxSteps = 6,
    [string]$Configuration = 'Release',
    [string]$McpDll,
    [string]$OllamaUrl = 'http://localhost:11434/api/chat',
    [string]$OutDir,
    [string]$Label
)

$ErrorActionPreference = 'Stop'
# Remove the temporary Copilot MCP config even on a terminating error (the normal
# path also deletes it at the end). $script: scope so the trap can see it.
$script:mcpConfigPath = $null
trap { if ($script:mcpConfigPath -and (Test-Path $script:mcpConfigPath)) { Remove-Item $script:mcpConfigPath -Force -ErrorAction SilentlyContinue }; break }
$root = Split-Path -Parent $PSScriptRoot
$cliDll = Join-Path $root "src/Filtrace/bin/$Configuration/net10.0/filtrace.dll"
$tasksDir = Join-Path $PSScriptRoot 'tasks'
$mcpQaPath = Join-Path $PSScriptRoot 'mcp-qa.jsonl'
$commandsFile = Join-Path $root 'src/Filtrace/Cli/TraceCommands.cs'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'results' }

if (-not (Test-Path $cliDll)) {
    throw "CLI binary not found at '$cliDll'. Build first: dotnet build filtrace.slnx -c $Configuration."
}

. (Join-Path $root 'tools/Get-TokenEstimate.ps1')

# Surface-neutral operation names, so a task can require the right *intent* even
# after a tool is renamed or folded into another.
. (Join-Path $PSScriptRoot 'Get-OperationName.ps1')

$onWindows = [System.OperatingSystem]::IsWindows()

# Invoking via `pwsh -File ... -Tasks a,b` binds a single literal "a,b" rather than
# a two-element array; normalize by splitting any comma-joined elements.
if ($Tasks) {
    $Tasks = @($Tasks | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# The verb allowlist: the single-trace, JSON-envelope analysis verbs the agent may
# invoke. Derived from the CLI's [Command("name")] set, then filtered to the verbs
# that take one <TRACE> and render the JSON envelope. Excluded: capture (collect,
# which launches a process), the file-op verbs (convert, clean), the two-trace diff,
# and export (whose --format selects a flamegraph writer, not the JSON envelope) -
# the harness forces --format json, which those verbs would reject or misread. A
# model can only ever invoke one of the remaining verbs as the first token, so a
# response cannot launch a process or reach a shell.
$nonAnalysisVerbs = @('collect', 'convert', 'clean', 'diff', 'export')
$verbs = @(Select-String -Path $commandsFile -Pattern '\[Command\("([^"]+)"\)\]' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $nonAnalysisVerbs -notcontains $_ } | Sort-Object -Unique)
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
# <TRACE> is substituted with the real fixture path; --format json is forced; and
# the real path is masked back to <TRACE> in the returned output.
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
    $resolved = @($kept | ForEach-Object { $_.Replace('<TRACE>', $FixtureAbs) }) + @('--format', 'json')
    # Merge stderr so a usage error (bad flag, unknown option) flows back to the
    # model as text it can read and correct - the same signal a real agent sees.
    $out = & dotnet $cliDll @resolved 2>&1
    $raw = (($out | Out-String)).Trim()
    # Re-mask the absolute fixture path back to <TRACE> before it reaches the model
    # or the transcript - error text echoes the real path, and the placeholder
    # contract must stay symmetric (it matters once remote hosts are wired).
    $raw = $raw.Replace($FixtureAbs, '<TRACE>')
    if ($LASTEXITCODE -ne 0) { return @($false, "filtrace exited $LASTEXITCODE`n$raw") }
    return @($true, $raw)
}

# --- Host adapters: send a message list, return the assistant's text. ---------

function Invoke-OllamaChat {
    param([object[]]$Messages)
    $body = @{
        model    = $script:CurrentModel
        messages = $Messages
        stream   = $false
        options  = @{ temperature = 0 }
    } | ConvertTo-Json -Depth 8
    try {
        $resp = Invoke-RestMethod -Uri $OllamaUrl -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 300
    }
    catch {
        throw "Ollama request failed ($OllamaUrl): $($_.Exception.Message). Is 'ollama serve' running and '$($script:CurrentModel)' pulled?"
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

# Run one iteration on the ollama host: the mediated ReAct loop (the harness runs
# filtrace on the model's behalf). Returns the uniform iteration record below.
function Invoke-OllamaIteration {
    param([object]$Task, [string]$FixtureAbs)
    $messages = @(
        @{ role = 'system'; content = $systemPrompt },
        @{ role = 'user'; content = "Question: $($Task.prompt)" }
    )
    $calls = 0; $tokens = 0; $answer = $null; $note = ''
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
            $res = Invoke-FiltraceForAgent -ArgString $action[1] -FixtureAbs $FixtureAbs
            $output = [string]$res[1]
            $clip = if ($output.Length -gt 4000) { $output.Substring(0, 4000) + ' ...[truncated]' } else { $output }
            $tokens += [int](Get-TokenEstimate -Text $clip)
            $transcript.Add([pscustomobject]@{
                    cmd = [string]$action[1]; ok = [bool]$res[0]
                    info = if (-not $res[0]) { $output.Substring(0, [math]::Min(160, $output.Length)) } else { '' }
                })
            $messages += @{ role = 'user'; content = "OUTPUT:`n$clip" }
        }
        else {
            $messages += @{ role = 'user'; content = 'Respond with exactly one line starting with RUN: or ANSWER:.' }
        }
    }
    $sw.Stop()
    return [pscustomobject]@{
        answer = $answer; calls = $calls; tokens = $tokens
        wallMs = [int]$sw.ElapsedMilliseconds; note = $note; transcript = $transcript
    }
}

# Write a temporary Copilot MCP-server config pointing at the locally built
# Filtrace.Mcp server, and return its path. This is what lets the Copilot arm call
# the trace_* tools directly - exercising the MCP tool descriptions the cli arm
# never touches.
function New-FiltraceMcpConfig {
    $dll = if ($McpDll) { $McpDll } else { Join-Path $root "src/Filtrace.Mcp/bin/$Configuration/net10.0/Filtrace.Mcp.dll" }
    if (-not (Test-Path $dll)) {
        throw "Filtrace.Mcp server not found at '$dll'. Build it: dotnet build src/Filtrace.Mcp/Filtrace.Mcp.csproj -c $Configuration, or pass -McpDll."
    }
    $cfg = @{ mcpServers = @{ filtrace = @{ type = 'local'; command = 'dotnet'; args = @((Resolve-Path $dll).Path); tools = @('*') } } }
    $path = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace-mcp-$([guid]::NewGuid().ToString('N')).json"
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, ($cfg | ConvertTo-Json -Depth 6), $utf8)
    return $path
}

# Break one MCP tool result into the pieces the transport experiment compares.
# The same payload is currently carried in both content[0].text and
# structuredContent, so summing them would double-count; they are reported apart
# and `wire` is the complete result object the host received. `shape` records
# which members the host actually surfaced, since that is host-dependent and the
# measurement is only interpretable alongside it.
function Measure-McpResultTokens {
    param($Result)

    $text = 0
    $structured = 0
    $shape = 'none'

    if ($null -eq $Result) {
        return [pscustomobject]@{ text = 0; structured = 0; wire = 0; shape = $shape }
    }

    if ($Result -is [string]) {
        $text = [int](Get-TokenEstimate -Text $Result)
        return [pscustomobject]@{ text = $text; structured = 0; wire = $text; shape = 'text' }
    }

    $members = @($Result.PSObject.Properties.Name)
    $parts = [System.Collections.Generic.List[string]]::new()
    if ($members -contains 'content') {
        $parts.Add('content')
        foreach ($block in @($Result.content)) {
            # Presence of `text`, not truthiness: an empty text block is legitimately
            # zero tokens, and treating it as absent would serialize the whole block
            # and count the wrapper as payload.
            $blockText = if ($block -is [string]) { $block } elseif ($null -ne $block.text) { [string]$block.text } else { ($block | ConvertTo-Json -Depth 8 -Compress) }
            $text += [int](Get-TokenEstimate -Text $blockText)
        }
    }
    if ($members -contains 'structuredContent' -and $null -ne $Result.structuredContent) {
        $parts.Add('structuredContent')
        $structured = [int](Get-TokenEstimate -Text ($Result.structuredContent | ConvertTo-Json -Depth 12 -Compress))
    }
    if ($parts.Count -gt 0) { $shape = $parts -join '+' } else { $shape = 'object' }

    $wire = [int](Get-TokenEstimate -Text ($Result | ConvertTo-Json -Depth 12 -Compress))
    # A host that flattens the result to one object exposes no separate text block;
    # attribute the whole payload to text so the headline metric stays comparable
    # with the cli arm rather than reading as zero.
    if ($shape -eq 'object') { $text = $wire }
    return [pscustomobject]@{ text = $text; structured = $structured; wire = $wire; shape = $shape }
}

# Run one iteration on the Copilot CLI host: hand the agent the task prompt and the
# filtrace MCP server, let it drive the trace_* tools autonomously (headless,
# --allow-all), and parse its JSONL transcript. The metrics map as: calls = the
# filtrace tool invocations, tokens = the offline estimate of those tools' output
# (matching the cli arm's accounting), wall-time from the session. Returns the
# uniform iteration record.
function Invoke-CopilotIteration {
    param([object]$Task, [string]$FixtureAbs, [string]$McpConfig)
    $prompt = "Using the filtrace MCP tools, analyze the .NET trace at $FixtureAbs and answer the question. Base your answer only on tool output; do not guess. Question: $($Task.prompt)"
    $cmdArgs = @(
        '-p', $prompt, '--output-format', 'json', '--allow-all',
        '--no-custom-instructions', '--disable-builtin-mcps',
        '--additional-mcp-config', "@$McpConfig"
    )
    if ($script:CurrentModel) { $cmdArgs += @('--model', $script:CurrentModel) }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $out = & copilot @cmdArgs 2>&1
    $sw.Stop()
    $events = @($out | ForEach-Object { try { $_ | ConvertFrom-Json } catch {} } | Where-Object { $_ })
    # Mask the fixture path back to <TRACE> in both raw and JSON-escaped (\\) forms,
    # since arguments are serialized with ConvertTo-Json (which doubles backslashes).
    $jsonPath = $FixtureAbs.Replace('\', '\\')
    $mask = { param($t) ([string]$t).Replace($jsonPath, '<TRACE>').Replace($FixtureAbs, '<TRACE>') }
    $answer = & $mask ([string](($events | Where-Object { $_.type -eq 'assistant.message' } | Select-Object -Last 1).data.content))
    $starts = @($events | Where-Object { $_.type -eq 'tool.execution_start' -and $_.data.mcpServerName -eq 'filtrace' })
    $completes = @($events | Where-Object { $_.type -eq 'tool.execution_complete' })
    $tokens = 0
    $textTokens = 0
    $structuredTokens = 0
    $wireTokens = 0
    $resultShapes = [System.Collections.Generic.List[string]]::new()
    $transcript = [System.Collections.Generic.List[object]]::new()
    foreach ($s in $starts) {
        $c = $completes | Where-Object { $_.data.toolCallId -eq $s.data.toolCallId } | Select-Object -First 1
        $split = Measure-McpResultTokens -Result $(if ($c) { $c.data.result } else { $null })
        # The headline metric stays "what the agent consumed" - the text copy - so it
        # remains comparable with the cli arm and with existing baselines.
        $tokens += $split.text
        $textTokens += $split.text
        $structuredTokens += $split.structured
        $wireTokens += $split.wire
        if (-not $resultShapes.Contains($split.shape)) { $resultShapes.Add($split.shape) }
        $transcript.Add([pscustomobject]@{
                cmd  = & $mask ("$($s.data.mcpToolName) $($s.data.arguments | ConvertTo-Json -Compress)")
                ok   = [bool]($c -and $c.data.success); info = ''
                textTokens = $split.text; structuredTokens = $split.structured; wireTokens = $split.wire
            })
    }
    $result = $events | Where-Object { $_.type -eq 'result' } | Select-Object -First 1
    $note = ''
    if ($result -and $result.exitCode -ne 0) { $note = "copilot exitCode $($result.exitCode)" }
    elseif ($starts.Count -eq 0) { $note = 'no filtrace tool call' }
    $m = ($events | Where-Object { $_.type -eq 'session.tools_updated' } | Select-Object -First 1).data.model
    if ($m) { $script:CopilotActualModel = $m }
    $wallMs = if ($result.usage.sessionDurationMs) { [int]$result.usage.sessionDurationMs } else { [int]$sw.ElapsedMilliseconds }
    return [pscustomobject]@{
        answer = $answer; calls = $starts.Count; tokens = $tokens
        textTokens = $textTokens; structuredTokens = $structuredTokens; wireTokens = $wireTokens
        resultShape = ($resultShapes -join ',')
        # Whatever the host reports about its own context spend. Recorded verbatim
        # rather than reduced, because what a client puts in front of the model is
        # host-specific and this arm must not guess it.
        hostUsage = $result.usage
        wallMs = $wallMs; note = $note; transcript = $transcript
    }
}

# --- Task selection -----------------------------------------------------------

$mcpQaById = @{}
foreach ($line in Get-Content -LiteralPath $mcpQaPath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $mcpQaTask = $line | ConvertFrom-Json
    if ($mcpQaById.ContainsKey($mcpQaTask.id)) { throw "Duplicate MCP QA task id '$($mcpQaTask.id)'." }
    $mcpQaById[$mcpQaTask.id] = $mcpQaTask
}

$allTaskFiles = Get-ChildItem -Path $tasksDir -Filter '*.json' | Sort-Object Name
$selected = [System.Collections.Generic.List[object]]::new()
foreach ($file in $allTaskFiles) {
    $task = Get-Content $file.FullName -Raw | ConvertFrom-Json
    if ($Tasks -and ($Tasks -notcontains $task.id)) { continue }
    $os = if ($task.os) { $task.os } else { 'any' }
    if ($os -eq 'windows' -and -not $onWindows) { continue }
    if (-not $mcpQaById.ContainsKey($task.id)) { throw "Task '$($task.id)' is missing from eval/mcp-qa.jsonl." }
    $qa = $mcpQaById[$task.id]
    $task | Add-Member -NotePropertyName expectTools -NotePropertyValue @($qa.expectTools)
    $task | Add-Member -NotePropertyName expectOperations -NotePropertyValue @($qa.expectOperations | Where-Object { $_ })
    $task | Add-Member -NotePropertyName forbidOperations -NotePropertyValue @($qa.forbidOperations | Where-Object { $_ })
    $task | Add-Member -NotePropertyName maxCalls -NotePropertyValue $qa.maxCalls
    $selected.Add($task)
}
if ($selected.Count -eq 0) { throw "No matching tasks (filter: $($Tasks -join ', '))." }

# Per-host preflight (once). Copilot is agentic (it drives the trace_* tools itself
# over the MCP arm); ollama is the mediated cli ReAct loop.
$arm = if ($AgentHost -eq 'ollama') { 'cli' } else { 'mcp' }
$script:mcpConfigPath = $null
if ($AgentHost -eq 'copilot') {
    if (-not (Get-Command copilot -ErrorAction SilentlyContinue)) {
        throw "The 'copilot' CLI was not found on PATH. Install GitHub Copilot CLI and run 'copilot login'."
    }
    $script:mcpConfigPath = New-FiltraceMcpConfig
}

# The model list. -Models runs the matrix across several models in one invocation
# (evaluator diversity for the tuning loop). For copilot a $null entry means the
# CLI's default model. Default: -Model (ollama) or the copilot default.
#
# Built as a List rather than from a statement value: assigning `if (...) { @($null) }`
# unrolls the single null element and leaves $modelList as plain $null, which then
# iterates zero times - so the whole run silently did nothing while still exiting 0.
# @($modelList).Count reports 1 in that state, so the emptiness guard below cannot
# catch it either.
$modelList = [System.Collections.Generic.List[object]]::new()
if ($PSBoundParameters.ContainsKey('Models')) {
    foreach ($name in @($Models | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $modelList.Add($name)
    }
}
elseif ($AgentHost -eq 'copilot' -and -not $PSBoundParameters.ContainsKey('Model')) { $modelList.Add($null) }
else { $modelList.Add($Model) }
if ($modelList.Count -eq 0) { throw '-Models expanded to an empty list. Pass at least one model, e.g. -Models claude-opus-4.6.' }

# Median over a small int list.
function Get-Median([System.Collections.Generic.List[int]]$v) {
    $s = @($v | Sort-Object); $n = $s.Count
    if ($n -eq 0) { return 0 }
    if ($n % 2) { return $s[[int][math]::Floor($n / 2)] }
    return [int][math]::Round(($s[$n / 2 - 1] + $s[$n / 2]) / 2.0)
}

# Run one model configuration (tasks x N), print the table, persist a result file,
# and return its path. $script:CurrentModel drives both host adapters.
function Invoke-EvalRun {
    param($RunModel, [string]$RunLabel)
    $script:CurrentModel = $RunModel
    $script:CopilotActualModel = $null
    $modelLabel = if ($AgentHost -eq 'copilot') { if ($RunModel) { $RunModel } else { 'copilot-default' } } else { $RunModel }
    $labelTag = if ($RunLabel) { " label=$RunLabel" } else { '' }
    Write-Host "Live agent eval: host=$AgentHost model=$modelLabel arm=$arm tasks=$($selected.Count) N=$N$labelTag" -ForegroundColor Cyan

    $iterRecords = [System.Collections.Generic.List[object]]::new()
    $rows = [System.Collections.Generic.List[object]]::new()
    $transportRows = [System.Collections.Generic.List[object]]::new()
    foreach ($task in $selected) {
        $fixtureAbs = (Resolve-Path (Join-Path $root $task.fixture)).Path
        $expect = @($task.expect)
        $successes = 0
        $callsList = [System.Collections.Generic.List[int]]::new()
        $tokensList = [System.Collections.Generic.List[int]]::new()
        $msList = [System.Collections.Generic.List[int]]::new()
        $textList = [System.Collections.Generic.List[int]]::new()
        $structuredList = [System.Collections.Generic.List[int]]::new()
        $wireList = [System.Collections.Generic.List[int]]::new()
        $shapes = [System.Collections.Generic.List[string]]::new()

        for ($i = 1; $i -le $N; $i++) {
            $r = switch ($AgentHost) {
                'ollama' { Invoke-OllamaIteration -Task $task -FixtureAbs $fixtureAbs }
                'copilot' { Invoke-CopilotIteration -Task $task -FixtureAbs $fixtureAbs -McpConfig $script:mcpConfigPath }
                default { throw "Host '$AgentHost' is recognized but not yet wired." }
            }
            $answer = $r.answer; $calls = [int]$r.calls; $tokens = [int]$r.tokens
            $wallMs = [int]$r.wallMs; $note = $r.note; $transcript = $r.transcript

            $ok = $false
            if ($answer) {
                $ok = $true
                foreach ($e in $expect) { if ($answer -notmatch [regex]::Escape($e)) { $ok = $false } }
                if (-not $ok -and -not $note) { $note = 'answer missing expected content' }
            }
            elseif (-not $note) { $note = 'no answer produced' }

            # A correct-looking MCP answer must be grounded in the tools the task is
            # designed to exercise. Extra calls (for example trace_info first) are fine;
            # every expected tool must appear at least once as a successful call.
            # The first token of a transcript entry is the MCP tool name on the mcp arm
            # and the CLI verb on the cli arm.
            $successfulNames = @($transcript |
                Where-Object { $_.ok } |
                ForEach-Object { ($_.cmd -split '\s+', 2)[0] })
            $calledOperations = @($successfulNames | ForEach-Object { Get-OperationName -Name $_ } | Sort-Object -Unique)
            # Forbidden operations are graded on the attempt, not the outcome: reaching
            # for source lines on a speedscope profile is the mistake, and filtrace
            # rejects it - so scoring only successful calls would never see it.
            $attemptedOperations = @($transcript |
                ForEach-Object { Get-OperationName -Name ($_.cmd -split '\s+', 2)[0] } |
                Sort-Object -Unique)

            if ($ok -and $AgentHost -eq 'copilot') {
                $missingTools = @($task.expectTools | Where-Object { $successfulNames -notcontains $_ })
                if ($missingTools.Count -gt 0) {
                    $ok = $false
                    $note = "missing expected successful MCP tool(s): $($missingTools -join ', ')"
                }
            }

            # Intent grading. Unlike the exact tool names, this survives a rename or a
            # consolidation, so a baseline and a candidate surface can be compared.
            if ($ok -and $task.expectOperations.Count -gt 0) {
                $missingOperations = @($task.expectOperations | Where-Object { $calledOperations -notcontains $_ })
                if ($missingOperations.Count -gt 0) {
                    $ok = $false
                    $note = "missing expected operation(s): $($missingOperations -join ', ')"
                }
            }
            if ($ok -and $task.forbidOperations.Count -gt 0) {
                $forbiddenUsed = @($task.forbidOperations | Where-Object { $attemptedOperations -contains $_ })
                if ($forbiddenUsed.Count -gt 0) {
                    $ok = $false
                    $note = "called forbidden operation(s): $($forbiddenUsed -join ', ')"
                }
            }
            if ($ok -and $task.maxCalls -and $calls -gt [int]$task.maxCalls) {
                $ok = $false
                $note = "$calls calls exceeds this task's $($task.maxCalls)-call budget"
            }

            if ($ok) { $successes++ }
            $callsList.Add($calls); $tokensList.Add($tokens); $msList.Add($wallMs)
            if ($null -ne $r.wireTokens) {
                $textList.Add([int]$r.textTokens); $structuredList.Add([int]$r.structuredTokens); $wireList.Add([int]$r.wireTokens)
                if ($r.resultShape -and -not $shapes.Contains([string]$r.resultShape)) { $shapes.Add([string]$r.resultShape) }
            }
            $iterRecords.Add([pscustomobject]@{
                    task = $task.id; iteration = $i; success = $ok; calls = $calls
                    tokens = $tokens; wallMs = $wallMs
                    textTokens = $r.textTokens; structuredTokens = $r.structuredTokens
                    wireTokens = $r.wireTokens; resultShape = $r.resultShape; hostUsage = $r.hostUsage
                    operations = $calledOperations
                    attemptedOperations = $attemptedOperations
                    answer = $answer; note = $note; transcript = $transcript
                })
            $tag = if ($ok) { 'ok ' } else { 'MISS' }
            Write-Host ("  [{0}] {1} iter {2}/{3}: calls={4} tokens={5} {6}ms {7}" -f $tag, $task.id, $i, $N, $calls, $tokens, $wallMs, $note)
        }

        $rows.Add([pscustomobject]@{
                Task = $task.id; 'Success%' = [int]([math]::Round(100.0 * $successes / $N))
                MedCalls = (Get-Median $callsList); MedTokens = (Get-Median $tokensList); MedMs = (Get-Median $msList)
            })
        if ($wireList.Count -gt 0) {
            $transportRows.Add([pscustomobject]@{
                    Task = $task.id; MedText = (Get-Median $textList)
                    MedStructured = (Get-Median $structuredList); MedWire = (Get-Median $wireList)
                    Shape = ($shapes -join ',')
                })
        }
    }

    Write-Host ''
    $rows | Format-Table -AutoSize | Out-String | Write-Host
    if ($transportRows.Count -gt 0) {
        Write-Host 'Per-response transport cost (text and structured are two copies of the same payload):'
        $transportRows | Format-Table -AutoSize | Out-String | Write-Host
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
    # Report the model Copilot actually used (from its JSONL) when none was pinned.
    $reportModel = if ($AgentHost -eq 'copilot' -and $script:CopilotActualModel) { $script:CopilotActualModel } else { $modelLabel }
    $safeModel = ($reportModel -replace '[^\w.-]', '_')
    $labelPart = if ($RunLabel) { "$($RunLabel -replace '[^\w.-]', '_')-" } else { '' }
    $resultPath = Join-Path $OutDir "$AgentHost-$safeModel-$labelPart$stamp.json"
    $payload = [ordered]@{
        schemaVersion = 2
        host          = $AgentHost
        model         = $reportModel
        arm           = $arm
        label         = $RunLabel
        n             = $N
        maxSteps      = $MaxSteps
        mcpDll        = $McpDll
        timestamp     = (Get-Date).ToString('o')
        summary       = $rows
        transport     = $transportRows
        iterations    = $iterRecords
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($resultPath, (($payload | ConvertTo-Json -Depth 6) + "`n"), $utf8)
    Write-Host "Wrote results ($modelLabel$labelTag) to $resultPath" -ForegroundColor Green
    return $resultPath
}

# --- Run each model in the list -----------------------------------------------

# A labeled run is a baseline or a candidate someone will compare; one or two
# iterations cannot support a median, and a surface decision taken on that basis
# is noise. Warn rather than block - a labeled smoke run is still useful.
if ($Label -and $N -lt 3) {
    Write-Warning "-Label '$Label' with N=${N}: a baseline or candidate needs at least 3 iterations per task for its medians to mean anything."
}

foreach ($m in $modelList) { Invoke-EvalRun -RunModel $m -RunLabel $Label | Out-Null }

# Clean up the temporary Copilot MCP config, if one was written.
if ($script:mcpConfigPath -and (Test-Path $script:mcpConfigPath)) { Remove-Item $script:mcpConfigPath -Force -ErrorAction SilentlyContinue }
