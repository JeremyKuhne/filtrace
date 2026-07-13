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
and .NET Framework, ranks several metrics, reports structured runtime activity,
and drills into, diffs, or exports CPU profiles. There is no GUI; output is dense
text by default, or compact JSON.

## Getting a trace to analyze

filtrace records ETW captures itself - the `collect` verb launches an executable and
records an `.etl` (Windows, Administrator), no external recorder. For an EventPipe
`.nettrace` it defers to `dotnet-trace` (cross-platform, first-party); either way it
analyzes whatever a recorder produces. Record or produce one, then point filtrace (or
`trace_info`) at the file. Two capture worlds feed it, and the question decides which:

| Capture | Records | Elevation | Scope | Recorded by |
|---|---|---|---|---|
| EventPipe (`.nettrace`; a `.speedscope.json` export is cpu-only) | can carry cpu, alloc, exceptions, contention, gc, jit, and threadpool when their providers/keywords are enabled; wait/activity need capture opt-ins below | none | one process | `dotnet-trace collect`, BenchmarkDotNet `-p EP` |
| ETW (`.etl`) | cpu, threadtime, native frames | Administrator | machine-wide | `filtrace collect`, BenchmarkDotNet `-p ETW`, PerfView, `wpr` |

Only an ETW `.etl` carries wall-clock (`threadtime`), the native GC / JIT / `memcpy`
split (`--native-symbols` + `classify`), and multi-process scoping (`processes` +
`--process`); reach for it when the question is "CPU-bound or blocked?", "GC versus
my code?", or "which process is this?" - otherwise an EventPipe trace is the
lighter, no-elevation choice. Reading an `.etl` through filtrace is Windows-only;
direct `.etlx` input is not part of the current CLI or MCP surface.

A machine-wide `.etl` also grows fast, so keep the capture lean. `filtrace collect`
enables only the CPU (and, for `threadtime`, context-switch) keywords and stacks only
the sampled events; it never turns on the File/Disk keywords, whose system-wide *name*
rundown enumerates every open file on the machine - hundreds of thousands of events
that dominate the trace no matter how short the window. Bound an open-ended run with
`--duration` (by time) or `--max-size-mb` (a circular buffer that keeps the last N MB).
Only a `diskio` capture needs those File/Disk keywords, and `collect` has
no switch for them: that capture comes from another recorder (PerfView, `wpr`, or
a custom BenchmarkDotNet `EtwProfilerConfig` enabling `DiskIO` / `DiskFileIO`;
plain `-p ETW` is CPU-only), so expect the rundown there and trim it down afterward. Narrow
the analysis to your code with `--process` (lossless, so managed stacks survive) rather
than physically shrinking the file (see
[filtrace-etl-trimming.md](filtrace-etl-trimming.md)).

One EventPipe caveat: the `wait` family is .NET 9+ and needs the non-default
`WaitHandle` keyword (`0x40000000000`) enabled at capture. Preserve the default
runtime keywords and add it; for the runtime used here the combined mask is
`0x414C14FCCBD`:

```pwsh
dotnet-trace collect --profile cpu-sampling `
  --providers Microsoft-Windows-DotNETRuntime:0x414C14FCCBD:5 -- <app> <args>
```

A plain `dotnet-trace collect` records the default runtime families but not waits
(`rank --metric wait` then warns it found none).

`trace_info` separates the format constraint from capture evidence:

- `availableAnalyses` lists the selectors filtrace supports for this file format;
- `analyses.<name>.captureStatus` is `enabled`, `disabled`, or `unknown` for each listed selector;
- `analyses.<name>.eventCount` is the capture-wide source-record count when enabled, including zero.

Observed source events always establish `enabled`. A zero is reported only when
recorder metadata proves the provider/keyword was enabled; without that metadata,
no events means `unknown`, not disabled. The bundled capture helpers write a bounded
`<trace>.filtrace.json` sidecar with the profile facts they know. Keep it beside the
trace when moving the capture. Arbitrary third-party traces without a sidecar remain
honest: supported families with no evidence are unknown.

Activity ranking and `--activity` CPU scope need completed EventSource Start/Stop
pairs **and that application provider enabled during capture**. Use matching
`OperationStart` / `OperationStop` events (or explicit Start/Stop opcodes) and add
the provider alongside CPU sampling, for example:

```pwsh
dotnet-trace collect --profile cpu-sampling `
  --providers MyCompany-RequestSource:0xFFFFFFFFFFFFFFFF:5 -- <app> <args>
```

