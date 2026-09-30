#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Maps a CLI verb or MCP tool name to its surface-neutral operation name.

.DESCRIPTION
  The eval harness grades two things: which exact tool an agent called
  (`expectTools`, valid only while the current MCP surface is the baseline) and
  which *operation intent* it reached for (`expectOperations`, stable across a
  surface change). This map is what makes the second one possible:
  `rank --metric cpu` and `trace_rank` are both the `rank` operation, and
  `source --view lines` / `source --view heatmap` / `trace_lines` / `trace_heatmap`
  are all `source`.

  Keep the operation vocabulary aligned with docs/roadmap.md's surface so
  CLI report kinds map to the same operations as their corresponding MCP tools.

.NOTES
  Dot-source the script to reuse the function:
    . ./eval/Get-OperationName.ps1
    Get-OperationName -Name trace_query_events   # -> events
#>

# CLI verb or MCP tool -> operation. An unmapped name returns itself, so a new
# surface addition shows up in a result as its own name rather than silently
# grading as something else.
$script:FiltraceOperations = @{
    'info'              = 'info'
    'trace_info'        = 'info'
    'rank'              = 'rank'
    'trace_rank'        = 'rank'
    'callers'           = 'callers'
    'trace_callers'     = 'callers'
    'tree'              = 'tree'
    'trace_tree'        = 'tree'
    'source'            = 'source'
    'trace_lines'       = 'source'
    'trace_heatmap'     = 'source'
    'processes'         = 'processes'
    'trace_processes'   = 'processes'
    'classify'          = 'classify'
    'trace_classify'    = 'classify'
    'timeline'          = 'timeline'
    'trace_timeline'    = 'timeline'
    'diff'              = 'diff'
    'trace_diff'        = 'diff'
    'batch'             = 'batch'
    'trace_batch'       = 'batch'
    'events'            = 'events'
    'trace_query_events' = 'events'
    'export'            = 'export'
    'trace_export'      = 'export'
    'lifecycle'         = 'lifecycle'
    'trace_lifecycle'   = 'lifecycle'
    'trace_gc'          = 'gc'
    'trace_jit'         = 'jit'
    'trace_threadpool'  = 'threadpool'
    'trace_diskio'      = 'diskio'
    'cache'             = 'cache'
    'collect'           = 'collect'
}

# The distinct operation names, for validating a task's expectOperations /
# forbidOperations against the vocabulary rather than accepting a typo.
function Get-KnownOperations {
    return @($script:FiltraceOperations.Values | Sort-Object -Unique)
}

function Get-OperationName {
    param([Parameter(Mandatory)][string]$Name)
    $key = $Name.Trim()
    if ($script:FiltraceOperations.ContainsKey($key)) { return $script:FiltraceOperations[$key] }
    return $key
}
