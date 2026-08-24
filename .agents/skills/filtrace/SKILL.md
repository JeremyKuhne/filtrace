---
name: filtrace
description: Analyze .NET CPU, allocation, exception, GC, JIT, and wall-clock (thread-time) data in .nettrace, .etl, and speedscope files with the filtrace CLI or MCP server. Use when a user asks where time or allocation volume goes in a trace or benchmark, which method or source line is hot, why a run regressed against a baseline, what a captured .nettrace / .etl contains, or to rank / drill / diff / export a profile - including profiling .NET Framework (net481) via ETW, where an EventPipe ranking would mislead. Also covers capturing the trace first - choosing EventPipe vs ETW, elevation, and the recording tool (dotnet-trace, BenchmarkDotNet, PerfView, wpr).
license: MIT
compatibility: Pairs with the filtrace MCP server (the KlutzyNinja.Filtrace.Mcp package, run via `dnx`) for in-agent tool calls; otherwise shells out to the filtrace CLI (the KlutzyNinja.Filtrace global tool). Both heads share the analysis core; capture, cache operations, and all-process ETW widening are CLI-only.
metadata:
   portability: repo-specific
   applicability: tool-shipped
   binding: optional-overlay
   risk: local-write
   maturity: stable
   requires: none
   related: performance-testing
---

# Analyzing .NET traces with filtrace

If `overlay.md` exists beside this file, read it before acting; it contains
consumer-specific bindings. This core remains usable without it.

filtrace ranks CPU / allocation / exception / contention / wait / activity /
thread-time data, reports GC / JIT / thread-pool / disk / process-lifecycle activity,
and drills into, diffs, or exports CPU profiles from `.nettrace`, `.etl`, and speedscope
captures. It reads both modern .NET and .NET Framework traces. It is a command-line tool
and an MCP server - there is no GUI. Output is dense text by default, or compact JSON
(`--format json`); the analyzer itself runs on .NET 10.

