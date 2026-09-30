#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Validates a built or explicitly installed filtrace MCP server's wire protocol.

.DESCRIPTION
  Enforces the two checks born with the MCP facade (docs/design.md, "Measures of
  success"), plus a scripted client round-trip:

    1. stdout purity - stdout carries only JSON-RPC. The server is run with a
       deliberately chatty log level (Trace) forced through configuration; every
       line it writes to stdout must still parse as JSON, proving the logging
       providers are pinned to stderr and cannot corrupt the protocol stream.
    2. schema budget - the tool list a client sends to the model must stay small.
       The serialized `tools` array from a real `tools/list` round-trip is measured
       and the estimated token cost must stay within the budget, so the curated
       surface cannot grow into an unscannable wall that crowds the model's context.
    3. tool round-trip - a real `tools/call` (trace_info against a committed
       fixture) must come back as the tool's result envelope, not an error,
       exercising the whole client -> server -> service -> client path.

  Drives the server over stdio exactly as a client would: initialize, initialized,
  tools/list, then tools/call. Run from the filtrace subtree root (the directory
  holding filtrace.slnx).

.PARAMETER Configuration
  The build configuration whose MCP binary to exercise. Defaults to Release.

.PARAMETER MaxSchemaTokens
  The tool-list token budget. Defaults to 7000. Tokens are estimated by
  tools/Get-TokenEstimate.ps1 (a deterministic, offline pre-tokenizer estimate -
  far more accurate on JSON than the old four-characters-per-token rule, and
  slightly conservative); the check prints the measured characters and estimate so
  a regression is legible. The budget covers each tool's name, description, input
  schema, and output schema - everything the client puts in front of the model from
  tools/list, on every request.
  The ceiling is a bloat guard, not a per-tool allowance. The current 18-tool
  surface is near this budget; the schema report separates input schemas, output
  schemas, and descriptions so any movement is attributable. Historical transport
  and tool-consolidation findings are retained in docs/design.md.

.PARAMETER SchemaReportPath
  Where to write the per-tool schema-token breakdown (input schema, output schema,
  description, total, parameter count), plus the exercised server identity.
  Repo-relative unless rooted; defaults to the ignored
  artifacts/ directory. Pass an empty string to skip writing it.

.PARAMETER ServerExecutable
  Explicit installed server executable. When omitted, runs dotnet with the
  existing Configuration-specific built DLL.

.PARAMETER ServerArguments
  Argument array for ServerExecutable. Not a shell command string.

.PARAMETER ExpectedVersion
  When supplied, initialize must report this exact serverInfo.version.

.PARAMETER FixturePath
  Explicit trace_info round-trip fixture, such as an owned copy of the committed
  folding.speedscope.json. Defaults to that committed fixture.

.PARAMETER TimeoutSeconds
  Overall protocol deadline. Defaults to 30 seconds. Shutdown and stream draining
  each have an additional five-second bound.

.PARAMETER MaxOutputBytes
  Combined UTF-8 stdout/stderr capture bound. Defaults to 1 MiB.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [int]$MaxSchemaTokens = 7000,
    [string]$SchemaReportPath = 'artifacts/mcp-schema-tokens.json',
    [string]$ServerExecutable = '',
    [string[]]$ServerArguments = @(),
    [string]$ExpectedVersion = '',
    [string]$FixturePath = '',
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 30,
    [ValidateRange(1024, 4194304)][int]$MaxOutputBytes = 1MB
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$mcpDll = Join-Path $root "src/Filtrace.Mcp/bin/$Configuration/net10.0/Filtrace.Mcp.dll"

# The deterministic, offline token estimator shared with the C# OutputBudget.
. (Join-Path $PSScriptRoot 'Get-TokenEstimate.ps1')
. (Join-Path $root 'eval/CopilotEval.Helpers.ps1')

if (-not $ServerExecutable) {
    if ($ServerArguments.Count -ne 0) { throw 'ServerArguments requires ServerExecutable.' }
    if (-not (Test-Path -LiteralPath $mcpDll -PathType Leaf)) {
        throw "MCP binary not found at '$mcpDll'. Build the solution first (dotnet build filtrace.slnx -c $Configuration)."
    }
    $ServerExecutable = 'dotnet'
    $ServerArguments = @($mcpDll)
}
$fixture = if ($FixturePath) {
    (Resolve-Path -LiteralPath $FixturePath -ErrorAction Stop).Path
}
else {
    Join-Path $root 'tests/Filtrace.Core.Tests/Fixtures/folding.speedscope.json'
}
if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) {
    throw "Round-trip fixture not found at '$fixture'."
}