Replace the provider name; level `5` is Verbose and the mask enables all keywords.

For a BenchmarkDotNet capture, add `--keepFiles` to retain its generated build output
and point `--symbols` at the generated child output containing the exact PDBs recorded
in the trace, not merely the outer project output. Confirm the match in
`trace_info.sourceResolution`: inspect `matchingPdbModules`, the mapped/total sampled
managed-frame counts, and `highestUnmappedModules` before trusting `lines` or
`heatmap`. Scope every
**root-aware stack analysis** to the generated `WorkloadAction*` wrapper - not just
rankings, export too, and not just when the result looks noisy. In the CLI,
`--benchmark` supplies that preset to every verb that offers it. In MCP, pass
`benchmark: true` to `trace_rank`, `trace_callers`, `trace_tree`, `trace_classify`,
and `trace_export`. Do not guess a benchmark method substring: root/frame warnings
report the total match count and list up to 25 full frame definitions with up to 10
observed depths each, marking omitted definitions/depths, plus which outermost/deepest
definition the query selected. Narrow an ambiguous selector before trusting
percentages. `lines` and `heatmap` have no root scope in either head: narrow them with
their method/file filter and remember their percentages still describe the
process-scoped whole trace. The workload wrapper includes warmup and actual iterations;
it isolates the `[Benchmark]` code from bootstrap and overhead scaffolding, not warmup
from measurement. The bundled
[scripts/Capture-BenchmarkTrace.ps1](../.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1)
wraps the whole loop: it runs the benchmark under the chosen profiler
(self-elevating for ETW) in a run-specific artifacts/log
directory, emits `manifest.json` with every parameterized case and trace pair, and
prints only commands whose `captureStatus` is known-enabled, already scoped with
`--process` and `--benchmark`; disabled and unknown analyses become explicit warnings.
Full BenchmarkDotNet output stays in `capture.log`. Use `-Format Json` for a compact
machine-readable handoff or `-Quiet` to suppress text progress/commands while retaining
warnings. On a non-fatal elevated wait timeout, text modes emit a warning;
`-Format Json` returns `status: "timeout"`, `runId`, `log`, and `message` instead of
empty stdout. The helper parses
BenchmarkDotNet's logged child `OutDir` values and uses `filtrace info` to put a
directory in `symbolsDirectory` only when its PDB identity maps sampled frames. A
same-project/same-TFM file-handle lock rejects overlapping captures immediately;
different projects or TFMs remain independent. The script never selects a globally
newest artifact, so stale traces cannot enter the manifest.

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
serves a `--format chromium` synthetic flame-graph trace to the Perfetto UI. Each
hosts the file on a one-shot loopback listener, so nothing is uploaded.

## The canonical investigation: orient -> rank -> drill -> compare

Almost every investigation is the same four moves, and the verbs and MCP tools
are named for them:

1. **Orient.** Read the trace's format, sample count, and symbol-resolution rate
  first (`filtrace info` / `trace_info`). A rate below **0.8** fires a quality
  warning: inspect the unresolved rows before trusting frame names. Managed method
  names normally come from CLR rundown; `--symbols <build-output-dir>` supplies
  matching PDBs for source lines, not a replacement for missing rundown. Unresolved
  native ETW frames can also depress the aggregate rate while managed-method
  rankings remain usable; use `--native-symbols` when the native runtime split
  matters. This rate measures frame names, not PDB/source quality. For source-line
  analysis, read `trace_info.sourceResolution`: its mapped and sampled managed-frame
  counts give the source-resolution rate, `matchingPdbModules` confirms exact PDB
  identity, `highestUnmappedModules` identifies where sequence points are missing,
  and `searchedDirectories` records where filtrace looked.
  `trace_info.availableAnalyses` reports format support only;
  `trace_info.analyses` reports capture enablement and observed event counts. Follow
  known-enabled symptom routes; an unknown status means inspect capture settings or
  recapture, not that the provider was disabled.
2. **Rank.** Find the hottest frames by a metric (`filtrace cpu|alloc|exceptions|threadtime`,
   or `rank --metric`). Self-time finds the leaf that burns the resource;
   inclusive time finds the subtree that drives it.
3. **Drill CPU.** For an unwindowed CPU ranking, follow the hot frame into detail:
  who calls it (`callers`), which source lines (`lines`, `heatmap`), or what it
  calls (`tree`). These tools read CPU stacks only. For alloc, exceptions,
  contention, wait, activity, or threadtime, compare self/inclusive rankings or
  refine `root` / `time` instead of crossing into a CPU drill.