This skill is the *how*; the full reference is single-sourced in
[docs/workflow.md](https://github.com/JeremyKuhne/filtrace/blob/main/docs/workflow.md)
and [docs/traps.md](https://github.com/JeremyKuhne/filtrace/blob/main/docs/traps.md).

## Getting a trace to analyze

filtrace records ETW captures itself - the `collect` verb launches an executable and
records an `.etl` (Windows, Administrator) - and otherwise analyzes traces other tools
record; for an EventPipe `.nettrace`, that recorder is `dotnet-trace` (cross-platform).
Record or produce one, point a verb - or `trace_info` - at it, then pick by the question:

- **EventPipe** (`.nettrace`) - cross-platform, no elevation, single process. From
   `dotnet-trace collect` or BenchmarkDotNet `-p EP`. It can carry CPU,
   allocations, exceptions, contention, thread-pool, GC, and JIT data when the
   corresponding providers/keywords are enabled; activities require their application
   provider enabled. .NET 9+ wait-handle analysis needs a non-default capture keyword
   (recipes below).
   BenchmarkDotNet may also derive a CPU-only `.speedscope.json`; prefer the raw
   `.nettrace` when both exist.
- **ETW** (`.etl`) - **Windows only, needs Administrator** (kernel sampling),
  machine-wide. From `filtrace collect`, BenchmarkDotNet `-p ETW`, PerfView, or `wpr`.
  It is the *only* source for wall-clock (`threadtime`), the native GC / JIT / `memcpy` split
  (`--native-symbols` + `classify`), and multi-process scoping (`processes` +
  `--process`). For a command that finishes in tens of milliseconds, see trap 13.

So "where's the time / what allocates" on one process -> EventPipe; "CPU-bound or
blocked?", "GC versus my code?", or a machine-wide capture -> ETW. Bundled helpers:

- [scripts/Capture-BenchmarkTrace.ps1](scripts/Capture-BenchmarkTrace.ps1) captures isolated BenchmarkDotNet cases with exact identity and symbol verification.
- [scripts/Capture-ProjectTrace.ps1](scripts/Capture-ProjectTrace.ps1) builds and traces an executable directly - never through `dotnet run`.
- [scripts/Capture-CommandTrace.ps1](scripts/Capture-CommandTrace.ps1) repeats short commands inside one ETW session and writes a manifest for `batch` / `diff`.
- [scripts/Open-SpeedscopeTrace.ps1](scripts/Open-SpeedscopeTrace.ps1) and
   [scripts/Open-PerfettoTrace.ps1](scripts/Open-PerfettoTrace.ps1) open exports through a one-shot loopback host without uploading them.
- [scripts/Invoke-FiltraceAnalysis.ps1](scripts/Invoke-FiltraceAnalysis.ps1) records decisive read-only queries with exact argv and input/output hashes; `-ReplayFrom` rejects changed plan or trace bytes before executing a query.

Capture helpers derive analysis availability from `filtrace info`, retain logs and
manifests, and never fabricate an enabled provider. Keep analysis plans to accepted
orientation, attribution, allocation, and comparison evidence - not every dead end.

A plain `dotnet-trace collect` captures CPU, runtime contention, and the structured
runtime reports its profile selects, but two metrics need an explicit provider on the
command line. `rank --metric wait` needs a .NET 9+ process captured with the runtime's
default keywords plus `WaitHandle` (`0x40000000000`); the combined mask for the runtime
used here is `0x414C14FCCBD`. Activity ranking and `--activity` CPU scope need completed
EventSource Start/Stop pairs - matching `OperationStart` / `OperationStop` events, or
explicit Start/Stop opcodes - **and that application provider enabled during capture**.
Level `5` is Verbose; an all-ones mask enables every keyword:

```pwsh
# for rank --metric wait
dotnet-trace collect --profile dotnet-common,dotnet-sampled-thread-time `
   --providers Microsoft-Windows-DotNETRuntime:0x414C14FCCBD:5 -- <app> <args>
# for metric activity / --activity scope
dotnet-trace collect --profile dotnet-common,dotnet-sampled-thread-time `
   --providers MyCompany-RequestSource:0xFFFFFFFFFFFFFFFF:5 -- <app> <args>
```

`Capture-ProjectTrace.ps1` queries `list-profiles` before build/launch, prefers the current CPU pair, records the effective recorder contract, and fails before the workload when no known profile mapping exists.

## The workflow: orient -> rank -> drill -> compare

Almost every investigation is the same four moves:

1. **Orient.** Read the trace's format, sample count, and symbol-resolution rate
   first - `filtrace info <trace>` or the `trace_info` tool. A rate **below 0.8**
   fires a quality warning: inspect the unresolved rows before trusting frame names.
   Managed method names normally come from the capture's CLR rundown; `--symbols`
   supplies matching PDBs for source lines, not a replacement for missing rundown, and
   the rate measures frame names only. Unresolved native ETW frames can depress the
   aggregate while managed-method rankings remain usable; use `--native-symbols` when
   the native runtime split matters. Before any source-line analysis, inspect
   `sourceResolution` - trap 2 has the full field-by-field procedure.
   Check `availableAnalyses` before selecting a metric, then read
   `analyses.<name>`: `captureStatus` and `eventCount` distinguish enabled-zero,
   disabled, observed, and unknown provider state.
   In shell automation, `filtrace info --strict` exits 3 below the 0.8 CPU
   frame-name threshold. Add `--require-enabled <names>` when provider enablement
   is required (enabled-zero passes), or `--require-events <names>` when a positive
   event count is required. The full info envelope is still emitted on rejection.
2. **Rank.** Find the hottest frames with `rank --metric <m>`.
   Self-time finds the leaf that burns the resource; inclusive-time finds the
   subtree that drives it.
3. **Drill CPU.** For an unwindowed CPU ranking, follow the hot frame with
   `callers <frame>` (who calls it), `source --view lines|heatmap` (which source
   lines), or `tree` (what it calls). These tools read CPU stacks only. For alloc,
   exceptions, contention, wait, activity, or threadtime, compare self/inclusive
   rankings or refine `root` / `time` instead of crossing into a CPU drill.
4. **Compare.** `diff <before> <after>` accepts traces or capture manifests and
   reports absolute plus normalized changes. `batch <manifest>` runs one compact
   ranking query across every case; `export --format speedscope` hands a human a
   flame graph.

```pwsh
filtrace info app.nettrace                   # 1. orient: format, symbol rate, analyses
filtrace rank app.nettrace --metric cpu      # 2. rank self-time
filtrace callers app.nettrace MyApp.Parse    # 3. who drives the hot frame
filtrace source app.nettrace --view lines --symbols bin/Release/net10.0   # 3. hot source lines
filtrace diff before.nettrace after.nettrace # 4. what changed
```

Choose the analysis from the symptom, confirm it appears in `availableAnalyses`,
then require `captureStatus: enabled` before interpreting a zero as an empty
workload:

| Symptom / question | Start with | What it establishes |
|---|---|---|
| CPU saturated or a hot loop | `rank --metric cpu` self, then inclusive / callers | executing leaf, then the subtree or caller driving it |
| Slow with low CPU | `rank --metric threadtime`, `rank --metric contention|wait`, or `report --kind threadpool` | broad on/off-CPU split, lock/handle waits, or pool starvation |
| High allocation rate or GC pauses | `rank --metric alloc`, then `report --kind gc` | sampled allocation volume by site, then collection/pause cost |
| Startup or first-call delay | `report --kind jit` | JIT count and compile cost |
| Repeated exceptions | `rank --metric exceptions` self, then inclusive | thrown types, then the paths that throw them |
| One captured request or job is slow | metric `activity`, then CPU scoped with `activity` | completed activity paths, then CPU inside the named operation |
| A spike occurs at an unknown time | `timeline`, then `rank --time` | the busy window, then its stacks |
| A command finishes in tens of milliseconds | `lifecycle`, then `rank --metric cpu` (trap 13) | wall-clock phases first; sampled CPU alone cannot explain a blocked command |
| Physical disk pressure | `report --kind diskio` (`.etl` with disk keywords) | files ranked by physical disk service time |

<!-- filtrace:begin verbs -->
### CLI commands

**Orient** - see what a capture holds before ranking:

| Verb | Shows |
|---|---|
| `info` | format, samples, frame-name and source/PDB quality, per-thread counts, per-analysis format/capture/event state, quality warnings, and optional shell acceptance gates - the CLI counterpart of `trace_info` |

**Rank** - find the hottest frames by a metric:

| Verb | Ranks | Reads |
|---|---|---|
| `rank --metric <m>` | any metric (`cpu`, `alloc`, `exceptions`, `threadtime`, `contention`, `wait`, `activity`) | per metric |

**CPU drill** - follow a CPU ranking into detail:

| Verb | Shows |
|---|---|
| `callers <frame>` | immediate CPU callers of a frame, or a caller/callee view with `--callees` |
| `source --view lines|heatmap` | hottest CPU source lines, or per-line heat for one source file |
| `tree` | top-down CPU call tree from the root |

**Inventory** - see what a (possibly machine-wide) capture holds:

| Verb | Shows |
|---|---|
| `processes` | processes by CPU-sample weight, to pick a `--process` or `--pid` target |
| `classify` | CPU time by runtime work category (zeroing / copying / GC / JIT) |

**Temporal** - see what happened when, to find the window to drill:

| Verb | Shows |
|---|---|
| `timeline` | per-bucket GC / CPU / exception / allocation / JIT activity across the trace |

**Compare and export:**

| Verb | Does |
|---|---|
| `diff <before> <after>` | absolute and normalized CPU changes; trace pairs or paired manifests |
| `batch <manifest>` | one compact metric ranking across every parameterized manifest case |
| `export --format <fmt>` | write a flame graph for a viewer - `speedscope` or `chromium` |

**Structured reports:**

| Verb | Reports |
|---|---|
| `report --kind gc|jit|threadpool|diskio` | bounded GC, JIT, thread-pool, or physical disk-I/O report |
| `lifecycle` | per-invocation wall-clock phases: root lifetime, time to first child, child span, teardown (`.etl`, Windows) |
| `events --name <n>` | raw events, filtered by name / payload / pid / tid, paged (`.nettrace`, or `.etl` on Windows) |

**Capture** - record a Windows ETW `.etl` yourself (for an EventPipe `.nettrace`, use `dotnet-trace`):

| Verb | Does |
|---|---|
| `collect` | launch an executable and record a CPU / thread-time `.etl` (Windows, Administrator) |

**File ops** - manage the ETLX conversion cache TraceEvent keeps beside a trace:

| Verb | Does |
|---|---|
| `cache --action convert|clean` | build/reuse or remove the ETLX cache |

Same-trace conversions are coordinated by canonical path across threads and
processes. filtrace converts to a unique sibling temporary file and atomically
publishes the completed cache, so MCP calls against one trace may run in parallel;
different traces remain independent. `trace_info.etlxCacheState` and
`cache --action convert` report `hit`, `waited`, `converted`, or `recovered`
(`null` for speedscope). `cache --action clean` waits for an active conversion
before removing its cache.
<!-- filtrace:end verbs -->

Run `filtrace <verb> --help` for the full option set of any verb.

## Scope and symbols

<!-- filtrace:begin scopes -->
**Implemented scope inventory:**

- **Named process:** CLI `info`, `rank`, `source`, `callers`, `tree`, `classify`,
  `timeline`, `diff`, `batch`, and `export`; MCP
  `trace_info`, `trace_rank`, `trace_callers`, `trace_lines`, `trace_heatmap`,
  `trace_tree`, `trace_classify`, `trace_timeline`, `trace_diff`, `trace_batch`, and
  `trace_export`. These auto-scope a multi-process `.etl` to the busiest process tree.
  Run `processes` / `trace_processes` first to inspect the capture, then set
  `--process <name>` / `process` to override. CLI commands expose `--all-processes`
  where an aggregate is supported; MCP has no all-process aggregate.
- **Exact process ids:** the same commands and tools accept `--pid <id>[,<id>]`
  (comma-separated, not repeated) / `pid` instead of a name. A name substring is right
  for discovery, but a common host name such as `dotnet` matches every unrelated
  instance in a machine-wide capture and ranks them together; an exact id set cannot.
  Prefer it for manifests and automation. The three selectors are mutually exclusive,
  an id reused by two processes in one trace is refused rather than merged, and an id
  that is not in the trace is reported.
- **Descendants:** the same commands and tools accept `--children include|exclude` /
  `children`. Both selectors follow descendants by default, because the common capture
  shapes put the measured work in a child the host launched. Pass `exclude` to separate
  a parent's own CPU from a child runtime's; without it a native host's own cost is
  blended with the CoreCLR frames of the child it launched.
- **Invocation roots:** CLI `lifecycle` and MCP `trace_lifecycle` take the same
  `--process` / `--pid` selectors, but each matched process instance is one invocation
  and descendants always follow, so neither takes `--children` or `--all-processes`.
- **Root subtree:** CLI `rank`, `callers`,
  `tree`, `classify`, `diff`, `batch`, and `export`; MCP `trace_rank`,
  `trace_callers`, `trace_tree`, `trace_classify`, `trace_diff`, `trace_batch`, and
  `trace_export`. Set `--root <frame>` / `root` to keep the subtree under a frame.
  Root filtering is stack ancestry, not causal correlation: stacks without the
  selected frame are excluded, including sibling workers. Root-aware structured
  results identify `rootKind: stackAncestry` and report available versus retained
  weight and record counts; direct diffs report both sides, and manifest batch/diff
  report each case. Use an instrumented activity or validated time window for a
  parallel phase, and ETW `threadtime` when sampled CPU does not explain elapsed time.
- **BenchmarkDotNet workload:** CLI `rank`, `callers`, `tree`, `classify`, `diff`,
  `batch`, and `export` accept
  `--benchmark`; MCP `trace_rank`, `trace_callers`, `trace_tree`, `trace_classify`,
  `trace_diff`, `trace_batch`, and `trace_export` accept `benchmark: true`. The
  preset isolates the `WorkloadAction` subtree from harness and overhead scaffolding;
  it is mutually exclusive with an explicit root. `source` views are not
  root-aware, so narrow them by method/file and treat percentages as process-scoped
  whole-trace values.
<!-- filtrace:end scopes -->

- **Scope to a time window.** `rank --time <start>,<end>` (milliseconds relative to
  the trace start, either bound optional: `1000,5000`, `1000,`, or `,5000`) keeps
  only the samples anchored in the window. It applies to every metric, so it zooms
  a `.nettrace` / `.etl` ranking to the slice around a spike or one slow request
   (`.speedscope.json` is aggregate-only here and warns that the window was ignored).
- **Symbols.** Managed frames (including NGEN and ReadyToRun framework methods)
   resolve to method names from the trace's CLR rundown. `--symbols <dir>` supplies
   matching local PDBs for source-line attribution; it does not repair missing rundown
   names, and a same-named PDB is not a matching one (trap 2).
   `--native-symbols` (CPU `.etl` only, opt-in, network) names the
   unmanaged GC / JIT / `memcpy` frames that otherwise show as a `?` leaf.

## Interpret and report the evidence

- Read `warnings` before the payload and use `hints` as candidate next steps. An
   empty or poorly resolved result is a reason to fix scope/symbols, not evidence
   that the behavior does not exist.
- State the trace format, selected process/root/time window, metric, and
   self-versus-inclusive measure with the finding. Percentages are relative to that
   scope; CPU milliseconds are sampled estimates scaled to the effective sample
   interval, not exact elapsed duration, and inclusive rows along one stack overlap
   (trap 14).
- Keep counts separate from weight, and read the query's own count rather than the
   trace's: rankings and callers expose `contributingRecordCount`, lines and heat maps
   expose attributed and unattributed counts, and `scopeWeight` is metric weight, never
   a record count. The 200-record method and 1,000-record line thresholds apply only to
   periodic CPU sampling; evented speedscope records are duration intervals, so report
   their count without applying those thresholds (trap 5).
- `alloc` attributes `GCAllocationTick` volume to allocation sites. It does **not**
   report retained bytes, object reachability, or GC-root paths, so it cannot prove a
   memory leak; use a heap snapshot/dump tool for retention.
- `threadtime` aggregates running and blocked intervals across threads. Do not call
   its total a request's latency unless the scope isolates that request/thread.
- `lifecycle` reports wall clock from kernel process events, not sampled CPU; a parent's
   lifetime contains its children's, and clipped invocations are excluded from the
   medians (trap 14).
- `contention`, `wait`, and `activity` pair Start/Stop events. An operation still
   open at trace end may be absent; an empty ranking does not rule out an active
   hang. Use ETW threadtime or a dump/current-state tool when the unfinished state
   itself is the question.
- `diff` reports absolute weight, scope shares, percentage-point change, normalized
   weight change, and appearing/disappearing frames. Scope direct traces consistently
   with root/process/benchmark. Manifest cases pair only by exact benchmark plus
   parameters; per-operation fields require complete count and matching unit on both
   sides.
- `batch` / `trace_batch` returns one compact top-frame row and case-specific warnings
   for each of at most 24 manifest cases. Use the returned path with `rank` for detail.
- Chromium export reconstructs one aggregate synthetic track whose widths preserve
   sample weight. Its axis is not the capture's original timestamps, thread
   concurrency, or idle gaps; use `timeline` / `--time` for temporal conclusions.
- Report observations separately from hypotheses. A hot frame, high allocation
   site, or positive diff identifies where recorded cost landed; it does not by itself
   establish root cause or prove that a code change caused the difference.

## Traps

The recurring ways a .NET trace investigation goes wrong:

<!-- filtrace:begin traps -->
## Trap catalog

1. **Profile .NET Framework with ETW, never extrapolate from an EventPipe trace.**
   EventPipe (`.nettrace`) is modern-.NET-only and managed-only. The net10
   EventPipe ranking actively *misleads* for `net481`: weaker Framework inlining
   relocates the hot frame, so a method that is 1.5% self-time on the EventPipe
   trace can be 56% on the ETW (`.etl`) capture of the same workload. Capture
   net481 under ETW (`rank --metric threadtime|cpu` over an `.etl`) and rank that.

2. **Treat low symbol resolution as a quality gate, not an automatic rejection.**
   A rate below **0.8** (surfaced by `trace_info` / the load warning) means unresolved
   frames need inspection. Managed method names normally come from CLR rundown;
   `--symbols <build-output-dir>` supplies matching PDBs for source lines, not a
   replacement for missing rundown. The aggregate rate conflates managed and native
   frames, so a net481 ETW capture can read low while every *managed* leaf resolves
   correctly; in that case managed-method rankings remain usable, and
   `--native-symbols` is the relevant opt-in when the native runtime split matters.
   Conversely, 100% method-name resolution does not prove that any source line is
   available. Before `source`, inspect `trace_info.sourceResolution`:
   require the relevant module in `matchingPdbModules`, then report mapped versus
   sampled managed frames and `highestUnmappedModules`. When
   `pdbIdentityMismatchModules` names the module, the expected PDB filename exists
   but its GUID or age differs from the trace. For BenchmarkDotNet, use the generated
   child output retained with `--keepFiles`, not the outer project output. Once the
   relevant module appears in `matchingPdbModules`, compare
   `sourceMappedManagedMethodCount` with `sampledManagedMethodCount`; then use
   `unmappedNamedManagedFrameCount` and `highestUnmappedMethods` to quantify and
   identify named frames that still map to `<no source>`.

3. **On a machine-wide `.etl`, confirm the process before scoping.** filtrace
   auto-scopes to the busiest process tree ranked by **CPU-sample count** (a
   long-lived background service wins a wall-clock race but owns few samples), and
   that default is usually right - but run `processes` first to see what is in the
   capture, then pass `--process <name>` if the auto-pick is wrong. A common host
   name matches every unrelated instance of it; when that happens, scope to the exact
   `name(pid)` values the inventory printed with `--pid`.

4. **BenchmarkDotNet captures include the harness - scope with `--benchmark` by
   default, not as an afterthought.** A raw ranking (or export) of a BDN trace is
   mixed with orchestrator and overhead scaffolding outside your `[Benchmark]`.
   In the CLI, pass `--benchmark` to `rank`, `callers`, `tree`, `classify`,
   `diff`, `batch`, and `export`; in
   MCP, pass `benchmark: true` to `trace_rank`, `trace_callers`, `trace_tree`,
   `trace_classify`, `trace_diff`, `trace_batch`, and `trace_export`. The wrapper
   includes warmup and actual workload iterations; it excludes harness/overhead
   scaffolding, not warmup. This applies especially to export - a flame graph with
   the harness left in is not just noisy, its proportions are wrong. Do not
   substitute a benchmark method substring:
   if root/frame warnings report multiple definitions or depths, narrow the selector
   before trusting the result. `source` cannot preserve root scope; narrow
   them with their method/file filter and treat percentages as whole-trace.

5. **A healthy whole trace can still produce a statistically thin scoped result.**
   `trace_info.sampleCount` describes the loaded trace, while a root, focus method,
   method filter, or file filter may retain only a small subset. Read the query's
   `contributingRecordCount` or line-level attributed/unattributed counts separately
   from `scopeWeight`, which is metric weight rather than a record count. The default
   200-record method and 1,000-record line warnings apply only to periodic CPU
   sampling. Evented speedscope records are duration intervals: report their count,
   but do not treat it as a periodic sample confidence gate.

6. **A supported format does not prove the provider was enabled.**
   `availableAnalyses` is the format inventory only. Read
   `trace_info.analyses.<name>` before acting: observed events prove `enabled`;
   recorder metadata can prove `enabled` with zero events or `disabled`; without
   either, the status is `unknown`. Never relabel unknown as an empty workload.
   The bundled capture helpers write `<trace>.filtrace.json`; preserve that sidecar
   with the trace so enabled-zero stays distinguishable from disabled.

7. **Native runtime frames need `--native-symbols`.** Without it, the unmanaged
   share of a trace - GC, JIT, `memset` / `memcpy`, write barriers - shows as
   unresolved `?` leaves. Opt in (CPU `.etl` only; fetches PDBs from the Microsoft
   public symbol server, cached locally) to name them, then `classify` to get the
   zeroing-vs-copying-vs-GC-vs-JIT split. It is off by default so analysis stays
   offline and deterministic.

8. **Self-time and inclusive-time answer different questions.** Self-time finds
   the leaf that burns the resource; inclusive-time finds the subtree that drives
   it. Ranking by the wrong measure hides the frame you want - start with self for
   "what is hot", switch to inclusive for "what is responsible".

9. **Reading an `.etl` through filtrace is Windows-only.** The ETW -> ETLX
   conversion needs Windows, and direct `.etlx` input is not part of the current
   CLI or MCP surface. The `.etl` paths report a clean error off Windows. Do not
   serialize same-trace MCP calls as a workaround: filtrace now coordinates ETLX
   conversion across threads and processes, publishes atomically, and reports
   `hit`, `waited`, `converted`, or `recovered` in `trace_info.etlxCacheState`.

10. **The default fold list hides runtime leaves on purpose.** It folds
   `memmove`, write-barriers, and GC-poll helpers into their managed caller -
   right for "which method is hot", wrong for "what kind of work dominates". Use
   `--no-fold` (or `classify`) to let the native leaves rank on their own.

11. **Trace the built app, not `dotnet run`.** `dotnet run` builds and then forks
   your program into a separate child process, so a single-process EventPipe
   session launched with `dotnet-trace collect -- dotnet run ...` records the
   build/run host, not your code, and the hot frames never appear. Build first,
   then launch the built output directly (`dotnet-trace collect -- dotnet
   <app>.dll`, or `dotnet-trace collect -- <apphost>`); the bundled
   `Capture-ProjectTrace.ps1` resolves that run target for you.

12. **A machine-wide `.etl` can be huge - capture lean, then scope at analysis.**
   ETW kernel tracing is machine-wide, so the wrong keywords balloon the file: the
   File/Disk *name* rundowns enumerate every open file on the box (hundreds of
   thousands of events that dwarf the workload) no matter how short the window.
   `filtrace collect` enables only the sampled-profile, process, thread, and image-load
   kernel keywords - never the File/Disk or network ones - and stacks just the sampled
   events, so prefer it and bound open-ended runs with `--duration` or `--max-size-mb`
   (a circular buffer keeping the last N MB). Its CLR keywords are narrowed the same way,
   to managed-method naming plus the GC and exception events the timeline lanes read.
   Pick the set with `--profile`: `cpu` (default), `threadtime` (adds context switches,
   the most expensive), or `startup`, which keeps only the managed-naming keywords - use
   it when instrumentation must not change a short process's lifetime, and expect the GC,
   contention, and exception analyses to have no events.
   A `diskio` capture needs the File/Disk keywords, and `filtrace collect` has no
   switch for them: that capture comes from another recorder (PerfView, `wpr`, or a
   custom BenchmarkDotNet `EtwProfilerConfig` enabling `DiskIO` / `DiskFileIO`; plain
   `-p ETW` is CPU-only), so expect the system-wide rundown there and trim it down
   afterward. To focus a big capture on your code, scope at *analysis* time with
   `--process` or `--pid` (lossless - it keeps
   managed stacks); physically trimming the file by relogging is a transport-only
   optimization that currently drops JITted managed frames.

13. **A 30-100 ms command breaks the capture defaults, quietly.** Every failure here
   returns a plausible-looking trace. ETW session startup and flush cost roughly 900 ms
   and the CLR provider perturbs a short process hard, so a capture reporting a 197 ms
   lifetime against a 28 ms uninstrumented run is measuring the instrument - time the
   command outside a session first and keep that baseline. Then: `--profile startup` to
   stop paying for keywords a short run does not read; `--cpu-ms` below the 1 ms default,
   since 1 ms leaves a 50 ms command with tens of samples, though below the machine's
   floor Windows samples at the floor while echoing your request back, so take the rate
   from the effective interval `collect` reports rather than the one you passed or one
   read back from the OS; `--iterations` to amortize the session over repeated launches;
   and `--pid` with the manifest's exact ids, since a common host name matches every
   unrelated instance of it. For a Native AOT parent, rank *inclusive* - its cost sits in
   ancestors self-time never surfaces - and combine `--symbols` for your own native PDBs
   with `--native-symbols` for the host, runtime, and OS ones; they compose.

14. **Wall clock is not CPU, and inclusive rows do not add up.** A process blocked in the
   loader or waiting on a child owns no samples while it waits, so sampled CPU cannot
   explain a command whose elapsed time exceeds it - derive the phases from kernel process
   and image events (`lifecycle`) instead, and treat the gap between a root's lifetime and
   its sampled CPU as the blocked time. Two consequences when reporting: sampled
   milliseconds are an estimate scaled to the *effective* sample interval, not a measured
   duration; and inclusive rows along one stack contain each other, so summing them
   double-counts. An invocation whose start or stop the capture never observed is clipped
   to the capture window, making its lifetime a lower bound rather than a value.
<!-- filtrace:end traps -->

## CLI or MCP

The two heads share one analysis core, with deliberately different operational surfaces:

- **CLI** - `dotnet tool install -g KlutzyNinja.Filtrace`, then `filtrace <verb>`.
- **MCP server** - `dnx KlutzyNinja.Filtrace.Mcp` over stdio, exposing eighteen
  `trace_*` tools: one per verb above, minus the capture and ETLX-cache verbs.
  Each returns one envelope: a `schemaVersion`, a `warnings` list, next-step
   `hints`, and the typed result. MCP can auto-scope or select a named ETW process;
   use the CLI when the question requires `--all-processes`, capture, or ETLX cache
   operations.

See [docs/workflow.md](https://github.com/JeremyKuhne/filtrace/blob/main/docs/workflow.md)
for the full verb/tool reference and the MCP config snippet, and
[docs/traps.md](https://github.com/JeremyKuhne/filtrace/blob/main/docs/traps.md) for
the trap catalog.