$failures = [System.Collections.Generic.List[string]]::new()
function Add-Failure([string]$message) { $failures.Add($message) }

# Drive the server over stdio exactly as a client would.
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $ServerExecutable
foreach ($argument in $ServerArguments) { $psi.ArgumentList.Add($argument) }
$psi.WorkingDirectory = Split-Path -Parent $fixture
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
# Force a deliberately chatty log level so the run exercises the logging path; the
# server must keep every one of these off stdout. The double underscore is the
# configuration-provider nesting separator (Logging:LogLevel:Default).
$psi.Environment['Logging__LogLevel__Default'] = 'Trace'

# A JSON-RPC 2.0 message carries jsonrpc == "2.0"; the purity contract is that
# stdout lines are well-formed JSON-RPC, not merely parseable JSON.
function Test-IsJsonRpc20($root) {
    try {
        $field = $root.GetProperty('jsonrpc')
        return ($field.ValueKind -eq [System.Text.Json.JsonValueKind]::String) -and ($field.GetString() -eq '2.0')
    }
    catch { return $false }
}

# The numeric JSON-RPC id of a message, or $null for a notification (no id) or a
# non-integer id. Used to identify the tools/list response by id == 2 exactly,
# rather than a substring match that would also accept id 20, 21, ...
function Get-JsonRpcId($root) {
    try {
        $field = $root.GetProperty('id')
        if ($field.ValueKind -eq [System.Text.Json.JsonValueKind]::Number) { return $field.GetInt32() }
    }
    catch { }
    return $null
}

# Round-trip a real tool call (id 3): invoke trace_info against a committed fixture
# so the harness exercises the full client -> server -> service -> client path, not
# just tools/list. ConvertTo-Json handles escaping the (possibly back-slashed) path.
$callRequest = @{
    jsonrpc = '2.0'
    id      = 3
    method  = 'tools/call'
    params  = @{ name = 'trace_info'; arguments = @{ path = $fixture } }
} | ConvertTo-Json -Compress -Depth 6
$stdout = [System.Collections.Generic.List[string]]::new()
$responses = @{ initialize = $false; toolsList = $false; call = $false }
function Add-ProtocolLine([string]$Line) {
    $stdout.Add($Line)
    try {
        $probe = [System.Text.Json.JsonDocument]::Parse($Line.Trim())
        try {
            switch (Get-JsonRpcId $probe.RootElement) {
                1 { $responses.initialize = $true }
                2 { $responses.toolsList = $true }
                3 { $responses.call = $true }
            }
        }
        finally { $probe.Dispose() }
    }
    catch { } # The purity check below diagnoses invalid lines.
}
Write-Host "Exercising the MCP server: $ServerExecutable $($ServerArguments -join ' ')"
$p = [System.Diagnostics.Process]::new()
$p.StartInfo = $psi
$started = $false
$terminated = $false
$serverExitCode = $null
$stderr = [System.Text.StringBuilder]::new()
$partialLine = ''
$capturedBytes = 0L
$utf8 = [System.Text.Encoding]::UTF8
try {
    $started = $p.Start()
    if (-not $started) { throw "Failed to start MCP server '$ServerExecutable'." }
    # Chunk reads bound even a single unterminated stdout line. Both streams are
    # pumped concurrently so Trace-level stderr cannot block protocol responses.
    $stdoutBuffer = [char[]]::new(4096)
    $stderrBuffer = [char[]]::new(4096)
    $stdoutTask = $p.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
    $stderrTask = $p.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
    $stdoutClosed = $false
    $stderrClosed = $false
    $inputClosed = $false
    $terminationAttempted = $false
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"ci","version":"1.0"}}}')
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
    $p.StandardInput.WriteLine($callRequest)
    $p.StandardInput.Flush()
    while (-not ($stdoutClosed -and $stderrClosed -and $p.HasExited)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            if ($terminationAttempted) {
                Add-Failure 'MCP process or redirected streams did not close within the bounded shutdown.'
                break
            }
            if (-not ($responses.initialize -and $responses.toolsList -and $responses.call)) {
                Add-Failure "MCP responses did not arrive within $TimeoutSeconds seconds."
            }
            if (-not $p.HasExited) {
                $terminated = $true
                Stop-AgentEvalProcess $p $started
            }
            $terminationAttempted = $true
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
        }
        foreach ($isStdout in @($true, $false)) {
            if (($isStdout -and $stdoutClosed) -or (-not $isStdout -and $stderrClosed)) { continue }
            $task = if ($isStdout) { $stdoutTask } else { $stderrTask }
            if (-not $task.IsCompleted) { continue }
            $count = $task.GetAwaiter().GetResult()
            if ($count -eq 0) {
                if ($isStdout) {
                    $stdoutClosed = $true
                    if ($partialLine.Length -gt 0) { Add-ProtocolLine $partialLine; $partialLine = '' }
                }
                else { $stderrClosed = $true }
                continue
            }
            $buffer = if ($isStdout) { $stdoutBuffer } else { $stderrBuffer }
            $chunkBytes = $utf8.GetByteCount($buffer, 0, $count)
            if ($chunkBytes -gt ($MaxOutputBytes - $capturedBytes)) {
                throw "MCP stdout/stderr exceeded the $MaxOutputBytes-byte capture bound."
            }
            $capturedBytes += $chunkBytes
            if ($isStdout) {
                $partialLine += [string]::new($buffer, 0, $count)
                while (($newline = $partialLine.IndexOf("`n")) -ge 0) {
                    Add-ProtocolLine ($partialLine.Substring(0, $newline).TrimEnd("`r"))
                    $partialLine = $partialLine.Substring($newline + 1)
                }
                $stdoutTask = $p.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
            }
            else {
                [void]$stderr.Append($buffer, 0, $count)
                $stderrTask = $p.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
            }
        }
        if (-not $inputClosed -and $responses.initialize -and $responses.toolsList -and $responses.call) {
            $p.StandardInput.Close()
            $inputClosed = $true
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
        }
        Start-Sleep -Milliseconds 10
    }
    if ($p.HasExited) { $serverExitCode = $p.ExitCode }
    if (-not $terminated -and $serverExitCode -ne 0) {
        Add-Failure "MCP server exited with code '$serverExitCode'."
    }
}
finally {
    Stop-AgentEvalProcess $p $started
    if ($started -and -not $p.HasExited) { Add-Failure 'The owned MCP server did not terminate.' }
    $p.Dispose()
}
$stderrOutput = $stderr.ToString()

