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
    descriptions/server instructions, CLI command/output, and discovered skill-use
    regressions - surfaces the deterministic gate cannot see. The strict Copilot
    Windows-only CLI arms run in an owned workspace; cli-skill copies the shipped
    filtrace skill there, enables normal project-skill discovery, and verifies the
    exact discovered path, skill invocation, and injected model context.

  It is meant to run locally / occasionally (it needs a model host and is
  non-deterministic), never in CI. The design's regression net stays the
  deterministic gate.

    ARMS: the ollama host keeps its mediated cli arm (the harness runs filtrace for
    the model). The Copilot host accepts mcp, cli, and cli-skill. mcp preserves the
    existing MCP-tool path. The two Copilot CLI arms use the same host, pinned model,
    and tool set against this checkout's apphost; only cli-skill receives the shipped
    skill. Copilot CLI runs use an outside-repository workspace, empty owned config
    homes, copied committed trace, safe child-environment allowlist, disabled remote
    export, and bounded process output, dynamic artifacts, and wall time. A per-run
    pre-tool hook validates task-derived literal filtrace argv and atomically
    consumes the call budget before the apphost can execute.
    The ollama model emits one action per turn (`RUN: <args>` or `ANSWER: <text>`),
    and the harness runs `filtrace <args>` on its behalf and feeds back the JSON.

  HOSTS: ollama (local, no metered API) and copilot (the GitHub Copilot CLI - the
  production target; metered, needs `copilot login`). claude is recognized but not
  yet wired - the runner reports that and exits cleanly.

    METRICS per (task, iteration): success (the final answer contains every
    `expect` substring, case-insensitive; every expected operation was called, no
    forbidden operation was; and the selected arm's required tool/model/skill
    evidence passed), calls (filtrace invocations), tokens
  (offline estimate of the tool output the agent consumed, via
  tools/Get-TokenEstimate.ps1 - the same accounting the deterministic gate
  uses), and wall-time. Results are written under eval/results/ and summarized
  as medians with a success rate.

  On the MCP arm the token figure is also broken down per response, because the
  same payload is currently carried twice on the wire (text content and structured
  content) and the transport experiment needs those measured apart: textTokens,
  structuredTokens, and wireTokens (the MCP result - the members the protocol
  defines). hostResultTokens is the whole object the host's transcript recorded,
  which is NOT the same thing: the Copilot CLI adds its own renderings
  (detailedContent, contents), so its transcript carries about four copies of a
  payload the server sent twice. Only wireTokens responds to a transport change.
  The client-visible value - what the host actually placed in the model's context -
  is recorded only when the host reports it; Copilot's transcript exposes session
  usage rather than per-call context, so hostUsage is captured instead of a
  fabricated per-call number.

.PARAMETER AgentHost
    The agent host. 'ollama' and 'copilot' are implemented; 'claude' is recognized
    but not yet wired.

.PARAMETER Arm
    Surface to evaluate. Defaults to cli for ollama and mcp for copilot. Copilot
    additionally supports strict experimental cli and cli-skill arms on Windows.

.PARAMETER Model
  The model name passed to the host. Defaults to 'gpt-oss:20b' for ollama; for
    the Copilot mcp arm, omit it to use the CLI default. Copilot cli and cli-skill
    require an explicit model and retain the separately observed host model.

.PARAMETER ExpectedModel
    Required for Copilot cli and cli-skill. The iteration cannot pass unless the
    host reports this exact model; the requested value is never substituted.

.PARAMETER Models
    Run the matrix across several models in one invocation. Overrides -Model; one
    result file is written per model. The strict Copilot CLI arms instead require one
    explicit -Model and -ExpectedModel pair.

.PARAMETER Label
  A label stamped into each result file's name and payload (e.g. baseline or
  candidate) so Compare-EvalRuns.ps1 can pair a candidate run against its baseline.

.PARAMETER Tasks
  Task ids to run (default: every task whose os matches this machine). Accepts
  the smoke subset, e.g. -Tasks cpu-hotspot,gc-report.