4. **Compare.** Diff a run against a baseline (`diff`) to see what regressed or
   improved, or `export` a flame graph for a human.

### Route by symptom

Choose the analysis from the symptom, confirm it appears in `availableAnalyses`,
then require `captureStatus: enabled` before treating an empty result as a
meaningful zero:

| Symptom / question | Start with | What it establishes |
|---|---|---|
| CPU saturated or a hot loop | `cpu` self, then inclusive / callers | executing leaf, then the subtree or caller driving it |
| Slow with low CPU | `threadtime` (`.etl`), or `contention` / `wait` / `threadpool` (`.nettrace`) | broad on/off-CPU split, lock/handle waits, or pool starvation |
| High allocation rate or GC pauses | `alloc`, then `gcstats` | sampled allocation volume by site, then collection/pause cost |
| Startup or first-call delay | `jitstats` | JIT count and compile cost |
| Repeated exceptions | `exceptions` self, then inclusive | thrown types, then the paths that throw them |
| One captured request or job is slow | metric `activity`, then CPU scoped with `activity` | completed activity paths, then CPU inside the named operation |
| A spike occurs at an unknown time | `timeline`, then `rank --time` | the busy window, then its stacks |
| Physical disk pressure | `diskio` (`.etl` with disk keywords) | files ranked by physical disk service time |

<!-- filtrace:begin verbs -->
### CLI verbs

**Orient** - see what a capture holds before ranking:

| Verb | Shows |
|---|---|
| `info` | format, samples, frame-name and source/PDB quality, per-thread counts, per-analysis format/capture/event state, and quality warnings - the CLI counterpart of `trace_info` |

**Rank** - find the hottest frames by a metric:

| Verb | Ranks | Reads |
|---|---|---|
| `rank --metric <m>` | any metric (`cpu`, `alloc`, `exceptions`, `threadtime`, `contention`, `wait`, `activity`) | per metric |
| `cpu` | CPU self/inclusive time | `.nettrace`, `.etl`, `.speedscope.json` |
| `alloc` | bytes allocated, by site | `.nettrace` |
| `exceptions` | exception types, by count | `.nettrace` |
| `threadtime` | wall-clock (running + blocked) | `.etl` (Windows) |

**CPU drill** - follow a CPU ranking into detail:

| Verb | Shows |
|---|---|
| `callers <frame>` | immediate CPU callers of a frame, or a caller/callee view with `--callees` |
| `lines` | hottest CPU source lines of the scoped methods |
| `heatmap <file>` | per-line CPU heat for one source file |
| `tree` | top-down CPU call tree from the root |

**Inventory** - see what a (possibly machine-wide) capture holds:

| Verb | Shows |
|---|---|
| `processes` | processes by CPU-sample weight, to pick a `--process` target |
| `classify` | CPU time by runtime work category (zeroing / copying / GC / JIT) |

**Temporal** - see what happened when, to find the window to drill:

| Verb | Shows |
|---|---|
| `timeline` | per-bucket GC / CPU / exception / allocation / JIT activity across the trace |

**Compare and export:**

| Verb | Does |
|---|---|
| `diff <before> <after>` | absolute CPU sampled-time changes between comparable traces |
| `export --format <fmt>` | write a flame graph for a viewer - `speedscope` or `chromium` |

**Structured reports:**

| Verb | Reports |
|---|---|
| `gcstats` | GC counts, pauses, heap summary (`.nettrace`) |
| `jitstats` | JIT method count, compile time, sizes (`.nettrace`) |
| `threadpool` | worker-thread adjustments and starvation - slow under load, CPU idle (`.nettrace`) |
| `diskio` | physical disk I/O by file: bytes and disk service time (`.etl`, Windows) |
| `events --name <n>` | raw events, filtered by name / payload / pid / tid, paged (`.nettrace`, or `.etl` on Windows) |

**Capture** - record a Windows ETW `.etl` yourself (for an EventPipe `.nettrace`, use `dotnet-trace`):

| Verb | Does |
|---|---|
| `collect` | launch an executable and record a CPU / thread-time `.etl` (Windows, Administrator) |

**File ops** - manage the ETLX conversion cache TraceEvent keeps beside a trace:

| Verb | Does |
|---|---|
| `convert` | build the ETLX cache up front |
| `clean` | remove the ETLX cache to force a rebuild |