# 1. stdout purity: every non-empty stdout line must be a JSON-RPC 2.0 message, not
# merely parseable JSON. The tools/list response is id == 2; the tools/call is id == 3.
$toolsLine = $null
$callLine = $null
$initializeLine = $null
foreach ($line in $stdout) {
    $trimmed = $line.Trim()
    if ($trimmed.Length -eq 0) { continue }
    try {
        $doc = [System.Text.Json.JsonDocument]::Parse($trimmed)
    }
    catch {
        Add-Failure "Non-JSON line on stdout (would corrupt the JSON-RPC stream): $trimmed"
        continue
    }

    if (-not (Test-IsJsonRpc20 $doc.RootElement)) {
        Add-Failure "Line on stdout is not a JSON-RPC 2.0 message (missing jsonrpc=2.0): $trimmed"
        continue
    }

    $id = Get-JsonRpcId $doc.RootElement
    if ($id -eq 1) { $initializeLine = $trimmed }
    elseif ($id -eq 2) { $toolsLine = $trimmed }
    elseif ($id -eq 3) { $callLine = $trimmed }
}

$serverVersion = $null
if ($null -ne $initializeLine) {
    $initializeDoc = [System.Text.Json.JsonDocument]::Parse($initializeLine)
    try {
        $serverInfo = $initializeDoc.RootElement.GetProperty('result').GetProperty('serverInfo')
        $serverVersion = $serverInfo.GetProperty('version').GetString()
        if ($serverInfo.GetProperty('name').GetString() -cne 'filtrace' -or
            [string]::IsNullOrWhiteSpace($serverVersion)) {
            Add-Failure 'initialize did not report a valid filtrace server identity.'
        }
        if ($ExpectedVersion -and $serverVersion -cne $ExpectedVersion) {
            Add-Failure "initialize reported version '$serverVersion', expected '$ExpectedVersion'."
        }
        Write-Host "Initialize: filtrace $serverVersion"
    }
    catch { Add-Failure 'initialize did not return the expected serverInfo envelope.' }
    finally { $initializeDoc.Dispose() }
}
else { Add-Failure 'Could not locate the initialize response (id 1).' }

