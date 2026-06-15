# filtrace workflow (single source)

This page is the **single source of truth** for filtrace's workflow text. The
shipped skill ([../skills/filtrace/SKILL.md](../skills/filtrace/SKILL.md)) and the
[README](../README.md) embed the marked blocks below verbatim, and
[tools/Test-Docs.ps1](../tools/Test-Docs.ps1) fails CI if a copy drifts from this
source. Edit the blocks here, then run `tools/Test-Docs.ps1 -Fix` to refresh the
copies.

filtrace is a command-line and MCP trace analyzer. It reads EventPipe
(`.nettrace`, `.speedscope.json`) and ETW (`.etl`) captures from both modern .NET
and .NET Framework, ranks where a metric goes, drills into one frame, and diffs
two runs. There is no GUI; output is dense text by default, or compact JSON.

## The canonical investigation: rank -> drill -> compare

Almost every investigation is the same three moves, and the verbs and MCP tools
are named for them:

1. **Orient.** Read the trace's format, sample count, and symbol-resolution rate
   first (`filtrace processes` / `trace_info`). A symbol-resolution rate below
   **0.8** means managed frames are missing and the rankings cannot be trusted -
   pass a `--symbols <build-output-dir>` before reading further.
2. **Rank.** Find the hottest frames by a metric (`filtrace cpu|alloc|exceptions|threadtime`,
   or `rank --metric`). Self-time finds the leaf that burns the resource;
   inclusive time finds the subtree that drives it.
3. **Drill.** Follow the hot frame into detail: who calls it (`callers`), which
   source lines (`lines`, `heatmap`), or what it calls (`tree`).
4. **Compare.** Diff a run against a baseline (`diff`) to see what regressed or
   improved, or `export` a flame graph for a human.

<!-- filtrace:begin verbs -->
### CLI verbs

**Rank** - find the hottest frames by a metric:

| Verb | Ranks | Reads |
|---|---|---|
| `rank --metric <m>` | any metric (`cpu`, `alloc`, `exceptions`, `threadtime`) | per metric |
| `cpu` | CPU self/inclusive time | `.nettrace`, `.etl`, `.speedscope.json` |
| `alloc` | bytes allocated, by site | `.nettrace` |
| `exceptions` | throw sites, by count | `.nettrace` |
| `threadtime` | wall-clock (running + blocked) | `.etl` (Windows) |

**Drill** - follow a ranking into detail:

| Verb | Shows |
|---|---|
| `callers <frame>` | immediate callers of a frame |
| `lines` | hottest source lines of the scoped methods |
| `heatmap <file>` | per-line heat for one source file |
| `tree` | top-down call tree from the root |

**Inventory** - see what a (possibly machine-wide) capture holds:

| Verb | Shows |
|---|---|
| `processes` | processes by CPU-sample weight, to pick a `--process` target |
| `classify` | CPU time by runtime work category (zeroing / copying / GC / JIT) |

**Compare and export:**

| Verb | Does |
|---|---|
| `diff <before> <after>` | what got slower/faster between two traces |
| `export --format <speedscope\|chromium>` | write a flame graph for a viewer |

**Structured reports** (EventPipe `.nettrace`):

| Verb | Reports |
|---|---|
| `gcstats` | GC counts, pauses, heap summary |
| `jitstats` | JIT method count, compile time, sizes |
| `events --name <n>` | raw events by name, paged |

**File ops** - manage the ETLX conversion cache TraceEvent keeps beside a trace:

| Verb | Does |
|---|---|
| `convert` | build the ETLX cache up front |
| `clean` | remove the ETLX cache to force a rebuild |
<!-- filtrace:end verbs -->

## Scope to the relevant slice

An agent wants the smallest relevant slice, not a machine-wide firehose. filtrace
defaults to scenario scope and lets you tighten further:

- **`--process <name>` / `--all-processes`** - the stack verbs that read a
  multi-process `.etl` (`cpu`, `threadtime`, `rank`, `callers`, `lines`,
  `heatmap`, `tree`, `classify`) auto-scope to the busiest process tree (ranked by
  CPU-sample count) unless told otherwise. `alloc` and `exceptions` read a
  single-process `.nettrace`, so they have no process options. Run `processes`
  first to see what is in a capture.