Same-trace conversions are coordinated by canonical path across threads and
processes. filtrace converts to a unique sibling temporary file and atomically
publishes the completed cache, so MCP calls against one trace may run in parallel;
different traces remain independent. `trace_info.etlxCacheState` and the `convert`
verb report `hit`, `waited`, `converted`, or `recovered` (`null` for speedscope).
`clean` waits for an active conversion before removing its cache.
<!-- filtrace:end verbs -->

## Scope to the relevant slice

An agent wants the smallest relevant slice, not a machine-wide firehose. filtrace
defaults to scenario scope and lets you tighten further:

- **Process scope** - the verbs that read a
  multi-process `.etl` (`cpu`, `threadtime`, `rank`, `callers`, `lines`,
  `heatmap`, `tree`, `classify`, `timeline`) auto-scope to the busiest process tree
  (ranked by CPU-sample count) unless told otherwise. `alloc` and `exceptions` read a
  single-process `.nettrace`, so they have no process options. Run `processes`
  first to see what is in a capture. Both heads accept a named process (`--process
  <name>` in the CLI, `process` in MCP); only the CLI accepts `--all-processes` to
  widen an analysis to the aggregate capture.
- **`--root <frame>`** - scope a ranking to the subtree under a frame.
- **BenchmarkDotNet workload scope** - preset the root to the measured-workload
  wrapper, isolating the `[Benchmark]` code from harness and overhead scaffolding.
  Use `--benchmark` in CLI verbs that offer it; in MCP use `benchmark: true` on
  `trace_rank`, `trace_callers`, `trace_tree`, `trace_classify`, and `trace_export`.
  The wrapper includes warmup and actual iterations. `lines` / `heatmap` are not
  root-aware; use their method/file filter and treat percentages as whole-trace. A
  benchmark preset is mutually exclusive with an explicit root. When using a root or
  frame substring, inspect warnings: they report the total match count, then list up
  to 25 full definitions and 10 depths per definition with omitted-count markers, plus
  the per-stack selection rule. Narrow an ambiguous selector before treating its
  percentages as evidence.
- **`--activity <name>`** (`rank`, cpu metric) - scope the CPU view to the samples
  taken inside one start-stop activity - a request, job, or operation - or a child
  of it. Answers "why is *this* request slow?".
- **`--time <start>,<end>`** (`rank`, any metric) - scope to a time window in
  milliseconds relative to the trace start, keeping only the samples anchored inside
  it. Either bound may be omitted (`1000,5000`, `1000,`, or `,5000`). Zooms every
  metric to the slice around a latency spike or one slow request for `.nettrace` /
  `.etl`. Speedscope evented/sampled time units are normalized to milliseconds for
  ranking, but speedscope is aggregate-only for this option and warns that the window
  was ignored.

## Symbols

Managed frames - including NGEN (`.NET Framework`) and ReadyToRun (modern .NET)
framework methods - resolve for free from the trace's own CLR rundown. Two opt-ins
go further:

- **`--symbols <build-output-dir>`** - map managed code to source lines using
  matching portable PDBs in a build-output directory. Needed for `lines` /
  `heatmap`; managed method names normally come from CLR rundown, so this is not a
  general repair for a low aggregate name-resolution rate. Read
  `trace_info.sourceResolution` to confirm exact PDB matches and sampled frame
  mapping. For BenchmarkDotNet, use the generated child output retained by
  `--keepFiles`; the outer project output can have the right filenames but different
  PDB identities.
- **`--native-symbols`** (CPU `.etl` only) - resolve the *unmanaged* runtime
  frames (GC, JIT, `memset` / `memcpy`, write barriers) from the Microsoft public
  symbol server. Off by default so analysis stays offline and deterministic; the
  first run downloads to `--symbol-cache`, later runs hit the cache. `classify`
  buckets these resolved native leaves into work categories.

<!-- filtrace:begin tools -->
### MCP tools

The MCP server exposes the same analysis core as sixteen `trace_*` tools over stdio.
Every tool returns one envelope - a `schemaVersion`, a `warnings` list, next-step
`hints`, and the typed result - and the read-only analysis tools are annotated
`readOnlyHint`.

