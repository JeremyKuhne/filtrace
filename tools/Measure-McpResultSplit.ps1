#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Ground truth for the transport experiment: what one tools/call result actually costs.

.DESCRIPTION
  Calls the filtrace MCP server directly over stdio and measures a real tool result
  split into the text block, structuredContent, and the complete MCP result, so a
  transport variant can be compared against a measurement rather than an argument.

  This exists because the live agent harness takes the same split from the Copilot
  CLI's JSONL transcript, which is a HOST rendering of the result and not the
  protocol result. Measured 2026-08-02, that host adds `detailedContent` and
  `contents` - two further copies of the payload that never crossed the MCP
  boundary - so its transcript carries about four copies of what the server sent
  twice. Reading a duplication factor off the transcript overstates what a
  transport change can remove. Run this against each VN1 variant; the harness
  reports the host figure separately as hostResultTokens.

.PARAMETER Configuration
  Build configuration whose Filtrace.Mcp build is exercised.

.EXAMPLE
  ./tools/Measure-McpResultSplit.ps1 -Configuration Release
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'tools/Get-TokenEstimate.ps1')

$dll = Join-Path $root "src/Filtrace.Mcp/bin/$Configuration/net10.0/Filtrace.Mcp.dll"
if (-not (Test-Path $dll)) { throw "MCP server not built at '$dll'. Build the solution first." }

function Get-Fixture([string]$name) { Join-Path $root "tests/Filtrace.Core.Tests/Fixtures/$name" }

# Span the size range a real investigation covers: a small orientation call up to a
# result at the response ceiling, so a per-call fixed cost cannot masquerade as a
# proportional one.
$calls = @(
    @{ label = 'trace_info'; name = 'trace_info'; arguments = @{ path = (Get-Fixture 'folding.speedscope.json') } }
    @{ label = 'trace_gc'; name = 'trace_gc'; arguments = @{ path = (Get-Fixture 'alloc.nettrace') } }
    @{ label = 'trace_rank'; name = 'trace_rank'; arguments = @{ path = (Get-Fixture 'threadpool.nettrace'); top = 25 } }
    @{ label = 'trace_jit top=25'; name = 'trace_jit'; arguments = @{ path = (Get-Fixture 'jit.nettrace'); top = 25 } }
    @{ label = 'trace_jit top=100000'; name = 'trace_jit'; arguments = @{ path = (Get-Fixture 'jit.nettrace'); top = 100000 } }
    @{ label = 'trace_query_events take=400'; name = 'trace_query_events'; arguments = @{ path = (Get-Fixture 'alloc.nettrace'); name = 'AllocationTick'; take = 400 } }
)

$psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet', "`"$dll`"")
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $root
$process = [System.Diagnostics.Process]::Start($psi)
# Drain stderr concurrently; a full pipe blocks the server mid-protocol.
$stderrTask = $process.StandardError.ReadToEndAsync()

try {
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}')
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')

    $expected = @{}
    $id = 10
    foreach ($call in $calls) {
        $expected[$id] = $call.label
        $process.StandardInput.WriteLine((@{
                    jsonrpc = '2.0'; id = $id; method = 'tools/call'
                    params  = @{ name = $call.name; arguments = $call.arguments }
                } | ConvertTo-Json -Compress -Depth 8))
        $id++
    }
    $process.StandardInput.Flush()

    $responses = @{}
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $pending = $process.StandardOutput.ReadLineAsync()
    while ([DateTime]::UtcNow -lt $deadline -and $responses.Count -lt $calls.Count) {
        if (-not $pending.Wait(500)) { continue }
        $line = $pending.Result
        if ($null -eq $line) { break }
        $trimmed = $line.Trim()
        if ($trimmed.Length -gt 0) {
            try {
                $document = $trimmed | ConvertFrom-Json
                if ($null -ne $document.id -and $expected.ContainsKey([int]$document.id)) { $responses[[int]$document.id] = $document }
            }
            catch { }
        }
        $pending = $process.StandardOutput.ReadLineAsync()
    }

    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
    $stderrOutput = $stderrTask.GetAwaiter().GetResult()

    if ($responses.Count -lt $calls.Count) {
        throw "Only $($responses.Count) of $($calls.Count) tool calls returned within the deadline.`nstderr:`n$stderrOutput"
    }
}
finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}

$rows = foreach ($key in ($expected.Keys | Sort-Object)) {
    $result = $responses[$key].result
    $textRaw = ''
    foreach ($block in @($result.content)) {
        if ($null -ne $block.text) { $textRaw += [string]$block.text }
    }
    $structuredRaw = if ($null -ne $result.structuredContent) { $result.structuredContent | ConvertTo-Json -Depth 24 -Compress } else { '' }
    $wireRaw = $result | ConvertTo-Json -Depth 24 -Compress

    $text = [int](Get-TokenEstimate -Text $textRaw)
    $wire = [int](Get-TokenEstimate -Text $wireRaw)

    [pscustomobject]@{
        Call          = $expected[$key]
        TextTokens    = $text
        StructTokens  = [int](Get-TokenEstimate -Text $structuredRaw)
        WireTokens    = $wire
        'Wire/Text'   = if ($text -gt 0) { [math]::Round($wire / $text, 2) } else { 0 }
        'TextIsCopy'  = ($textRaw -eq $structuredRaw)
    }
}

$rows | Format-Table -AutoSize
