# filtrace workflow (single source)

This page is the **single source of truth** for filtrace's workflow text. Some of
its marked blocks are embedded verbatim into other surfaces and guarded by
[tools/Test-Docs.ps1](../tools/Test-Docs.ps1), which fails CI if a copy drifts:
the `verbs` block into the shipped skill
([../.agents/skills/filtrace/SKILL.md](../.agents/skills/filtrace/SKILL.md)) and the
`agents-snippet` block into the [README](../README.md). The `tools` block is
reference-only - it is not embedded anywhere, but the drift check asserts every
MCP tool appears in it. Edit a block here, then run `tools/Test-Docs.ps1 -Fix` to
refresh the embedded copies.

filtrace is a command-line and MCP trace analyzer. It reads EventPipe
(`.nettrace`, `.speedscope.json`) and ETW (`.etl`) captures from both modern .NET
and .NET Framework, ranks where a metric goes, drills into one frame, and diffs
two runs. There is no GUI; output is dense text by default, or compact JSON.

## Getting a trace to analyze

filtrace records ETW captures itself - the `collect` verb launches an executable and
records an `.etl` (Windows, Administrator), no external recorder. For an EventPipe
`.nettrace` it defers to `dotnet-trace` (cross-platform, first-party); either way it
analyzes whatever a recorder produces. Record or produce one, then point filtrace (or
`trace_info`) at the file. Two capture worlds feed it, and the question decides which:

| Capture | Records | Elevation | Scope | Recorded by |
|---|---|---|---|---|
| EventPipe (`.nettrace`; a `.speedscope.json` export is cpu-only) | cpu, alloc, exceptions, contention, wait, gc, jit | none | one process | `dotnet-trace collect`, BenchmarkDotNet `-p EP` |
| ETW (`.etl`) | cpu, threadtime, native frames | Administrator | machine-wide | `filtrace collect`, BenchmarkDotNet `-p ETW`, PerfView, `wpr` |

Only an ETW `.etl` carries wall-clock (`threadtime`), the native GC / JIT / `memcpy`
split (`--native-symbols` + `classify`), and multi-process scoping (`processes` +
`--process`); reach for it when the question is "CPU-bound or blocked?", "GC versus
my code?", or "which process is this?" - otherwise an EventPipe trace is the
lighter, no-elevation choice. Reading an `.etl` is itself Windows-only (the
ETW -> ETLX conversion), though the resulting `.etlx` then analyzes on any OS.

A machine-wide `.etl` also grows fast, so keep the capture lean. `filtrace collect`
enables only the CPU (and, for `threadtime`, context-switch) keywords and stacks only
the sampled events; it never turns on the File/Disk keywords, whose system-wide *name*
rundown enumerates every open file on the machine - hundreds of thousands of events
that dominate the trace no matter how short the window. Bound an open-ended run with
`--duration` (by time) or `--max-size-mb` (a circular buffer that keeps the last N MB).
Only a `diskio` capture needs those File/Disk keywords, and `collect` has
no switch for them: that capture comes from another recorder (PerfView, `wpr`, or
BenchmarkDotNet ETW), so expect the rundown there and trim it down afterward. Narrow
the analysis to your code with `--process` (lossless, so managed stacks survive) rather
than physically shrinking the file (see
[filtrace-etl-trimming.md](filtrace-etl-trimming.md)).

One EventPipe caveat: the `wait` family needs the non-default `WaitHandle` keyword
enabled at capture, so a plain `dotnet-trace collect` records the other families but
not waits (`rank --metric wait` then warns it found none).

For a BenchmarkDotNet capture, add `--keepFiles` so the kept build output supplies
the PDBs that resolve source lines, and **default every analysis to `--benchmark`**
to scope past the harness - not just the ranking verbs, `export` too, and not just
when the result looks noisy. `--benchmark` presets the root to the generated
`WorkloadAction*` wrapper, isolating the measured `[Benchmark]` code from the
bootstrap and warmup/overhead iterations a raw BDN trace otherwise dumps into the
same call tree. The bundled
[scripts/Capture-BenchmarkTrace.ps1](../.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1)
wraps the whole loop: it runs the benchmark under the chosen profiler
(self-elevating for ETW, with visible progress), finds the newest trace, and prints
the next-step filtrace commands already scoped with `--process` and `--benchmark`.