| Tool | CLI equivalent | Purpose |
|---|---|---|
| `trace_info` | (orient) | format, samples, frame-name and source/PDB quality, available analyses; call first |
| `trace_rank` | `rank` / `cpu` / `alloc` / `exceptions` / `threadtime` | rank by `metric` (cpu, threadtime, alloc, exceptions, contention, wait, activity) |
| `trace_callers` | `callers` | immediate CPU callers of a frame, or a caller/callee view (`callees`) |
| `trace_lines` | `lines` | hottest CPU source lines of the scoped methods |
| `trace_heatmap` | `heatmap` | per-line CPU heat for one source file |
| `trace_tree` | `tree` | top-down CPU call tree from the root |
| `trace_processes` | `processes` | processes by weight, to pick a scope |
| `trace_classify` | `classify` | CPU time by runtime work category |
| `trace_diff` | `diff` | what changed between two traces |
| `trace_export` | `export` | write a speedscope / chromium flame graph (write tool) |
| `trace_timeline` | `timeline` | per-bucket GC / CPU / exception / allocation / JIT activity over time |
| `trace_gc` | `gcstats` | GC counts, pauses, % time in GC, induced, heap |
| `trace_jit` | `jitstats` | JIT compile time and sizes |
| `trace_threadpool` | `threadpool` | worker-thread adjustments and starvation |
| `trace_diskio` | `diskio` | physical disk I/O by file (bytes, disk time) |
| `trace_query_events` | `events` | raw events, filtered by name / payload / pid / tid, paged |

The capture and file-management verbs (`collect`, `convert`, `clean`) stay CLI-only.
MCP also omits the CLI's `--all-processes` widening switch; its ETW analyses use the
automatic busiest-process scope or an explicit `process` name.
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

## Interpret and report the evidence

- Read `warnings` before the payload and use `hints` as candidate next steps. An
  empty or poorly resolved result is a reason to fix scope/symbols, not evidence
  that the behavior does not exist.
- State the trace format, selected process/root/time window, metric, and
  self-versus-inclusive measure with the finding. Percentages are relative to that
  scope; CPU milliseconds are sampled estimates, not exact elapsed duration.
- Keep counts separate from weight. `trace_info.sampleCount` describes the loaded
  whole trace after process/activity/time filters; it does not establish that a
  narrower root/method/file query is well sampled. Stack rankings and callers expose
  `contributingRecordCount`; lines and heat maps expose `attributedRecordCount` and
  `unattributedRecordCount`. `scopeWeight` remains metric weight (CPU milliseconds,
  allocation bytes, event counts, or elapsed interval milliseconds), never a generic
  record count.
- The default 200-record method and 1,000-record line warnings apply only when the
  reader establishes periodic CPU sampling. Evented speedscope records are duration
  intervals: report their count separately from weight, but do not apply periodic
  sample thresholds. A `null` count means the source cannot establish a
  meaningful record count.
- `alloc` attributes `GCAllocationTick` volume to allocation sites. It does **not**
  report retained bytes, object reachability, or GC-root paths, so it cannot prove a
  memory leak; use a heap snapshot/dump tool for retention.
- `threadtime` aggregates running and blocked intervals across threads. Do not call
  its total a request's latency unless the scope isolates that request/thread.
- `contention`, `wait`, and `activity` pair Start/Stop events. An operation still
  open at trace end may be absent; an empty ranking does not rule out an active
  hang. Use ETW threadtime or a dump/current-state tool when the unfinished state
  itself is the question.
- `diff` compares absolute CPU sampled weights, not normalized percentages. Compare
  equivalent workloads, capture lengths, runtime/configuration, symbols, root, fold,
  and measure. It has no process selector and auto-scopes each ETW input separately,
  so first confirm the same workload is busiest in both captures.
- Chromium export reconstructs one aggregate synthetic track whose widths preserve
  sample weight. Its axis is not the capture's original timestamps, thread
  concurrency, or idle gaps; use `timeline` / `--time` for temporal conclusions.
- Report observations separately from hypotheses. A hot frame, high allocation
  site, or positive diff identifies where recorded cost landed; it does not by itself
  establish root cause or prove that a code change caused the difference.

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
`trace_info` (CLI: `filtrace info`) first; when symbol resolution is below 0.8,
inspect its warning and unresolved rows. Treat that as frame-name quality; before
source-line analysis, inspect `sourceResolution` for exact matching PDB modules,
mapped sampled managed frames, searched directories, and highest-unmapped modules.
Use the generated BenchmarkDotNet child output when the outer build PDB does not
match; use native symbols for CPU ETW runtime frames as applicable. Rank by the metric that matches the question (cpu, alloc, exceptions,
threadtime, contention, wait, activity); for an unwindowed CPU ranking, drill the
hot frame with callers / lines / tree; diff comparable CPU traces against a baseline.
<!-- filtrace:end agents-snippet -->