# 2. schema budget: measure the serialized tools array from the tools/list response.
$toolNames = @()
$classifyDescription = $null
$scopeTools = [ordered]@{
    process = [System.Collections.Generic.List[string]]::new()
    root = [System.Collections.Generic.List[string]]::new()
    benchmark = [System.Collections.Generic.List[string]]::new()
}
if ($null -ne $toolsLine) {
    $doc = [System.Text.Json.JsonDocument]::Parse($toolsLine)
    $tools = $doc.RootElement.GetProperty('result').GetProperty('tools')
    # Per-tool breakdown, so a budget movement can be attributed to a definition
    # rather than only observed in the total. Descriptions, input schemas, and
    # output schemas are three independent levers with very different sizes.
    $toolReport = [System.Collections.Generic.List[object]]::new()
    foreach ($tool in $tools.EnumerateArray()) {
        $toolName = $tool.GetProperty('name').GetString()
        $toolNames += $toolName
        $inputSchema = $tool.GetProperty('inputSchema')

        $descriptionElement = [System.Text.Json.JsonElement]::new()
        $descriptionTokens = 0
        if ($tool.TryGetProperty('description', [ref]$descriptionElement)) {
            $descriptionTokens = [int](Get-TokenEstimate -Text $descriptionElement.GetString())
            if ($toolName -eq 'trace_classify') {
                $classifyDescription = $descriptionElement.GetString()
            }
        }
        $outputElement = [System.Text.Json.JsonElement]::new()
        $outputTokens = 0
        if ($tool.TryGetProperty('outputSchema', [ref]$outputElement)) {
            $outputTokens = [int](Get-TokenEstimate -Text $outputElement.GetRawText())
        }

        $properties = [System.Text.Json.JsonElement]::new()
        $hasProperties = $inputSchema.TryGetProperty('properties', [ref]$properties)
        $parameterCount = 0
        if ($hasProperties) {
            foreach ($property in $properties.EnumerateObject()) { $parameterCount++ }
        }

        $toolReport.Add([pscustomobject][ordered]@{
                name               = $toolName
                totalTokens        = [int](Get-TokenEstimate -Text $tool.GetRawText())
                inputSchemaTokens  = [int](Get-TokenEstimate -Text $inputSchema.GetRawText())
                outputSchemaTokens = $outputTokens
                descriptionTokens  = $descriptionTokens
                parameterCount     = $parameterCount
            })

        if (-not $hasProperties) {
            continue
        }
        foreach ($scope in $scopeTools.Keys) {
            try {
                $null = $properties.GetProperty($scope)
                $scopeTools[$scope].Add($toolName)
            }
            catch { }
        }
    }

    $serialized = $tools.GetRawText()
    $chars = $serialized.Length
    $estimatedTokens = Get-TokenEstimate -Text $serialized
    Write-Host "Tool list: $($toolNames.Count) tools ($($toolNames -join ', '))"
    Write-Host "Schema size: $chars chars, ~$estimatedTokens tokens (budget $MaxSchemaTokens)"
    if ($estimatedTokens -gt $MaxSchemaTokens) {
        Add-Failure "Tool-list schema is ~$estimatedTokens tokens (budget $MaxSchemaTokens). Tighten descriptions or trim the surface."
    }
    if ($null -eq $classifyDescription -or
        $classifyDescription -notmatch 'trace-recorded sample intervals' -or
        $classifyDescription -notmatch 'raw sample counts') {
        Add-Failure 'trace_classify does not explain interval-derived milliseconds and raw sample counts.'
    }
    elseif ($classifyDescription -match '(?i)CPU self-time|(?:each|every)\s+sample\s+(?:weighs|as)\s+1 ms') {
        Add-Failure 'trace_classify still presents CPU weights as fixed time.'
    }

    if ($SchemaReportPath) {
        $reportFull = if ([System.IO.Path]::IsPathRooted($SchemaReportPath)) { $SchemaReportPath } else { Join-Path $root $SchemaReportPath }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportFull) | Out-Null
        $report = [ordered]@{
            schemaVersion      = 1
            timestamp          = (Get-Date).ToString('o')
            configuration      = $Configuration
            serverExecutable   = $ServerExecutable
            serverArguments    = @($ServerArguments)
            serverVersion      = $serverVersion
            serverExitCode     = $serverExitCode
            serverTerminated   = $terminated
            toolCount          = $toolNames.Count
            characters         = $chars
            estimatedTokens    = [int]$estimatedTokens
            budgetTokens       = $MaxSchemaTokens
            inputSchemaTokens  = [int](($toolReport | Measure-Object -Property inputSchemaTokens -Sum).Sum)
            outputSchemaTokens = [int](($toolReport | Measure-Object -Property outputSchemaTokens -Sum).Sum)
            descriptionTokens  = [int](($toolReport | Measure-Object -Property descriptionTokens -Sum).Sum)
            tools              = @($toolReport | Sort-Object { -$_.totalTokens })
        }
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($reportFull, (($report | ConvertTo-Json -Depth 5) + "`n"), $utf8)
        Write-Host "Schema breakdown: input $($report.inputSchemaTokens), output $($report.outputSchemaTokens), descriptions $($report.descriptionTokens) tokens -> $SchemaReportPath"
    }

    $workflow = Get-Content -LiteralPath (Join-Path $root 'docs/workflow.md') -Raw
    $scopeBlockMatch = [regex]::Match(
        $workflow,
        '(?s)<!-- filtrace:begin scopes -->\r?\n(.*?)\r?\n<!-- filtrace:end scopes -->')
    if (-not $scopeBlockMatch.Success) {
        Add-Failure "docs/workflow.md has no synchronized 'scopes' block."
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
        foreach ($scope in $scopeTools.Keys) {
            if ([string]::IsNullOrWhiteSpace($scopeSections[$scope])) {
                Add-Failure "docs/workflow.md scope inventory has no '$scope' section."
                continue
            }
            foreach ($toolName in $scopeTools[$scope]) {
                $token = '`' + $toolName + '`'
                if (-not $scopeSections[$scope].Contains($token, [StringComparison]::Ordinal)) {
                    Add-Failure "MCP tool '$toolName' implements '$scope' but is absent from the scope inventory."
                }
            }
        }
    }
}
else {
    Add-Failure 'Could not locate the tools/list response to measure the schema budget.'
}