To profile a whole executable project instead of a micro-benchmark, capture its
running output with `dotnet-trace` (EventPipe) or `filtrace collect` (ETW). Build first
and launch the built app directly - `dotnet run` forks your program into a separate
process, so a single-process EventPipe session would trace the build/run host, not
your code (see the trap catalog). The bundled
[scripts/Capture-ProjectTrace.ps1](../.agents/skills/filtrace/scripts/Capture-ProjectTrace.ps1)
does this: it builds the project, resolves the run target, traces it under the
chosen profiler, and prints the next-step filtrace commands.

Once you have an `export`, two more bundled scripts open it in a hosted viewer with the
profile already loaded, no manual upload:
[scripts/Open-SpeedscopeTrace.ps1](../.agents/skills/filtrace/scripts/Open-SpeedscopeTrace.ps1)
serves a `--format speedscope` profile to speedscope.app (defaulting to the Left Heavy
hotspot view), and
[scripts/Open-PerfettoTrace.ps1](../.agents/skills/filtrace/scripts/Open-PerfettoTrace.ps1)
serves a `--format chromium` trace to the Perfetto UI. Each hosts the file on a one-shot
loopback listener, so nothing is uploaded.

## The canonical investigation: orient -> rank -> drill -> compare

Almost every investigation is the same four moves, and the verbs and MCP tools
are named for them:

1. **Orient.** Read the trace's format, sample count, and symbol-resolution rate
   first (`filtrace processes` / `trace_info`). A symbol-resolution rate below
   **0.8** means managed frames are missing and the rankings cannot be trusted -
   pass a `--symbols <build-output-dir>` before reading further. `trace_info` also
   reports which analyses the trace's format can answer and hints the metric that
   matches the symptom, so a vague "why is this slow?" reaches an applicable view.
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
| `rank --metric <m>` | any metric (`cpu`, `alloc`, `exceptions`, `threadtime`, `contention`, `wait`) | per metric |
| `cpu` | CPU self/inclusive time | `.nettrace`, `.etl`, `.speedscope.json` |
| `alloc` | bytes allocated, by site | `.nettrace` |
| `exceptions` | exception types, by count | `.nettrace` |
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
| `export --format <fmt>` | write a flame graph for a viewer - `speedscope` or `chromium` |

**Structured reports:**

| Verb | Reports |
|---|---|
| `gcstats` | GC counts, pauses, heap summary (`.nettrace`) |
| `jitstats` | JIT method count, compile time, sizes (`.nettrace`) |
| `threadpool` | worker-thread adjustments and starvation - slow under load, CPU idle (`.nettrace`) |
| `diskio` | physical disk I/O by file: bytes and disk service time (`.etl`, Windows) |
| `events --name <n>` | raw events by name, paged (`.nettrace`, or `.etl` on Windows) |

**Capture** - record a Windows ETW `.etl` yourself (for an EventPipe `.nettrace`, use `dotnet-trace`):

| Verb | Does |
|---|---|
| `collect` | launch an executable and record a CPU / thread-time `.etl` (Windows, Administrator) |

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

The MCP server exposes the same analysis as fifteen `trace_*` tools over stdio.
Every tool returns one envelope - a `schemaVersion`, a `warnings` list, next-step
`hints`, and the typed result - and the read-only analysis tools are annotated
`readOnlyHint`.

| Tool | CLI equivalent | Purpose |
|---|---|---|
| `trace_info` | (orient) | format, sample count, symbol-resolution rate, available analyses; call first |
| `trace_rank` | `rank` / `cpu` / `alloc` / `exceptions` / `threadtime` | rank by `metric` (cpu, threadtime, alloc, exceptions, contention, wait) |
| `trace_callers` | `callers` | immediate callers of a frame |
| `trace_lines` | `lines` | hottest source lines of the scoped methods |
| `trace_heatmap` | `heatmap` | per-line heat for one source file |
| `trace_tree` | `tree` | top-down call tree from the root |
| `trace_processes` | `processes` | processes by weight, to pick a scope |
| `trace_classify` | `classify` | CPU time by runtime work category |
| `trace_diff` | `diff` | what changed between two traces |
| `trace_export` | `export` | write a speedscope / chromium flame graph (write tool) |
| `trace_gc` | `gcstats` | GC counts, pauses, % time in GC, induced, heap |
| `trace_jit` | `jitstats` | JIT compile time and sizes |
| `trace_threadpool` | `threadpool` | worker-thread adjustments and starvation |
| `trace_diskio` | `diskio` | physical disk I/O by file (bytes, disk time) |
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
exceptions, threadtime, contention, wait); drill the hot frame with callers / lines / tree; diff
against a baseline to see what changed.
<!-- filtrace:end agents-snippet -->