.PARAMETER N
    Iterations per task (the design's N), from 1 through 1,000. Defaults to 1.
    Summaries are descriptive; the runner does not claim statistical confidence.

.PARAMETER MaxSteps
    Per-attempt filtrace call budget (the design's G1). Defaults to 6. The Copilot
        strict CLI arms consume this budget before allowing execution, not only while
        grading the completed transcript.

.PARAMETER Configuration
  The build configuration whose CLI binary the agent drives. Defaults to Release.

.PARAMETER McpDll
  An explicit Filtrace.Mcp.dll to serve the mcp arm, overriding the Configuration
  build output. This is how a transport or surface variant published under
  artifacts/ is measured against the baseline without editing committed tasks.

.PARAMETER OllamaUrl
  The Ollama chat endpoint. Defaults to http://localhost:11434/api/chat.

.PARAMETER OutDir
    Where to write results. Strict run directories use this location only when it
    is outside the repository; otherwise they use an owned temporary root.

.PARAMETER CopilotPath
    Explicit native Copilot executable. Defaults to the first copilot.exe on PATH.

.PARAMETER NativeTimeoutSeconds
    Maximum wall time for one Copilot host process. Defaults to 600 seconds.

.PARAMETER MaxHostOutputBytes
    Maximum combined captured stdout and stderr bytes. Defaults to 10 MiB.

.PARAMETER MaxHostArtifactBytes
    Maximum dynamic bytes under one owned Copilot run directory, excluding the
    pre-attested immutable CLI, fixture, and skill inputs and the separately
    bounded Windows Copilot runtime cache. Defaults to 16 MiB.

.EXAMPLE
  ./eval/Invoke-AgentEval.ps1 -Tasks cpu-hotspot,gc-report -N 1
  A quick two-task sample against the default local model.
#>
[CmdletBinding()]
param(
    [ValidateSet('ollama', 'copilot', 'claude')]
    [string]$AgentHost = 'ollama',
    [ValidateSet('mcp', 'cli', 'cli-skill')]
    [string]$Arm,
    [string]$Model = 'gpt-oss:20b',
    [string]$ExpectedModel,
    [string[]]$Models,
    [string[]]$Tasks,
    [ValidateRange(1, 1000)]
    [int]$N = 1,
    [ValidateRange(1, 64)]
    [int]$MaxSteps = 6,
    [string]$Configuration = 'Release',
    [string]$McpDll,
    [string]$OllamaUrl = 'http://localhost:11434/api/chat',
    [string]$OutDir,
    [string]$Label,
    [string]$CopilotPath,
    [string]$CopilotAdapterPath,
    [ValidateRange(1, 600)]
    [int]$NativeTimeoutSeconds = 600,
    [ValidateRange(1024, 104857600)]
    [int]$MaxHostOutputBytes = 10485760,
    [ValidateRange(1024, 104857600)]
    [int]$MaxHostArtifactBytes = 16777216
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
. (Join-Path $PSScriptRoot 'CopilotEval.Helpers.ps1')

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

function Get-AgentEvalMediatedOperation([string] $ArgString) {
    [string] $clean = $ArgString.Trim().Trim('`').Trim()
    [string[]] $tokens = @(Split-ArgString -Text $clean)
    while ($tokens.Count -gt 0 -and $tokens[0] -match '^(?i)(filtrace(\.dll|\.exe)?|dotnet|\./filtrace)$') {
        $tokens = @($tokens | Select-Object -Skip 1)
    }
    if ($tokens.Count -eq 0) { return '' }
    [string] $operationSource = $tokens[0]
    if ([string]::Equals($operationSource, 'report', [StringComparison]::Ordinal)) {
        [int[]] $kindIndexes = @(for ($index = 0; $index -lt $tokens.Count; $index++) {
                if ([string]::Equals($tokens[$index], '--kind', [StringComparison]::Ordinal)) { $index }
            })
        if ($kindIndexes.Count -eq 1 -and $kindIndexes[0] -lt ($tokens.Count - 1)) {
            $operationSource = $tokens[$kindIndexes[0] + 1]
        }
    }
    return Get-OperationName -Name $operationSource
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
            $callTokens = [int](Get-TokenEstimate -Text $clip)
            $tokens += $callTokens
            $transcript.Add([pscustomobject]@{
                    kind = 'filtrace'
                    operation = Get-AgentEvalMediatedOperation -ArgString ([string]$action[1])
                    cmd = [string]$action[1]; ok = [bool]$res[0]; textTokens = $callTokens
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
        observedModel = [string]$script:CurrentModel
        observedModels = @([string]$script:CurrentModel)
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
# structuredContent, so summing them would double-count; they are reported apart.
# `wire` counts only the members the MCP protocol defines, because a transport
# change moves those and nothing else. `hostResult` counts the whole object the
# host recorded: the Copilot CLI adds `detailedContent` and `contents`, two further
# copies that never crossed the MCP boundary, so conflating the two overstates the
# duplication a transport variant can remove (measured 4x recorded against 2x real).
# `shape` records which members the host actually surfaced, since that is
# host-dependent and the measurement is only interpretable alongside it.
function Measure-McpResultTokens {
    param($Result)

    $text = 0
    $structured = 0
    $shape = 'none'

    if ($null -eq $Result) {
        return [pscustomobject]@{ text = 0; structured = 0; wire = 0; hostResult = 0; shape = $shape }
    }

    if ($Result -is [string]) {
        $text = [int](Get-TokenEstimate -Text $Result)
        return [pscustomobject]@{ text = $text; structured = 0; wire = $text; hostResult = $text; shape = 'text' }
    }

    $members = @($Result.PSObject.Properties.Name)
    $parts = [System.Collections.Generic.List[string]]::new()
    # Rebuild the protocol-defined result so `wire` measures what the server sent
    # rather than what the host chose to log around it.
    $canonical = [ordered]@{}
    if ($members -contains 'content') {
        $parts.Add('content')
        $canonical['content'] = $Result.content
        foreach ($block in @($Result.content)) {
            # Presence of `text`, not truthiness: an empty text block is legitimately
            # zero tokens, and treating it as absent would serialize the whole block
            # and count the wrapper as payload.
            $blockText = if ($block -is [string]) { $block } elseif ($null -ne $block.text) { [string]$block.text } else { ($block | ConvertTo-Json -Depth 24 -Compress) }
            $text += [int](Get-TokenEstimate -Text $blockText)
        }
    }
    if ($members -contains 'structuredContent' -and $null -ne $Result.structuredContent) {
        $parts.Add('structuredContent')
        $canonical['structuredContent'] = $Result.structuredContent
        $structured = [int](Get-TokenEstimate -Text ($Result.structuredContent | ConvertTo-Json -Depth 24 -Compress))
    }
    if ($members -contains 'isError') { $canonical['isError'] = $Result.isError }
    if ($parts.Count -gt 0) { $shape = $parts -join '+' } else { $shape = 'object' }

    $hostResult = [int](Get-TokenEstimate -Text ($Result | ConvertTo-Json -Depth 24 -Compress))
    # A host that flattens the result to one object exposes no protocol members to
    # separate, so the whole object is the best available reading of both.
    if ($shape -eq 'object') {
        return [pscustomobject]@{ text = $hostResult; structured = 0; wire = $hostResult; hostResult = $hostResult; shape = $shape }
    }

    $wire = [int](Get-TokenEstimate -Text (([pscustomobject]$canonical) | ConvertTo-Json -Depth 24 -Compress))
    return [pscustomobject]@{ text = $text; structured = $structured; wire = $wire; hostResult = $hostResult; shape = $shape }
}

function ConvertFrom-AgentEvalJsonLines([string[]] $Lines) {
    [System.Collections.Generic.List[object]] $events = [System.Collections.Generic.List[object]]::new()
    [System.Collections.Generic.HashSet[string]] $knownTypes =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($knownType in @(
        'session.start', 'session.info', 'session.managed_settings_resolved',
        'session.mcp_servers_loaded', 'session.skills_loaded',
        'session.tools_updated', 'session.background_tasks_changed', 'session.usage_checkpoint',
        'session.shutdown', 'system.message', 'user.message', 'hook.start', 'hook.end',
        'model.turn_started', 'model.call_start', 'model.model_call_started',
        'model.captured_assignment_context', 'model.model_call_success', 'model.call_finished',
        'model.message', 'model.messages_snapshot', 'model.tool_execution', 'model.response', 'model.turn_ended',
        'assistant.turn_start', 'assistant.message_start', 'assistant.tool_call_delta',
        'assistant.message_delta', 'assistant.message', 'assistant.reasoning',
        'assistant.idle', 'assistant.turn_end',
        'tool.execution_start', 'tool.execution_partial_result', 'tool.execution_complete', 'result')) {
        [void]$knownTypes.Add($knownType)
    }
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $record = $line | ConvertFrom-Json }
        catch { throw "Copilot host emitted malformed JSONL: $($_.Exception.Message)" }
        [string[]] $members = @($record.PSObject.Properties.Name)
        if (@($members | Where-Object {
                    [string]::Equals($_, 'type', [StringComparison]::Ordinal)
                }).Count -ne 1 -or $record.type -isnot [string] -or
            -not $knownTypes.Contains([string]$record.type)) {
            throw "Copilot host emitted an unknown event type '$($record.type)'."
        }
        $events.Add($record)
    }
    if ($events.Count -eq 0) { throw 'Copilot host emitted no JSONL events.' }
    return $events
}

function Get-AgentEvalLiteralCommand($Arguments, $Context, $ExecutionPolicy) {
    if ($null -eq $Arguments) { return $null }
    [string[]] $argumentMembers = @($Arguments.PSObject.Properties.Name)
    [System.Collections.Generic.HashSet[string]] $allowedArgumentMembers =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($member in @('command', 'description', 'mode', 'initial_wait')) {
        [void]$allowedArgumentMembers.Add($member)
    }
    [System.Collections.Generic.HashSet[string]] $actualArgumentMembers =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($member in $argumentMembers) { [void]$actualArgumentMembers.Add($member) }
    if ($argumentMembers.Count -lt 2 -or $argumentMembers.Count -gt $allowedArgumentMembers.Count -or
        @($argumentMembers | Where-Object { -not $allowedArgumentMembers.Contains($_) }).Count -ne 0 -or
        -not $actualArgumentMembers.Contains('command') -or
        -not $actualArgumentMembers.Contains('description') -or
        $Arguments.command -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Arguments.command) -or
        $Arguments.description -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string]$Arguments.description) -or
        $Arguments.description.Length -gt 256 -or
        @($Arguments.description.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0) {
        return $null
    }
    if ($actualArgumentMembers.Contains('mode') -and
        ($Arguments.mode -isnot [string] -or
        -not [string]::Equals([string]$Arguments.mode, 'sync', [StringComparison]::Ordinal))) {
        return $null
    }
    if ($actualArgumentMembers.Contains('initial_wait') -and
        (($Arguments.initial_wait -isnot [int] -and $Arguments.initial_wait -isnot [long]) -or
        [long]$Arguments.initial_wait -lt 1 -or [long]$Arguments.initial_wait -gt 30)) {
        return $null
    }

    [System.Management.Automation.Language.Token[]] $commandTokens = $null
    [System.Management.Automation.Language.ParseError[]] $parseErrors = $null
    [System.Management.Automation.Language.ScriptBlockAst] $ast =
        [System.Management.Automation.Language.Parser]::ParseInput(
            [string]$Arguments.command, [ref]$commandTokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0 -or $null -eq $ast.EndBlock -or
        -not $ast.EndBlock.Unnamed -or $ast.EndBlock.Statements.Count -ne 1 -or
        $null -ne $ast.EndBlock.Traps -or $null -ne $ast.BeginBlock -or
        $null -ne $ast.ProcessBlock -or $null -ne $ast.CleanBlock -or
        $null -ne $ast.DynamicParamBlock -or $null -ne $ast.ParamBlock -or
        $null -ne $ast.ScriptRequirements -or $ast.UsingStatements.Count -ne 0 -or
        $ast.Attributes.Count -ne 0) { return $null }
    $statement = $ast.EndBlock.Statements[0]
    if ($statement -isnot [System.Management.Automation.Language.PipelineAst] -or
        $statement.PipelineElements.Count -ne 1) {
        return $null
    }
    $command = $statement.PipelineElements[0]
    if ($command -isnot [System.Management.Automation.Language.CommandAst] -or
        $command.InvocationOperator -ne [System.Management.Automation.Language.TokenKind]::Ampersand -or
        $command.Redirections.Count -ne 0 -or $command.CommandElements.Count -lt 2) {
        return $null
    }
    [System.Collections.Generic.List[string]] $values = [System.Collections.Generic.List[string]]::new()
    foreach ($element in $command.CommandElements) {
        if ($element -isnot [System.Management.Automation.Language.StringConstantExpressionAst] -or
            $element.StringConstantType -ne [System.Management.Automation.Language.StringConstantType]::SingleQuoted) {
            return $null
        }
        $values.Add([string]$element.Value)
    }
    [string] $executable = try { [System.IO.Path]::GetFullPath($values[0]) } catch { return $null }
    [string] $expectedExecutable = [System.IO.Path]::GetFullPath($Context.cliPath)
    if (-not [string]::Equals($executable, $expectedExecutable, [StringComparison]::Ordinal)) { return $null }
    [string[]] $argv = @($values | Select-Object -Skip 1)
    if ($argv.Count -eq 1 -and
        [string]::Equals($argv[0], '--help', [StringComparison]::Ordinal)) {
        return [pscustomobject]@{
            command = [string]$Arguments.command
            verb = 'help'
            operation = 'help'
            isHelp = $true
            argv = $argv
        }
    }
    if ($argv.Count -eq 2 -and
        [string]::Equals($argv[1], '--help', [StringComparison]::Ordinal)) {
        $helpFamily = @($ExecutionPolicy.commandFamilies | Where-Object {
                [string]::Equals([string]$_.verb, $argv[0], [StringComparison]::Ordinal)
            })
        if ($helpFamily.Count -ne 1) { return $null }
        return [pscustomobject]@{
            command = [string]$Arguments.command
            verb = $argv[0]
            operation = 'help'
            isHelp = $true
            argv = $argv
        }
    }
    [string] $verb = $argv[0]
    if (-not @($verbs | Where-Object { [string]::Equals($_, $verb, [StringComparison]::Ordinal) }).Count) { return $null }
    if ($argv.Count -lt 4 -or
        -not [string]::Equals($argv[1], $Context.fixturePath, [StringComparison]::Ordinal)) {
        return $null
    }
    [int[]] $fixtureIndexes = @(for ($index = 0; $index -lt $argv.Count; $index++) {
            if ([string]::Equals($argv[$index], $Context.fixturePath, [StringComparison]::Ordinal)) { $index }
        })
    [int[]] $formatIndexes = @(for ($index = 0; $index -lt $argv.Count; $index++) {
            if ([string]::Equals($argv[$index], '--format', [StringComparison]::Ordinal)) { $index }
        })
    if ($fixtureIndexes.Count -ne 1 -or $formatIndexes.Count -ne 1 -or
        $formatIndexes[0] -ge ($argv.Count - 1) -or
        -not [string]::Equals($argv[$formatIndexes[0] + 1], 'json', [StringComparison]::Ordinal)) {
        return $null
    }
    [string] $operation = Get-OperationName -Name $verb
    if ($verb -eq 'report') {
        [int[]] $kindIndexes = @(for ($index = 0; $index -lt $argv.Count; $index++) {
                if ([string]::Equals($argv[$index], '--kind', [StringComparison]::Ordinal)) { $index }
            })
        if ($kindIndexes.Count -ne 1 -or $kindIndexes[0] -ge ($argv.Count - 1) -or
            $argv[$kindIndexes[0] + 1] -notin @('gc', 'jit', 'threadpool', 'diskio')) {
            return $null
        }
        $operation = Get-OperationName -Name $argv[$kindIndexes[0] + 1]
    }
    return [pscustomobject]@{
        command = [string]$Arguments.command
        verb = $verb
        operation = $operation
        isHelp = $false
        argv = $argv
    }
}

function Test-AgentEvalCliHelpResult($Result) {
    [string] $resultText = Get-AgentEvalPowerShellResultText $Result
    return -not [string]::IsNullOrWhiteSpace($resultText) -and
        $resultText.Contains('Usage:', [StringComparison]::Ordinal)
}

function Test-AgentEvalCliResult($Result, [string] $ExpectedOperation) {
    [string] $resultText = Get-AgentEvalPowerShellResultText $Result
    if ([string]::IsNullOrWhiteSpace($resultText)) { return $false }
    try { $payload = $resultText | ConvertFrom-Json }
    catch { return $false }
    [string[]] $members = @($payload.PSObject.Properties.Name)
    if ($members -notcontains 'schemaVersion' -or $payload.schemaVersion -notin @(17, 17L) -or
        $members -notcontains 'context' -or $null -eq $payload.context -or
        @($payload.context.PSObject.Properties.Name) -notcontains 'operation' -or
        $payload.context.operation -isnot [string] -or
        -not [string]::Equals([string]$payload.context.operation, $ExpectedOperation, [StringComparison]::Ordinal) -or
        $members -notcontains 'result' -or $null -eq $payload.result) {
        return $false
    }
    return $true
}

function Test-AgentEvalHostUsage($Usage) {
    if ($null -eq $Usage -or $Usage -is [string] -or $Usage -is [ValueType]) { return $false }
    [string[]] $members = @($Usage.PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($member in @('premiumRequests', 'totalApiDurationMs', 'sessionDurationMs')) {
        if ($members -notcontains $member) { return $false }
    }
    [bool] $premiumRequestsTypeValid = $Usage.premiumRequests -is [byte] -or
        $Usage.premiumRequests -is [sbyte] -or
        $Usage.premiumRequests -is [short] -or
        $Usage.premiumRequests -is [ushort] -or
        $Usage.premiumRequests -is [int] -or
        $Usage.premiumRequests -is [uint] -or
        $Usage.premiumRequests -is [long] -or
        $Usage.premiumRequests -is [ulong] -or
        $Usage.premiumRequests -is [float] -or
        $Usage.premiumRequests -is [double] -or
        $Usage.premiumRequests -is [decimal]
    [double] $premiumRequests = if ($premiumRequestsTypeValid) { [double]$Usage.premiumRequests } else { -1 }
    if (-not $premiumRequestsTypeValid -or $premiumRequests -lt 0 -or
        [double]::IsNaN($premiumRequests) -or [double]::IsInfinity($premiumRequests)) {
        return $false
    }
    foreach ($member in @('totalApiDurationMs', 'sessionDurationMs')) {
        if ($members -notcontains $member -or
            ($Usage.$member -isnot [int] -and $Usage.$member -isnot [long]) -or
            [long]$Usage.$member -lt 0) {
            return $false
        }
    }
    return $true
}

function Get-AgentEvalTaskExpectedOperations($Task) {
    [System.Collections.Generic.HashSet[string]] $operations =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($step in @($Task.steps)) {
        [string[]] $arguments = @($step.args)
        if ($arguments.Count -eq 0) { throw "Task '$($Task.id)' has an empty command step." }
        [string] $operationSource = $arguments[0]
        if ([string]::Equals($operationSource, 'report', [StringComparison]::Ordinal)) {
            [int[]] $kindIndexes = @(for ($index = 0; $index -lt $arguments.Count; $index++) {
                    if ([string]::Equals($arguments[$index], '--kind', [StringComparison]::Ordinal)) { $index }
                })
            if ($kindIndexes.Count -ne 1 -or $kindIndexes[0] -ge ($arguments.Count - 1)) {
                throw "Task '$($Task.id)' has a report step without exactly one kind."
            }
            $operationSource = $arguments[$kindIndexes[0] + 1]
        }
        [void]$operations.Add((Get-OperationName -Name $operationSource))
    }
    return [string[]]@($operations)
}

function Get-AgentEvalStrictRunProjection {
    param(
        [Parameter(Mandatory)][object[]] $Tasks,
        [Parameter(Mandatory)][int] $Iterations,
        [Parameter(Mandatory)][string] $Arm,
        [Parameter(Mandatory)][string] $Configuration,
        [Parameter(Mandatory)][long] $MaxArtifactBytes
    )

    [string] $appHostName = if ([System.OperatingSystem]::IsWindows()) { 'filtrace.exe' } else { 'filtrace' }
    [string] $cliSourceDirectory = Join-Path $root "src/Filtrace/bin/$Configuration/net10.0"
    if (-not (Test-Path -LiteralPath (Join-Path $cliSourceDirectory $appHostName) -PathType Leaf)) {
        throw "Current-checkout filtrace apphost was not built beneath '$cliSourceDirectory'."
    }
    $runtimeInventory = Get-AgentEvalFileInventory `
        -SourceDirectory $cliSourceDirectory `
        -DestinationPrefix '' `
        -ExcludedExtensions @('.pdb', '.xml') `
        -MaxFiles 256 `
        -MaxEntries 512 `
        -MaxBytes 512MB
    [string] $architectureDirectoryName = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        ([System.Runtime.InteropServices.Architecture]::X64) { 'amd64' }
        ([System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' }
        default { throw "Unsupported eval process architecture '$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)'." }
    }
    $architectureInventory = Get-AgentEvalFileInventory `
        -SourceDirectory (Join-Path $cliSourceDirectory $architectureDirectoryName) `
        -DestinationPrefix $architectureDirectoryName `
        -Recurse `
        -MaxFiles 256 `
        -MaxEntries 512 `
        -MaxBytes 512MB
    [long] $immutableBytes = [long]$runtimeInventory.bytes + [long]$architectureInventory.bytes
    if ($Arm -eq 'cli-skill') {
        $skillInventory = Get-AgentEvalFileInventory `
            -SourceDirectory (Join-Path $root '.agents/skills/filtrace') `
            -DestinationPrefix '' `
            -Recurse `
            -MaxFiles 64 `
            -MaxEntries 128 `
            -MaxBytes 16MB
        $immutableBytes += [long]$skillInventory.bytes
    }

    [long] $projectedBytes = 0
    foreach ($task in $Tasks) {
        [string] $fixturePath = Assert-AgentEvalTrackedFixture `
            -Root $root `
            -FixturePath (Join-Path $root $task.fixture)
        [long] $iterationBytes = $immutableBytes + (Get-Item -LiteralPath $fixturePath).Length +
            [long]$MaxArtifactBytes + 256MB + 1MB
        $projectedBytes += $iterationBytes * [long]$Iterations
    }
    return [pscustomobject]@{
        projectedBytes = $projectedBytes
        maxBytes = 2GB
    }
}

function Test-AgentEvalStringMultiset([string[]] $Left, [string[]] $Right) {
    if ($Left.Count -ne $Right.Count) { return $false }
    [string[]] $leftSorted = @($Left | Sort-Object)
    [string[]] $rightSorted = @($Right | Sort-Object)
    for ($index = 0; $index -lt $leftSorted.Count; $index++) {
        if (-not [string]::Equals($leftSorted[$index], $rightSorted[$index], [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    return $true
}

function Test-AgentEvalPolicyDeniedCompletion($Completion) {
    if ($null -eq $Completion -or
        @($Completion.PSObject.Properties.Name) -notcontains 'data' -or
        $null -eq $Completion.data) {
        return $false
    }
    [string[]] $members = @($Completion.data.PSObject.Properties.Name)
    if ($members -notcontains 'success' -or $Completion.data.success -isnot [bool] -or
        $Completion.data.success -or $members -notcontains 'error' -or
        $null -eq $Completion.data.error) {
        return $false
    }
    [string[]] $errorMembers = @($Completion.data.error.PSObject.Properties.Name)
    return $errorMembers -contains 'code' -and $Completion.data.error.code -is [string] -and
        [string]::Equals([string]$Completion.data.error.code, 'denied', [StringComparison]::Ordinal) -and
        $errorMembers -contains 'message' -and $Completion.data.error.message -is [string] -and
        ([string]$Completion.data.error.message).StartsWith(
            'Denied by preToolUse hook:', [StringComparison]::Ordinal)
}

function Write-AgentEvalNewFile([string] $Path, [byte[]] $Bytes) {
    [System.IO.FileStream] $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length) }
    finally { $stream.Dispose() }
}

function ConvertTo-AgentEvalResultJson($Payload) {
    return [string]($Payload | ConvertTo-Json -Depth 32 -WarningAction Stop)
}

function Save-AgentEvalHostOutput($ProcessResult, $Context, [long] $MaxArtifactBytes) {
    if (-not (Test-AgentEvalPathContained -Path $Context.logDirectory -Root $Context.runDirectory)) {
        throw 'Copilot host output directory escaped the owned run directory.'
    }
    [System.Text.Encoding] $utf8 = [System.Text.UTF8Encoding]::new($false)
    [byte[]] $stdoutBytes = $utf8.GetBytes([string]$ProcessResult.stdoutText)
    [byte[]] $stderrBytes = $utf8.GetBytes([string]$ProcessResult.stderrText)
    [long] $projectedBytes = [long]$stdoutBytes.Length + [long]$stderrBytes.Length
    [long] $remainingBytes = $MaxArtifactBytes - [long]$ProcessResult.artifactBytes
    if ($remainingBytes -lt 0) { throw 'Copilot host artifacts already exceeded the output retention budget.' }

    [string] $stdoutPath = Join-Path $Context.logDirectory 'host-stdout.jsonl'
    [string] $stderrPath = Join-Path $Context.logDirectory 'host-stderr.log'
    [string] $diagnosticPath = Join-Path $Context.logDirectory 'host-output-not-retained.txt'
    foreach ($path in @($stdoutPath, $stderrPath, $diagnosticPath)) {
        if (Test-Path -LiteralPath $path) { throw "Copilot host output path '$path' already exists." }
    }

    [bool] $retained = $projectedBytes -le $remainingBytes
    if ($retained) {
        Write-AgentEvalNewFile -Path $stdoutPath -Bytes $stdoutBytes
        Write-AgentEvalNewFile -Path $stderrPath -Bytes $stderrBytes
    }
    else {
        [byte[]] $diagnosticBytes = $utf8.GetBytes(
            "Host output was not retained: stdout=$($stdoutBytes.Length) bytes; stderr=$($stderrBytes.Length) bytes.`n")
        if ($diagnosticBytes.Length -gt $remainingBytes) {
            [byte[]] $boundedBytes = [byte[]]::new([int]$remainingBytes)
            if ($boundedBytes.Length -gt 0) {
                [Array]::Copy($diagnosticBytes, $boundedBytes, $boundedBytes.Length)
            }
            $diagnosticBytes = $boundedBytes
        }
        Write-AgentEvalNewFile -Path $diagnosticPath -Bytes $diagnosticBytes
    }

    $usage = Get-AgentEvalCopilotUsage `
        -Context $Context `
        -MaxArtifactBytes $MaxArtifactBytes
    Assert-AgentEvalContextIntegrity $Context
    return [pscustomobject]@{
        retained = $retained
        stdoutPath = if ($retained) { $stdoutPath } else { $null }
        stderrPath = if ($retained) { $stderrPath } else { $null }
        diagnosticPath = if ($retained) { $null } else { $diagnosticPath }
        stdoutBytes = $stdoutBytes.Length
        stderrBytes = $stderrBytes.Length
        artifactBytes = $usage.artifacts.bytes
    }
}

# Run one bounded iteration on the Copilot CLI host and parse its JSONL transcript.
# The selected arm supplies MCP tools, the owned CLI bundle, or that CLI plus the
# copied skill. Calls count only matched filtrace invocations; token estimates cover
# observed tool results, while host-reported session usage remains separate.
function Invoke-CopilotIteration {
    param([object]$Task, [string]$FixtureAbs, [string]$McpConfig)
    $context = New-CopilotEvalContext `
        -Root $root `
        -OutDir $OutDir `
        -Arm $arm `
        -FixturePath $FixtureAbs `
        -Configuration $Configuration `
        -StrictCliArm:($arm -ne 'mcp')
    $executionPolicy = if ($arm -in @('cli', 'cli-skill')) {
        Initialize-CopilotEvalExecutionPolicy `
            -Context $context `
            -Task $Task `
            -AllowedVerbs $verbs `
            -MaxCalls $MaxSteps `
            -HookSourcePath (Join-Path $PSScriptRoot 'CopilotEval.PolicyHook.ps1')
    }
    else { $null }
    $effectiveFixture = $context.fixturePath
    $surface = if ($arm -eq 'mcp') { 'the filtrace MCP tools' } else { "the local filtrace CLI at $($context.cliPath)" }
    $commandInstruction = if ($arm -in @('cli', 'cli-skill')) {
        " Use one literal & '<filtrace-path>' '--help' command when you need to discover commands. Invoke analysis as & '<filtrace-path>' '<verb>' '<trace-path>' ending '--format' 'json'. Keep every token single-quoted and use no pipeline, redirection, assignment, interpolation, or additional expression."
    }
    else { '' }
    $prompt = "Using $surface, analyze the .NET trace at $effectiveFixture and answer the question.$commandInstruction Base your answer only on successful tool output; do not guess or run builds, tests, package managers, network tools, or any executable other than the provided filtrace CLI. Question: $($Task.prompt)"
    $cmdArgs = @(
        '-C', $context.workspace,
        '-p', $prompt,
        '--output-format', 'json',
        '--disable-builtin-mcps',
        '--no-remote-export',
        '--no-remote',
        '--no-auto-update',
        '--no-bash-env',
        '--no-ask-user',
        '--session-id', $context.runId,
        '--usage-output-file', $context.usagePath,
        '--log-dir', $context.logDirectory
    )
    if ($arm -eq 'mcp') {
        $cmdArgs += @('--allow-all', '--no-custom-instructions', '--additional-mcp-config', "@$McpConfig")
    }
    else {
        [string[]] $availableToolNames = if ($arm -eq 'cli-skill') { @('powershell', 'skill') } else { @('powershell') }
        [string[]] $observedBuiltinTools = @(
            'powershell', 'read_powershell', 'stop_powershell', 'list_powershell', 'apply_patch', 'view',
            'web_fetch', 'fetch_copilot_cli_documentation', 'skill', 'sql', 'session_store_sql',
            'read_agent', 'list_agents', 'write_agent', 'rg', 'glob', 'task')
        [string[]] $excludedToolNames = @($observedBuiltinTools | Where-Object { $availableToolNames -notcontains $_ })
        $cmdArgs += @(
            '--available-tools', ($availableToolNames -join ','),
            '--excluded-tools', ($excludedToolNames -join ','),
            '--deny-tool', 'read,shell,write,url',
            '--disallow-temp-dir',
            '--max-ai-credits', '30'
        )
        if ($arm -eq 'cli') { $cmdArgs += '--no-custom-instructions' }
    }
    if ($script:CurrentModel) { $cmdArgs += @('--model', $script:CurrentModel) }
    $processArgs = if ($CopilotAdapterPath) { @('-File', $CopilotAdapterPath) + $cmdArgs } else { $cmdArgs }
    $processEnvironment = @{}
    if ($CopilotAdapterPath) {
        $processEnvironment['FILTRACE_AGENT_EVAL_FAKE_MODE'] =
            [System.Environment]::GetEnvironmentVariable('FILTRACE_AGENT_EVAL_FAKE_MODE')
    }
    $processResult = Invoke-BoundedCopilotProcess `
        -FilePath $CopilotPath `
        -Arguments $processArgs `
        -Context $context `
        -TimeoutSeconds $NativeTimeoutSeconds `
        -MaxOutputBytes $MaxHostOutputBytes `
        -MaxArtifactBytes $MaxHostArtifactBytes `
        -Environment $processEnvironment
    $outputPersistence = $null
    [System.Exception] $outputPersistenceError = $null
    try {
        $outputPersistence = Save-AgentEvalHostOutput `
            -ProcessResult $processResult `
            -Context $context `
            -MaxArtifactBytes $MaxHostArtifactBytes
        $processResult.artifactBytes = $outputPersistence.artifactBytes
    }
    catch {
        $outputPersistenceError = $_.Exception
    }
    $executionPolicyState = if ($executionPolicy) {
        Get-CopilotEvalExecutionPolicyState -ExecutionPolicy $executionPolicy
    }
    else { $null }
    $usageFile = Get-AgentEvalHostUsageFile -Context $context
    $out = $processResult.stdout
    $copilotExitCode = $processResult.exitCode
    $events = @(ConvertFrom-AgentEvalJsonLines $out)
    if ($null -ne $outputPersistenceError) {
        throw [System.InvalidOperationException]::new(
            "Copilot host output could not be retained: $($outputPersistenceError.Message)",
            $outputPersistenceError)
    }
    # Mask the fixture path back to <TRACE> in both raw and JSON-escaped (\\) forms,
    # since arguments are serialized with ConvertTo-Json (which doubles backslashes).
    $jsonPath = $effectiveFixture.Replace('\', '\\')
    $jsonWorkspace = $context.workspace.Replace('\', '\\')
    $mask = {
        param($t)
        ([string]$t).Replace($jsonPath, '<TRACE>').Replace($effectiveFixture, '<TRACE>').
            Replace($jsonWorkspace, '<WORKSPACE>').Replace($context.workspace, '<WORKSPACE>')
    }
    $answerEvent = $null
    [int] $answerEventIndex = -1
    [int] $skillContextEventIndex = -1
    [System.Collections.Generic.Dictionary[string, int]] $startEventIndexes =
        [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    [System.Collections.Generic.Dictionary[string, int]] $completionEventIndexes =
        [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    for ($eventIndex = 0; $eventIndex -lt $events.Count; $eventIndex++) {
        $event = $events[$eventIndex]
        if ($event.PSObject.Properties.Name -contains 'type' -and
            $event.type -eq 'assistant.message' -and
            $event.PSObject.Properties.Name -contains 'data') {
            $answerEvent = $event
            $answerEventIndex = $eventIndex
        }
        if ($event.PSObject.Properties.Name -contains 'type' -and
            $event.type -eq 'model.message' -and
            $event.PSObject.Properties.Name -contains 'data' -and
            $event.data.message.role -eq 'user' -and
            $event.data.message.content -is [string] -and
            ([string]$event.data.message.content).StartsWith(
                '<skill-context name="filtrace">', [StringComparison]::Ordinal)) {
            $skillContextEventIndex = $eventIndex
        }
        if ($event.PSObject.Properties.Name -contains 'type' -and
            $event.type -eq 'tool.execution_start' -and
            $event.PSObject.Properties.Name -contains 'data' -and
            $event.data.PSObject.Properties.Name -contains 'toolCallId' -and
            $event.data.toolCallId -is [string] -and
            -not [string]::IsNullOrWhiteSpace([string]$event.data.toolCallId)) {
            [void]$startEventIndexes.TryAdd([string]$event.data.toolCallId, $eventIndex)
        }
        if ($event.PSObject.Properties.Name -contains 'type' -and
            $event.type -eq 'tool.execution_complete' -and
            $event.PSObject.Properties.Name -contains 'data' -and
            $event.data.PSObject.Properties.Name -contains 'toolCallId' -and
            $event.data.toolCallId -is [string] -and
            -not [string]::IsNullOrWhiteSpace([string]$event.data.toolCallId)) {
            [void]$completionEventIndexes.TryAdd([string]$event.data.toolCallId, $eventIndex)
        }
    }
    $answer = if ($answerEvent -and $answerEvent.data.PSObject.Properties.Name -contains 'content') {
        & $mask ([string]$answerEvent.data.content)
    }
    else {
        $null
    }
    $starts = if ($arm -eq 'mcp') {
        @($events | Where-Object {
            $_.PSObject.Properties.Name -contains 'type' -and $_.type -eq 'tool.execution_start' -and
            $_.PSObject.Properties.Name -contains 'data' -and
            $_.data.PSObject.Properties.Name -contains 'mcpServerName' -and
            $_.data.mcpServerName -eq 'filtrace'
        })
    }
    else {
        @($events | Where-Object {
            $_.PSObject.Properties.Name -contains 'type' -and $_.type -eq 'tool.execution_start' -and
            $_.PSObject.Properties.Name -contains 'data'
        })
    }
    $starts = @($starts)
    $allCompletes = @($events | Where-Object {
        $_.PSObject.Properties.Name -contains 'type' -and $_.type -eq 'tool.execution_complete' -and
        $_.PSObject.Properties.Name -contains 'data'
    })
    $completes = if ($arm -eq 'mcp') {
        [string[]] $mcpCallIds = @($starts | ForEach-Object { [string]$_.data.toolCallId })
        @($allCompletes | Where-Object { $mcpCallIds -contains [string]$_.data.toolCallId })
    }
    else {
        $allCompletes
    }
    [System.Collections.Generic.HashSet[string]] $startIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [System.Collections.Generic.HashSet[string]] $completionIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [bool] $evidenceValid = $true
    foreach ($start in $starts) {
        [string] $startId = if (@($start.data.PSObject.Properties.Name) -contains 'toolCallId') { [string]$start.data.toolCallId } else { '' }
        if ([string]::IsNullOrWhiteSpace($startId) -or -not $startIds.Add($startId)) {
            $evidenceValid = $false
        }
        else {
            [int] $startEventIndex = -1
            [int] $completionEventIndex = -1
            if (-not $startEventIndexes.TryGetValue($startId, [ref]$startEventIndex) -or
                -not $completionEventIndexes.TryGetValue($startId, [ref]$completionEventIndex) -or
                $startEventIndex -ge $completionEventIndex) {
                $evidenceValid = $false
            }
        }
    }
    foreach ($complete in $completes) {
        [string] $completionId = if (@($complete.data.PSObject.Properties.Name) -contains 'toolCallId') { [string]$complete.data.toolCallId } else { '' }
        if ([string]::IsNullOrWhiteSpace($completionId) -or -not $completionIds.Add($completionId) -or
            -not $startIds.Contains($completionId)) {
            $evidenceValid = $false
        }
    }
    if ($startIds.Count -ne $completionIds.Count) { $evidenceValid = $false }
    $tokens = 0
    $textTokens = 0
    $structuredTokens = 0
    $wireTokens = 0
    $hostResultTokens = 0
    $resultShapes = [System.Collections.Generic.List[string]]::new()
    $transcript = [System.Collections.Generic.List[object]]::new()
    $filtraceCalls = 0
    $helpCalls = 0
    $skillEvidence = [ordered]@{
        provided = [bool]($arm -eq 'cli-skill')
        sourcePath = (Join-Path $root '.agents/skills/filtrace')
        installedPath = $context.skillPath
        sha256 = $context.skillSha256
        sourceByteSha256 = $context.skillSha256
        sourceTextSha256 = $null
        sourceContextSha256 = $null
        textNormalization = $null
        inventory = $context.skillInventory
        observed = $false
        observedSha256 = $null
        observedTextSha256 = $null
        verified = $false
        failure = $null
        evidenceCallIds = @()
        sourceBytes = $null
        sourceChars = $null
        sourceLineCount = $null
        sourceTerminalNewline = $null
        observedChars = 0
        returnedPayloadChars = 0
        terminalNewlineOmissions = 0
        requestedBytes = 0
        reads = @()
        discovery = $null
    }
    [System.Collections.Generic.List[string]] $transcriptCommandHashes =
        [System.Collections.Generic.List[string]]::new()
    [System.Collections.Generic.List[string]] $transcriptViewRequestHashes =
        [System.Collections.Generic.List[string]]::new()
    [System.Collections.Generic.List[int]] $successfulAnalysisCompletionIndexes =
        [System.Collections.Generic.List[int]]::new()
    $skillSource = if ($context.skillPath) { Get-AgentEvalSkillSource $context.skillPath } else { $null }
    foreach ($s in $starts) {
        $startMembers = @($s.data.PSObject.Properties.Name)
        $callId = if ($startMembers -contains 'toolCallId') { [string]$s.data.toolCallId } else { '' }
        $c = if ($callId) {
            $completes | Where-Object {
                $_.data.PSObject.Properties.Name -contains 'toolCallId' -and $_.data.toolCallId -eq $callId
            } | Select-Object -First 1
        }
        else {
            $null
        }
        $completionMembers = if ($c) { @($c.data.PSObject.Properties.Name) } else { @() }
        $completionResult = if ($completionMembers -contains 'result') { $c.data.result } else { $null }
        $completionSucceeded = $completionMembers -contains 'success' -and
            $c.data.success -is [bool] -and $c.data.success
        $completionDeniedByPolicy = Test-AgentEvalPolicyDeniedCompletion $c
        $split = Measure-McpResultTokens -Result $completionResult
        # The headline metric stays "what the agent consumed" - the text copy - so it
        # remains comparable with the cli arm and with existing baselines.
        $tokens += $split.text
        $textTokens += $split.text
        $structuredTokens += $split.structured
        $wireTokens += $split.wire
        $hostResultTokens += $split.hostResult
        if (-not $resultShapes.Contains($split.shape)) { $resultShapes.Add($split.shape) }
        $commandName = ''
        $commandText = ''
        $operationName = ''
        $evidenceKind = 'other'
        if ($arm -ne 'mcp') {
            $arguments = if ($startMembers -contains 'arguments') { $s.data.arguments } else { $null }
            [string] $toolName = if ($startMembers -contains 'toolName') { [string]$s.data.toolName } else { '' }
                $literalCommand = if ([string]::Equals(
                    $toolName, 'powershell', [StringComparison]::Ordinal)) {
                Get-AgentEvalLiteralCommand `
                    -Arguments $arguments `
                    -Context $context `
                    -ExecutionPolicy $executionPolicy
            }
            else {
                $null
            }
            $isFiltraceCall = $null -ne $literalCommand
            if ($isFiltraceCall) {
                $commandName = if ($literalCommand.isHelp) { 'help' } else { $literalCommand.verb }
                $operationName = $literalCommand.operation
                if ($completionDeniedByPolicy) {
                    $evidenceKind = 'denied'
                    $isFiltraceCall = $false
                }
                else {
                    $transcriptCommandHashes.Add((Get-AgentEvalTextHash $literalCommand.command))
                    if ($literalCommand.isHelp) {
                        $evidenceKind = 'help'
                        $helpCalls++
                        if (-not $completionSucceeded -or
                            -not (Test-AgentEvalCliHelpResult -Result $completionResult)) {
                            $evidenceValid = $false
                        }
                    }
                    else {
                        $evidenceKind = 'filtrace'
                        $filtraceCalls++
                    }
                    if (-not $literalCommand.isHelp -and (-not $completionSucceeded -or
                        -not (Test-AgentEvalCliResult -Result $completionResult -ExpectedOperation $literalCommand.operation))) {
                        $evidenceValid = $false
                    }
                }
            }
            elseif ($arm -eq 'cli-skill' -and
                [string]::Equals($toolName, 'view', [StringComparison]::Ordinal) -and
                $context.skillPath) {
                try {
                    $viewRequest = Get-AgentEvalSkillViewRequest -Arguments $arguments -Source $skillSource
                    if (-not $completionSucceeded) { throw 'Skill view completion was not successful.' }
                    [void](Get-AgentEvalSkillViewPayload `
                            -Result $completionResult `
                            -Request $viewRequest `
                            -Source $skillSource)
                    $evidenceKind = 'skill-read'
                    $commandName = 'view'
                    $operationName = 'view'
                    $transcriptViewRequestHashes.Add($viewRequest.hash)
                }
                catch {
                    $evidenceValid = $false
                }
            }
            elseif ($arm -eq 'cli-skill' -and
                [string]::Equals($toolName, 'skill', [StringComparison]::Ordinal)) {
                $evidenceKind = 'skill'
                $commandName = 'skill'
                $operationName = 'skill'
                if (-not $completionSucceeded) { $evidenceValid = $false }
            }
            elseif ($completionDeniedByPolicy -and
                ([string]::Equals($toolName, 'powershell', [StringComparison]::Ordinal) -or
                    [string]::Equals($toolName, 'view', [StringComparison]::Ordinal))) {
                $evidenceKind = 'denied'
                $commandName = if ($toolName) { $toolName } else { 'unknown' }
                $operationName = Get-OperationName -Name $commandName
            }
            else {
                $evidenceValid = $false
            }
            if (-not $commandName) {
                $commandName = if ($toolName) { $toolName } else { 'unknown' }
            }
            [string] $argumentText = if ($null -ne $arguments) { [string]($arguments | ConvertTo-Json -Depth 12 -Compress) } else { '' }
            $commandText = "$commandName $argumentText"
        }
        else {
            $isFiltraceCall = $true
            $evidenceKind = 'filtrace'
            $filtraceCalls++
            $commandName = [string]$s.data.mcpToolName
            $operationName = Get-OperationName -Name $commandName
            $commandText = "$commandName $($s.data.arguments | ConvertTo-Json -Compress)"
        }
        if ($evidenceKind -eq 'filtrace' -and $completionSucceeded) {
            [int] $completionEventIndex = -1
            if (-not $completionEventIndexes.TryGetValue($callId, [ref]$completionEventIndex)) {
                $evidenceValid = $false
            }
            else {
                $successfulAnalysisCompletionIndexes.Add($completionEventIndex)
            }
        }
        $transcript.Add([pscustomobject]@{
                callId = $callId
                kind = $evidenceKind
                operation = $operationName
                cmd = & $mask $commandText
                ok = [bool]($isFiltraceCall -and $completionSucceeded)
                info = ''
                textTokens = $split.text; structuredTokens = $split.structured
                wireTokens = $split.wire; hostResultTokens = $split.hostResult
            })
    }
    if ($arm -eq 'cli-skill') {
        $completedSkillEvidence = Complete-AgentEvalDiscoveredSkillEvidence `
            -SourcePath $context.skillPath `
            -Events $events
        $skillEvidence.sourceByteSha256 = $completedSkillEvidence.sourceByteSha256
        $skillEvidence.sourceTextSha256 = $completedSkillEvidence.sourceTextSha256
        $skillEvidence.sourceContextSha256 = $completedSkillEvidence.sourceContextSha256
        $skillEvidence.textNormalization = $completedSkillEvidence.textNormalization
        $skillEvidence.observed = $completedSkillEvidence.observed
        $skillEvidence.observedSha256 = $completedSkillEvidence.observedTextSha256
        $skillEvidence.observedTextSha256 = $completedSkillEvidence.observedTextSha256
        $skillEvidence.verified = [bool]($completedSkillEvidence.verified -and
            [string]::Equals(
                [string]$completedSkillEvidence.sourceByteSha256,
                [string]$context.skillSha256,
                [StringComparison]::Ordinal))
        $skillEvidence.failure = $completedSkillEvidence.failure
        $skillEvidence.evidenceCallIds = @($completedSkillEvidence.toolCallId | Where-Object { $_ })
        $skillEvidence.sourceBytes = $completedSkillEvidence.sourceBytes
        $skillEvidence.sourceChars = $completedSkillEvidence.sourceChars
        $skillEvidence.sourceLineCount = $completedSkillEvidence.sourceLineCount
        $skillEvidence.sourceTerminalNewline = $completedSkillEvidence.sourceTerminalNewline
        $skillEvidence.observedChars = 0
        $skillEvidence.returnedPayloadChars = 0
        $skillEvidence.terminalNewlineOmissions = 0
        $skillEvidence.requestedBytes = 0
        $skillEvidence.reads = @()
        $skillEvidence.discovery = $completedSkillEvidence.discovery
        if ($skillEvidence.verified) {
            [int] $skillStartEventIndex = -1
            [int] $skillCompletionEventIndex = -1
            if (-not $startEventIndexes.TryGetValue(
                    [string]$completedSkillEvidence.toolCallId, [ref]$skillStartEventIndex) -or
                -not $completionEventIndexes.TryGetValue(
                    [string]$completedSkillEvidence.toolCallId, [ref]$skillCompletionEventIndex) -or
                $skillStartEventIndex -ge $skillCompletionEventIndex -or
                $skillCompletionEventIndex -ge $skillContextEventIndex) {
                $evidenceValid = $false
            }
        }
    }
    if ($answerEventIndex -lt 0 -or [string]::IsNullOrWhiteSpace([string]$answer)) {
        $evidenceValid = $false
    }
    elseif (@($successfulAnalysisCompletionIndexes | Where-Object { $_ -ge $answerEventIndex }).Count -gt 0 -or
        ($arm -eq 'cli-skill' -and $skillEvidence.verified -and
            ($skillContextEventIndex -lt 0 -or $skillContextEventIndex -ge $answerEventIndex))) {
        $evidenceValid = $false
    }
    if ($executionPolicyState) {
        if (-not (Test-AgentEvalStringMultiset `
                -Left ([string[]]@(
                    $executionPolicyState.commandHashes
                    $executionPolicyState.helpCommandHashes)) `
                -Right $transcriptCommandHashes.ToArray())) {
            $evidenceValid = $false
        }
        [string[]] $policyViewRequestHashes = @($executionPolicyState.viewRequests | ForEach-Object {
                [string]$_.requestHash
            })
        [string[]] $transcriptViewHashes = $transcriptViewRequestHashes.ToArray()
        if ($policyViewRequestHashes.Count -ne $transcriptViewHashes.Count) {
            $evidenceValid = $false
        }
        else {
            for ($viewIndex = 0; $viewIndex -lt $policyViewRequestHashes.Count; $viewIndex++) {
                if (-not [string]::Equals(
                        $policyViewRequestHashes[$viewIndex],
                        $transcriptViewHashes[$viewIndex],
                        [StringComparison]::Ordinal)) {
                    $evidenceValid = $false
                    break
                }
            }
        }
    }
    $resultEvents = @($events | Where-Object {
        $_.PSObject.Properties.Name -contains 'type' -and $_.type -eq 'result'
    })
    if ($resultEvents.Count -ne 1) { $evidenceValid = $false }
    $result = $resultEvents | Select-Object -First 1
    $resultMembers = if ($result) { @($result.PSObject.Properties.Name) } else { @() }
    $resultExitCode = if ($resultMembers -contains 'exitCode' -and
        $result.exitCode -in @([int]$result.exitCode, [long]$result.exitCode) -and
        ($result.exitCode -is [int] -or $result.exitCode -is [long])) {
        [int]$result.exitCode
    }
    else {
        $null
    }
    $modelEvents = @($events | Where-Object {
        $_.PSObject.Properties.Name -contains 'type' -and $_.type -eq 'session.tools_updated' -and
        $_.PSObject.Properties.Name -contains 'data'
    })
    $iterationObservedModels = @($modelEvents | Where-Object {
        $_.data.PSObject.Properties.Name -contains 'model' -and
        $_.data.model -is [string] -and -not [string]::IsNullOrWhiteSpace([string]$_.data.model)
    } | ForEach-Object { [string]$_.data.model } | Sort-Object -CaseSensitive -Unique)
    $m = if ($iterationObservedModels.Count -eq 1) { $iterationObservedModels[0] } else { $null }
    if ($m) { $script:CopilotActualModel = $m }
    $usage = if ($resultMembers -contains 'usage') { $result.usage } else { $null }
    [bool] $usageValid = Test-AgentEvalHostUsage $usage
    if ($null -ne $usage -and -not $usageValid) { $evidenceValid = $false }
    if ($usageFile.available -and
        ($null -eq $m -or -not [string]::Equals(
            [string]$usageFile.value.currentModel, [string]$m, [StringComparison]::Ordinal))) {
        $evidenceValid = $false
    }
    [bool] $hasUsageEvidence = $usageValid -or [bool]$usageFile.available
    $hostSucceeded = $copilotExitCode -eq 0 -and $null -ne $resultExitCode -and
        $resultExitCode -eq 0 -and $evidenceValid -and $hasUsageEvidence
    $note = ''
    if ($copilotExitCode -ne 0) { $note = "copilot process exitCode $copilotExitCode" }
    elseif ($null -eq $resultExitCode) { $note = 'copilot result event did not report an exit code' }
    elseif ($resultExitCode -ne 0) { $note = "copilot exitCode $resultExitCode" }
    elseif (-not $evidenceValid) { $note = 'copilot transcript evidence was malformed or included an unexpected tool attempt' }
    elseif (-not $hasUsageEvidence) { $note = 'copilot host reported no valid usage evidence' }
    elseif ($filtraceCalls -eq 0) { $note = 'no local filtrace tool call' }
    $wallMs = if ($usage -and $usage.PSObject.Properties.Name -contains 'sessionDurationMs' -and $usage.sessionDurationMs) {
        [int]$usage.sessionDurationMs
    }
    else {
        [int]$processResult.wallMs
    }
    return [pscustomobject]@{
        answer = $answer; calls = $filtraceCalls; helpCalls = $helpCalls; tokens = $tokens
        textTokens = $textTokens; structuredTokens = $structuredTokens; wireTokens = $wireTokens
        hostResultTokens = $hostResultTokens
        resultShape = ($resultShapes -join ',')
        # Whatever the host reports about its own context spend. Recorded verbatim
        # rather than reduced, because what a client puts in front of the model is
        # host-specific and this arm must not guess it.
        hostUsage = $usage
        hostUsageFile = $usageFile
        observedModel = [string]$m
        observedModels = $iterationObservedModels
        hostSucceeded = $hostSucceeded
        execution = [pscustomobject]@{
            runId = $context.runId
            workspace = $context.workspace
            capturedBytes = $processResult.capturedBytes
            artifactBytes = $processResult.artifactBytes
            hostRuntimeBytes = $processResult.hostRuntimeBytes
            processExitCode = $copilotExitCode
            resultExitCode = $resultExitCode
            hostOutput = $outputPersistence
            cli = [pscustomobject]@{
                sourcePath = $context.cliSourcePath
                sourceSha256 = $context.cliSourceSha256
                path = $context.cliPath
                sha256 = $context.cliSha256
                inventory = $context.cliInventory
            }
            fixture = [pscustomobject]@{ path = $context.fixturePath; sha256 = $context.fixtureSha256 }
            isolation = [pscustomobject]@{
                workspaceOutsideRepository = $context.isolation.workspaceOutsideRepository
                inheritedEnvironment = $context.isolation.inheritedEnvironment
                ownedEnvironment = $context.isolation.ownedEnvironment
                noCustomInstructions = [bool]($arm -eq 'cli')
                builtinMcpsDisabled = [bool]($arm -in @('cli', 'cli-skill'))
                availableTools = if ($arm -eq 'cli-skill') { @('powershell', 'skill') } elseif ($arm -eq 'cli') { @('powershell') } else { @() }
                excludedTools = if ($arm -in @('cli', 'cli-skill')) { $excludedToolNames } else { @() }
                shellDefaultDenied = [bool]($arm -in @('cli', 'cli-skill'))
                writeDenied = [bool]($arm -in @('cli', 'cli-skill'))
                urlDenied = [bool]($arm -in @('cli', 'cli-skill'))
                readDenied = [bool]($arm -in @('cli', 'cli-skill'))
                executionPolicyMaxCalls = if ($executionPolicy) { $executionPolicy.maxCalls } else { $null }
                executionPolicyCallCount = if ($executionPolicyState) { $executionPolicyState.callCount } else { $null }
                executionPolicyCommandHashes = if ($executionPolicyState) { $executionPolicyState.commandHashes } else { @() }
                executionPolicyMaxHelpCalls = if ($executionPolicy) { $executionPolicy.maxHelpCalls } else { $null }
                executionPolicyHelpCallCount = if ($executionPolicyState) { $executionPolicyState.helpCallCount } else { $null }
                executionPolicyHelpCommandHashes = if ($executionPolicyState) { $executionPolicyState.helpCommandHashes } else { @() }
                executionPolicyMaxViewCalls = if ($executionPolicy) { $executionPolicy.maxViewCalls } else { $null }
                executionPolicyMaxViewBytes = if ($executionPolicy) { $executionPolicy.maxViewBytes } else { $null }
                executionPolicyViewRequests = if ($executionPolicyState) { $executionPolicyState.viewRequests } else { @() }
                hookConfigurationPath = if ($executionPolicy) { $executionPolicy.hookConfigurationPath } else { $null }
                dynamicArtifactMaxBytes = $MaxHostArtifactBytes
                hostRuntimeMaxBytes = $processResult.hostRuntimeMaxBytes
                hostRuntimeMaxFileBytes = $processResult.hostRuntimeMaxFileBytes
                hostRuntimeMaxEntries = $processResult.hostRuntimeMaxEntries
                processTreeContained = $processResult.processTreeContained
            }
            inputPolicy = $context.inputPolicy
        }
        skill = [pscustomobject]$skillEvidence
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
    $qaMembers = @($qa.PSObject.Properties.Name)
    $task | Add-Member -NotePropertyName expectTools -NotePropertyValue @($qa.expectTools)
    $task | Add-Member -NotePropertyName expectOperations -NotePropertyValue @(
        if ($qaMembers -contains 'expectOperations') { $qa.expectOperations | Where-Object { $_ } })
    $task | Add-Member -NotePropertyName requiredCliOperations -NotePropertyValue @(
        Get-AgentEvalTaskExpectedOperations $task)
    $task | Add-Member -NotePropertyName forbidOperations -NotePropertyValue @(
        if ($qaMembers -contains 'forbidOperations') { $qa.forbidOperations | Where-Object { $_ } })
    $task | Add-Member -NotePropertyName maxCalls -NotePropertyValue $(
        if ($qaMembers -contains 'maxCalls') { $qa.maxCalls } else { $null })
    $task | Add-Member -NotePropertyName maxResponseTokens -NotePropertyValue $(
        if ($qaMembers -contains 'maxResponseTokens') { $qa.maxResponseTokens } else { $null })
    $selected.Add($task)
}
if ($selected.Count -eq 0) { throw "No matching tasks (filter: $($Tasks -join ', '))." }

# Per-host preflight (once). Copilot is agentic (it drives the trace_* tools itself
# over the MCP arm); ollama is the mediated cli ReAct loop.
if (-not $Arm) { $Arm = if ($AgentHost -eq 'ollama') { 'cli' } else { 'mcp' } }
$arm = $Arm
if ($AgentHost -eq 'ollama' -and $arm -ne 'cli') {
    throw "Host 'ollama' supports only -Arm cli."
}
if ($AgentHost -ne 'copilot' -and $arm -eq 'cli-skill') {
    throw "Arm 'cli-skill' requires -AgentHost copilot."
}
if ($AgentHost -eq 'copilot' -and $arm -in @('cli', 'cli-skill') -and
    -not [System.OperatingSystem]::IsWindows()) {
    throw "The experimental Copilot '$arm' arm currently validates only the Windows powershell tool schema."
}
$script:mcpConfigPath = $null
if ($AgentHost -eq 'copilot') {
    if (-not $CopilotPath) {
        [string] $copilotCommand = if ([System.OperatingSystem]::IsWindows()) { 'copilot.exe' } else { 'copilot' }
        $CopilotPath = @(Get-Command $copilotCommand -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    }
    if (-not $CopilotPath -or -not (Test-Path -LiteralPath $CopilotPath)) {
        throw "The 'copilot' CLI was not found on PATH. Install GitHub Copilot CLI and run 'copilot login'."
    }
    if ($CopilotAdapterPath -and -not (Test-Path -LiteralPath $CopilotAdapterPath)) {
        throw "Copilot adapter not found at '$CopilotAdapterPath'."
    }
    if ($CopilotAdapterPath) {
        $allowedAdapter = (Resolve-Path (Join-Path $root 'tools/fixtures/Fake-CopilotEvalHost.ps1')).Path
        if ((Resolve-Path -LiteralPath $CopilotAdapterPath).Path -ne $allowedAdapter) {
            throw "-CopilotAdapterPath is reserved for the repository's fake contract host."
        }
    }
    if ($arm -in @('cli', 'cli-skill') -and [string]::IsNullOrWhiteSpace($ExpectedModel)) {
        throw "-ExpectedModel is required for the Copilot '$arm' arm."
    }
    if ($arm -in @('cli', 'cli-skill') -and -not $PSBoundParameters.ContainsKey('Model')) {
        throw "-Model must be explicit for the Copilot '$arm' arm."
    }
    if ($arm -in @('cli', 'cli-skill') -and
        -not [string]::Equals($Model, $ExpectedModel, [StringComparison]::Ordinal)) {
        throw "-Model '$Model' must match -ExpectedModel '$ExpectedModel' for the Copilot '$arm' arm."
    }
    if ($arm -in @('cli', 'cli-skill') -and $PSBoundParameters.ContainsKey('Models')) {
        throw "-Models is not supported by the strict Copilot '$arm' arm; run one explicit -Model and -ExpectedModel pair."
    }
    if (-not $CopilotAdapterPath -and [System.OperatingSystem]::IsWindows() -and
        [System.IO.Path]::GetExtension($CopilotPath) -ne '.exe') {
        throw "CopilotPath must name the native copilot.exe, not a batch or PowerShell proxy."
    }
    if ($arm -eq 'mcp') { $script:mcpConfigPath = New-FiltraceMcpConfig }
}

$strictRunProjection = $null
if ($AgentHost -eq 'copilot' -and $arm -in @('cli', 'cli-skill')) {
    [long] $strictIterationCount = [long]$selected.Count * [long]$N
    if ($strictIterationCount -gt 64) {
        throw "The Copilot '$arm' arm is limited to 64 total task iterations per invocation; requested $strictIterationCount."
    }
    [System.Collections.Generic.List[string]] $unsupportedTasks = [System.Collections.Generic.List[string]]::new()
    foreach ($task in $selected) {
        try {
            if ([string]::Equals(
                    [System.IO.Path]::GetFileName([string]$task.fixture),
                    'manifest.json',
                    [StringComparison]::Ordinal)) {
                throw "Task '$($task.id)' is manifest-backed; strict arms do not copy its dependency closure."
            }
            [void](Get-AgentEvalTaskCommandFamilies -Task $task -AllowedVerbs $verbs)
        }
        catch { $unsupportedTasks.Add("$($task.id): $($_.Exception.Message)") }
    }
    if ($unsupportedTasks.Count -gt 0) {
        throw "The Copilot '$arm' arm cannot run the selected task set. Select supported tasks explicitly. " +
            ($unsupportedTasks -join '; ')
    }
    $strictRunProjection = Get-AgentEvalStrictRunProjection `
        -Tasks $selected.ToArray() `
        -Iterations $N `
        -Arm $arm `
        -Configuration $Configuration `
        -MaxArtifactBytes $MaxHostArtifactBytes
    if ([long]$strictRunProjection.projectedBytes -gt [long]$strictRunProjection.maxBytes) {
        throw "The Copilot '$arm' arm projects $($strictRunProjection.projectedBytes) retained bytes, exceeding the run-wide $($strictRunProjection.maxBytes)-byte limit."
    }
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

# Strip thousands separators from digit runs before comparing an answer with a
# task's expected substrings. A model writes "4,309" where the task pins "4309",
# and scoring that a miss measures formatting rather than analysis.
function ConvertTo-ComparableAnswer {
    param([string]$Text)
    return [regex]::Replace($Text, '(?<=\d),(?=\d)', '')
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
        $helpCallsList = [System.Collections.Generic.List[int]]::new()
        $tokensList = [System.Collections.Generic.List[int]]::new()
        $msList = [System.Collections.Generic.List[int]]::new()
        $textList = [System.Collections.Generic.List[int]]::new()
        $structuredList = [System.Collections.Generic.List[int]]::new()
        $wireList = [System.Collections.Generic.List[int]]::new()
        $hostResultList = [System.Collections.Generic.List[int]]::new()
        $shapes = [System.Collections.Generic.List[string]]::new()

        for ($i = 1; $i -le $N; $i++) {
            $r = switch ($AgentHost) {
                'ollama' { Invoke-OllamaIteration -Task $task -FixtureAbs $fixtureAbs }
                'copilot' { Invoke-CopilotIteration -Task $task -FixtureAbs $fixtureAbs -McpConfig $script:mcpConfigPath }
                default { throw "Host '$AgentHost' is recognized but not yet wired." }
            }
            $answer = $r.answer; $calls = [int]$r.calls; $helpCalls = [int]$r.helpCalls; $tokens = [int]$r.tokens
            $wallMs = [int]$r.wallMs; $note = $r.note; $transcript = $r.transcript

            $ok = $false
            if ($answer) {
                $ok = $true
                $comparableAnswer = ConvertTo-ComparableAnswer -Text $answer
                foreach ($e in $expect) {
                    $comparableExpect = ConvertTo-ComparableAnswer -Text $e
                    if ($comparableAnswer -notmatch [regex]::Escape($comparableExpect)) { $ok = $false }
                }

                if (-not $ok -and -not $note) { $note = 'answer missing expected content' }
            }
            elseif (-not $note) { $note = 'no answer produced' }

            if ($ok -and $AgentHost -eq 'copilot' -and -not $r.hostSucceeded) {
                $ok = $false
                if (-not $note) { $note = 'copilot host did not complete successfully' }
            }

            # A correct-looking MCP answer must be grounded in the tools the task is
            # designed to exercise. Extra calls (for example trace_info first) are fine;
            # every expected tool must appear at least once as a successful call.
            # The first token of a transcript entry is the MCP tool name on the mcp arm
            # and the CLI verb on the cli arm.
            $successfulNames = @($transcript |
                Where-Object { $_.ok } |
                ForEach-Object { ($_.cmd -split '\s+', 2)[0] })
            $calledOperations = @($transcript |
                Where-Object { $_.kind -eq 'filtrace' -and $_.ok -and $_.operation } |
                ForEach-Object { $_.operation } |
                Sort-Object -Unique)
            # Forbidden operations are graded on the attempt, not the outcome: reaching
            # for source lines on a speedscope profile is the mistake, and filtrace
            # rejects it - so scoring only successful calls would never see it.
            $attemptedOperations = @($transcript |
                Where-Object { $_.operation } |
                ForEach-Object { $_.operation } |
                Sort-Object -Unique)

            if ($ok -and $arm -eq 'mcp') {
                $missingTools = @($task.expectTools | Where-Object {
                    [string] $expectedTool = $_
                    -not @($successfulNames | Where-Object {
                        [string]::Equals([string]$_, $expectedTool, [StringComparison]::Ordinal)
                        }).Count
                    })
                if ($missingTools.Count -gt 0) {
                    $ok = $false
                    $note = "missing expected successful MCP tool(s): $($missingTools -join ', ')"
                }
            }
            if ($ok -and $AgentHost -eq 'copilot' -and $arm -in @('cli', 'cli-skill')) {
                if (@($r.observedModels).Count -eq 0) {
                    $ok = $false
                    $note = 'host did not report the observed model'
                }
                elseif (@($r.observedModels).Count -ne 1 -or
                    -not [string]::Equals($r.observedModel, $ExpectedModel, [StringComparison]::Ordinal)) {
                    $ok = $false
                    $note = "observed model(s) '$(@($r.observedModels) -join ', ')' did not match expected model '$ExpectedModel'"
                }
            }
            if ($ok -and $AgentHost -eq 'copilot' -and $arm -in @('cli', 'cli-skill') -and
                @($transcript | Where-Object { $_.kind -eq 'filtrace' -and $_.ok }).Count -eq 0) {
                $ok = $false
                $note = 'no matched successful local filtrace completion'
            }
            if ($ok -and $arm -eq 'cli-skill' -and -not $r.skill.verified) {
                $ok = $false
                $note = if (-not $r.skill.observed) {
                    'host did not discover and invoke the provided filtrace skill'
                }
                elseif ($r.skill.failure) {
                    "provided skill use was unverified: $($r.skill.failure)"
                }
                else {
                    "observed skill context hash '$($r.skill.observedTextSha256)' did not match expected context hash '$($r.skill.sourceContextSha256)'"
                }
            }

            # Intent grading. Unlike the exact tool names, this survives a rename or a
            # consolidation, so a baseline and a candidate surface can be compared.
            [string[]] $requiredOperations = @(
                $task.expectOperations
                if ($arm -in @('cli', 'cli-skill')) { $task.requiredCliOperations }
            ) | Where-Object { $_ } | Sort-Object -CaseSensitive -Unique
            if ($ok -and $requiredOperations.Count -gt 0) {
                $missingOperations = @($requiredOperations | Where-Object {
                    [string] $expectedOperation = $_
                    -not @($calledOperations | Where-Object {
                        [string]::Equals([string]$_, $expectedOperation, [StringComparison]::Ordinal)
                        }).Count
                    })
                if ($missingOperations.Count -gt 0) {
                    $ok = $false
                    $note = "missing expected operation(s): $($missingOperations -join ', ')"
                }
            }
            if ($ok -and $task.forbidOperations.Count -gt 0) {
                $forbiddenUsed = @($task.forbidOperations | Where-Object {
                    [string] $forbiddenOperation = $_
                    @($attemptedOperations | Where-Object {
                        [string]::Equals([string]$_, $forbiddenOperation, [StringComparison]::Ordinal)
                        }).Count -gt 0
                    })
                if ($forbiddenUsed.Count -gt 0) {
                    $ok = $false
                    $note = "called forbidden operation(s): $($forbiddenUsed -join ', ')"
                }
            }
            if ($ok -and $task.maxCalls -and $calls -gt [int]$task.maxCalls) {
                $ok = $false
                $note = "$calls calls exceeds this task's $($task.maxCalls)-call budget"
            }
            # Restraint is a response-size property, not a call-count one: one call that
            # asks for ten thousand event records still answers the question, and a call
            # budget cannot see it. Grade the largest single response rather than the
            # iteration's total - summing would fail an iteration whose responses were
            # each well inside the ceiling.
            if ($ok -and $task.maxResponseTokens) {
                $largestResponse = 0
                foreach ($entry in @($transcript | Where-Object { $_.kind -eq 'filtrace' })) {
                    $entryTokens = [math]::Max([int]$entry.textTokens, [int]$entry.structuredTokens)
                    if ($entryTokens -gt $largestResponse) { $largestResponse = $entryTokens }
                }

                if ($largestResponse -gt [int]$task.maxResponseTokens) {
                    $ok = $false
                    $note = "largest response of $largestResponse tokens exceeds this task's " +
                        "$($task.maxResponseTokens)-token ceiling"
                }
            }

            if ($ok) { $successes++ }
            $callsList.Add($calls); $helpCallsList.Add($helpCalls); $tokensList.Add($tokens); $msList.Add($wallMs)
            if ($null -ne $r.wireTokens) {
                $textList.Add([int]$r.textTokens); $structuredList.Add([int]$r.structuredTokens); $wireList.Add([int]$r.wireTokens)
                $hostResultList.Add([int]$r.hostResultTokens)
                if ($r.resultShape -and -not $shapes.Contains([string]$r.resultShape)) { $shapes.Add([string]$r.resultShape) }
            }
            $iterRecords.Add([pscustomobject]@{
                    task = $task.id; iteration = $i; success = $ok; calls = $calls; helpCalls = $helpCalls
                    tokens = $tokens; wallMs = $wallMs
                    textTokens = $r.textTokens; structuredTokens = $r.structuredTokens
                    wireTokens = $r.wireTokens; hostResultTokens = $r.hostResultTokens
                    resultShape = $r.resultShape; hostUsage = $r.hostUsage
                    hostUsageFile = $r.hostUsageFile
                    observedModel = $r.observedModel
                    observedModels = $r.observedModels
                    operations = $calledOperations
                    attemptedOperations = $attemptedOperations
                    execution = $r.execution
                    skill = $r.skill
                    answer = $answer; note = $note; transcript = $transcript
                })
            $tag = if ($ok) { 'ok ' } else { 'MISS' }
            Write-Host ("  [{0}] {1} iter {2}/{3}: calls={4} help={5} tokens={6} {7}ms {8}" -f $tag, $task.id, $i, $N, $calls, $helpCalls, $tokens, $wallMs, $note)
        }

        $rows.Add([pscustomobject]@{
            Task = $task.id; SuccessCount = $successes
            'Success%' = [int]([math]::Round(100.0 * $successes / $N))
                MedCalls = (Get-Median $callsList); MedHelpCalls = (Get-Median $helpCallsList)
                MedTokens = (Get-Median $tokensList); MedMs = (Get-Median $msList)
            })
        if ($wireList.Count -gt 0) {
            $transportRows.Add([pscustomobject]@{
                    Task = $task.id; MedText = (Get-Median $textList)
                    MedStructured = (Get-Median $structuredList); MedWire = (Get-Median $wireList)
                    MedHostResult = (Get-Median $hostResultList)
                    Shape = ($shapes -join ',')
                })
        }
    }

    Write-Host ''
    $rows | Format-Table -AutoSize | Out-String | Write-Host
    if ($transportRows.Count -gt 0) {
        Write-Host 'Per-response transport cost. Text and structured are two copies of the same payload;'
        Write-Host 'MedWire is the MCP result carrying both. MedHostResult is what the host logged around'
        Write-Host 'it, which includes its own extra renderings and does not respond to a transport change.'
        $transportRows | Format-Table -AutoSize | Out-String | Write-Host
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
    # Report the model Copilot actually used (from its JSONL) when none was pinned.
    $observedModels = if ($AgentHost -eq 'copilot') {
        @($iterRecords | ForEach-Object { $_.observedModels } | Where-Object { $_ } | Sort-Object -CaseSensitive -Unique)
    }
    else {
        @($RunModel)
    }
    $observedModels = @($observedModels)
    $observedModel = if ($observedModels.Count -eq 1) { $observedModels[0] } else { $null }
    $reportModel = [ordered]@{
        requested = $RunModel
        expected = $ExpectedModel
        observed = $observedModel
        observedDistinct = $observedModels
        verified = [bool]($observedModels.Count -eq 1 -and (-not $ExpectedModel -or
            [string]::Equals($observedModel, $ExpectedModel, [StringComparison]::Ordinal)))
    }
    $safeModelName = if ($observedModel) { $observedModel } elseif ($observedModels.Count -gt 1) { 'mixed-models' } else { 'unobserved' }
    $safeModel = ($safeModelName -replace '[^\w.-]', '_')
    $labelPart = if ($RunLabel) { "$($RunLabel -replace '[^\w.-]', '_')-" } else { '' }
    $resultPath = Join-Path $OutDir "$AgentHost-$safeModel-$labelPart$stamp.json"
    $payload = [ordered]@{
        schemaVersion = 3
        host          = $AgentHost
        model         = $reportModel
        arm           = $arm
        label         = $RunLabel
        n             = $N
        maxSteps      = $MaxSteps
        strictRunBudget = $strictRunProjection
        mcpDll        = $McpDll
        tokenAccounting = 'offline observed tool-result estimate; hostUsage is the result event; hostUsageFile is bounded host-reported token accounting'
        warnings      = @($iterRecords | ForEach-Object { $_.note } | Where-Object { $_ } | Sort-Object -Unique)
        timestamp     = (Get-Date).ToString('o')
        summary       = $rows
        transport     = $transportRows
        iterations    = $iterRecords
    }
    [System.Text.Encoding] $utf8 = [System.Text.UTF8Encoding]::new($false)
    [string] $resultJson = ConvertTo-AgentEvalResultJson $payload
    Write-AgentEvalNewFile -Path $resultPath -Bytes $utf8.GetBytes("$resultJson`n")
    Write-Host "Wrote results ($modelLabel$labelTag) to $resultPath" -ForegroundColor Green
    return $resultPath
}

# --- Run each model in the list -----------------------------------------------

# A labeled run is a baseline or a candidate someone will compare; one or two
# iterations cannot support a median, and a surface decision taken on that basis
# remains descriptive. Warn rather than block - a labeled smoke run is still useful.
if ($Label -and $N -lt 3) {
    Write-Warning "-Label '$Label' with N=${N} is a descriptive smoke run; comparison thresholds do not establish statistical confidence."
}

foreach ($m in $modelList) { Invoke-EvalRun -RunModel $m -RunLabel $Label | Out-Null }

# Clean up the temporary Copilot MCP config, if one was written.
if ($script:mcpConfigPath -and (Test-Path $script:mcpConfigPath)) { Remove-Item $script:mcpConfigPath -Force -ErrorAction SilentlyContinue }