# 3. tool round-trip: the tools/call response (id 3) must carry the tool's result
# envelope, not an error, and that envelope must be the AnalysisResult contract.
if ($null -ne $callLine) {
    $callDoc = [System.Text.Json.JsonDocument]::Parse($callLine)
    $callRoot = $callDoc.RootElement

    # A JSON-RPC error response carries an 'error' member instead of 'result'.
    $hasError = $false
    try { $null = $callRoot.GetProperty('error'); $hasError = $true } catch { }
    if ($hasError) {
        Add-Failure "trace_info round-trip returned a JSON-RPC error: $callLine"
    }
    else {
        try {
            $callResult = $callRoot.GetProperty('result')

            # A tool-level failure sets isError == true on the result.
            $isError = $false
            try { $isError = $callResult.GetProperty('isError').GetBoolean() } catch { }
            if ($isError) {
                Add-Failure "trace_info round-trip reported a tool error: $callLine"
            }

            # The tool's text content is the AnalysisResult envelope; confirm it parses
            # and carries the schema version, proving the full path returned real data.
            $firstContent = $callResult.GetProperty('content').EnumerateArray() | Select-Object -First 1
            $text = $firstContent.GetProperty('text').GetString()
            $envelope = [System.Text.Json.JsonDocument]::Parse($text)
            $schemaVersion = $envelope.RootElement.GetProperty('schemaVersion').GetInt32()
            Write-Host "Round-trip: trace_info returned an envelope (schemaVersion $schemaVersion)"
        }
        catch {
            Add-Failure "trace_info round-trip response was not the expected envelope: $callLine"
        }
    }
}
else {
    Add-Failure 'Could not locate the trace_info tools/call response (id 3).'
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "MCP server check FAILED with $($failures.Count) issue(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    if (-not [string]::IsNullOrWhiteSpace($stderrOutput)) {
        Write-Host ''
        Write-Host 'Server stderr (tail):' -ForegroundColor Yellow
        $tail = if ($stderrOutput.Length -gt 2048) { $stderrOutput.Substring($stderrOutput.Length - 2048) } else { $stderrOutput }
        Write-Host $tail
    }
    exit 1
}

Write-Host ''
Write-Host 'MCP server check passed.' -ForegroundColor Green
exit 0