- **`--root <frame>`** - scope a ranking to the subtree under a frame.
- **`--benchmark`** - preset the root to the BenchmarkDotNet measured-workload
  wrapper, isolating the `[Benchmark]` code from the harness and warmup. Mutually
  exclusive with `--root`.

## Symbols

Managed frames - including NGEN (`.NET Framework`) and ReadyToRun (modern .NET)
framework methods - resolve for free from the trace's own CLR rundown. Two opt-ins
go further:

- **`--symbols <build-output-dir>`** - resolve your own managed frames and source
  lines from the portable PDBs in a build-output directory. Needed for `lines` /
  `heatmap`, and to lift a low symbol-resolution rate.
- **`--native-symbols`** (CPU `.etl` only) - resolve the *unmanaged* runtime
  frames (GC, JIT, `memset` / `memcpy`, write barriers) from the Microsoft public
  symbol server. Off by default so analysis stays offline and deterministic; the
  first run downloads to `--symbol-cache`, later runs hit the cache. `classify`
  buckets these resolved native leaves into work categories.

<!-- filtrace:begin tools -->
### MCP tools

The MCP server exposes the same analysis as thirteen `trace_*` tools over stdio.
Every tool returns one envelope - a `schemaVersion`, a `warnings` list, next-step
`hints`, and the typed result - and the read-only analysis tools are annotated
`readOnlyHint`.

| Tool | CLI equivalent | Purpose |
|---|---|---|
| `trace_info` | (orient) | format, sample count, symbol-resolution rate; call first |
| `trace_rank` | `rank` / `cpu` / `alloc` / `exceptions` / `threadtime` | rank by `metric` (cpu, threadtime, alloc, exceptions) |
| `trace_callers` | `callers` | immediate callers of a frame |
| `trace_lines` | `lines` | hottest source lines of the scoped methods |
| `trace_heatmap` | `heatmap` | per-line heat for one source file |
| `trace_tree` | `tree` | top-down call tree from the root |
| `trace_processes` | `processes` | processes by weight, to pick a scope |
| `trace_classify` | `classify` | CPU time by runtime work category |
| `trace_diff` | `diff` | what changed between two traces |
| `trace_export` | `export` | write a speedscope / chromium flame graph (write tool) |
| `trace_gc` | `gcstats` | GC counts, pauses, heap summary |
| `trace_jit` | `jitstats` | JIT compile time and sizes |
| `trace_query_events` | `events` | raw events by name, paged |

The file-management verbs (`convert`, `clean`) stay CLI-only - they manage the
on-disk ETLX cache rather than return an analysis.
<!-- filtrace:end tools -->

## Output contract

Both heads share one envelope so an agent parses one shape:

- `schemaVersion` - the envelope version.
- `warnings` - resolution-rate gates, truncation notices, format guardrails.
- `hints` - the next step to take (a ranking points at the hottest frame's
  callers; a diff points at the frame that moved most; an empty scope steers
  toward widening).
- the typed result payload.

Text mode renders the same data as dense fixed-width tables; `--format json`
emits the envelope compact (single line, deterministically rounded) so it diffs
cleanly and stays cheap in tokens.

<!-- filtrace:begin agents-snippet -->
## Using filtrace from an AI agent

filtrace is built for an agent mid-investigation. Two ways to wire it in:

- **MCP server** - add the stdio server so the agent calls the `trace_*` tools
  directly:

  ```json
  {
    "servers": {
      "filtrace": {
        "type": "stdio",
        "command": "dnx",
        "args": ["KlutzyNinja.Filtrace.Mcp", "--yes"]
      }
    }
  }
  ```

- **CLI** - install the global tool (`dotnet tool install -g KlutzyNinja.Filtrace`)
  and let the agent shell out to `filtrace <verb>`.

Either way, the canonical loop is **orient -> rank -> drill -> compare**: read
`trace_info` first and trust the rankings only when the symbol-resolution rate is
at or above 0.8; rank by the metric that matches the question (cpu, alloc,
exceptions, threadtime); drill the hot frame with callers / lines / tree; diff
against a baseline to see what changed.
<!-- filtrace:end agents-snippet -->
