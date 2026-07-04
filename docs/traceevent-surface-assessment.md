# TraceEvent surface audit and API-expansion assessment

**Status:** TE-1 to TE-8 (lock contention, waits, GC-report depth, exception-by-type,
ThreadPool starvation, thread-time blocked-reason leaves, disk-I/O-by-file, and
request/activity scoping), TE-11 (agentic discoverability), TE-12 (raw event query over
`.etl`), TE-13 (capture-size cap), and TE-15 (time-window `--time` scope) have landed;
TE-9, TE-10, and TE-14 are proposed. A living backlog of candidate analysis families and
enrichments, prioritized against filtrace's design intents.
**Date:** 2026-07-04
**Basis:** Reflection over the actually-referenced assembly
`Microsoft.Diagnostics.Tracing.TraceEvent` **3.2.3** (`lib/netstandard2.0`),
cross-checked against filtrace's current providers and
[implementation-plan.md](implementation-plan.md) (section 1A and Addendum A).

This page is ordinary prose (no drift-checked marked blocks). It feeds the
milestone roadmap in [implementation-plan.md](implementation-plan.md); when an item
here is scheduled or lands, update its **Status** and reflect it in the plan's
backlog. Each proposal is framed by the plain-language question a non-expert .NET
developer would ask; the "Agentic flow" section below shows how an agent routes a
vague "why is this slow?" prompt to the right feature and discovers features it was
never told about.

## The lens: filtrace's design intents

Every proposal below is judged against the intents the plan already commits to:

1. **One engine, many providers.** Every analysis is `{stack, weight}` rolled into
   a call tree, so the shared `FoldingAggregator` gives `rank` / `callers` / `tree`
   / `lines` / `diff` / `export` for free. A new family is a *provider*, not new
   engine code.
2. **Agent-shaped.** Smallest relevant slice, deterministic compact output, a
   next-step nudge, and a deliberately small tool surface (13 `trace_*` tools).
3. **The capture axis is the value axis.** EventPipe (`.nettrace`) is no-elevation
   and cross-platform; ETW (`.etl`) is Windows plus Administrator. Extending the
   *default* EventPipe loop is worth more than an ETW-only addition.
4. **Own analysis; integrate capture and rendering.** Do not reimplement collectors
   or viewers.
5. **Scope-creep guard.** The plan resists PerfView parity on purpose; the backlog
   is the pressure valve. This list is prioritized, not exhaustive.

## Baseline: the current surface

Eight families over 19 verbs / 13 MCP tools:

- **Stack families** (ranked by the engine): CPU, ThreadTime (ETW), Alloc,
  Exceptions.
- **Structured reports:** GcStats, JitStats, EventQuery.
- **Engine and inventory:** rank, callers, lines, heatmap, tree, diff, export,
  processes, classify, info.

Already-catalogued gaps (Addendum A / backlog): net surviving heap
(`GCHeapSimulator` lift), retention / leak, PMC / CPU counters, any-event (partial
via EventQuery).

## Key audit findings

Verified by reflecting on the 3.2.3 assembly:

- **Three ready-made computers filtrace never wired up**, all deriving from
  `StartStopLatencyComputer` (entry point `GenerateStacks()`, plus
  `GetDefaultFoldPatterns()`) - the identical integration pattern used by the
  existing [ThreadTimeProvider](../src/Filtrace.Core/Tracing/Providers/ThreadTimeProvider.cs):
  - `ContentionLatencyComputer(TraceLog, MutableTraceEventStackSource)`
  - `WaitHandleWaitLatencyComputer(TraceLog, MutableTraceEventStackSource)`
  - `StartStopLatencyComputer` (request / activity latency)
- **Unused payload on events filtrace already reads:**
  `ExceptionTraceData.ExceptionType` / `.ExceptionMessage` (discarded today by
  [ExceptionsProvider](../src/Filtrace.Core/Tracing/Providers/ExceptionsProvider.cs)),
  and `TraceGC.Reason` / `.Type` / `.PromotedMB` / `.PercentTimeInGC` /
  `.PauseTimePercentageSinceLastGC` / `.SuspendDurationMSec` /
  `GlobalCondemnedReasons` (unused by `gcstats`).
- **Two corrections to Addendum A** (empirically verified absent from 3.2.3):
  - `GCHeapSimulator` is not present - the net-mem family stays a PerfView lift, as
    the plan already assumes.
  - `MemoryGraph` / `GCHeapDump` / `MemoryGraphStackSource` and any `Graphs`
    namespace are **not in this assembly**. Addendum A states the retention
    *analysis* is "already library code (GCHeapDump + MemoryGraph +
    MemoryGraphStackSource)"; that is **incorrect for TraceEvent 3.2.3**. The
    retention family needs a separate PerfView-side assembly and is therefore
    dependency-gated, not free.
  - `XmlStackSourceWriter` is likewise absent, confirming `export --format perfview`
    is PerfView-side (do not promise it cheaply). `ZippedETLWriter` *is* present
    (relevant to a future symbols-bundled `.etl` hand-off / the parked `trim`).

## Agentic flow: from a vague question to a feature

Most investigations do not begin with "rank CPU self-time." They begin with a
non-expert asking one of two things:

- **"Why is this slow?"** - diagnosis: find the dominant cost.
- **"How can I make this faster?"** - optimization: cut the biggest subtree, then
  prove the win.

Because filtrace is agent-shaped, a new family only pays off if an agent can *route*
a vague prompt to it without the user naming it. That routing has three parts: a
symptom map, discovery mechanisms, and the two loops.

### Symptom map (what a non-expert actually asks)

The developer describes a symptom in plain language; the agent maps it to a resource
and a family. Every row is a routine .NET situation, not an exotic one.

| The developer says... | Likely cost | Feature | Confirm with |
|---|---|---|---|
| "It maxes out a CPU core" | CPU-bound | CPU (shipping) | `rank cpu` (self time) |
| "It's slow but the CPU is idle" | blocked / waiting | contention (TE-1), wait (TE-2), threadtime | `rank metric=contention` |
| "It doesn't scale on more cores / threads fight" | lock contention | contention (TE-1) | `rank metric=contention` |
| "An await never comes back / it just stalls" | waiting on a handle or task | wait (TE-2), threadtime | `rank metric=wait` |
| "Memory climbs and it pauses" | allocation / GC | alloc (shipping), GC depth (TE-3) | `alloc`; `gcstats` |
| "It garbage-collects constantly" | alloc rate / gen-2 | GC depth (TE-3) | `gcstats` (reason, %GC) |
| "The logs are full of exceptions" | exception throughput | exceptions by type (TE-4) | `rank metric=exceptions` |
| "Fine on my machine, piles up under load" | threadpool starvation | threadpool report (TE-5) | `threadpool` |
| "It waits on the disk / database / network" | I/O | disk/file I/O (TE-7), blocked-leaf (TE-6) | `rank metric=diskio` |
| "Just this one endpoint is slow" | per-request | activity scoping (TE-8) | `--activity` |
| "Only the seconds around the spike matter" | a time slice | time-window scoping (TE-15) | `--time` |

That map is also the argument for the P0 items: today the "CPU is idle but it's
slow" row - one of the most common real complaints - has no cross-platform answer
(only ETW threadtime, which needs Windows and admin). TE-1 and TE-2 fill it on a
plain `.nettrace`.

### How the agent discovers and recommends a feature

A non-expert will never ask for "contention stacks" by name, so the agent has to
surface the option. Four mechanisms, in increasing order of leverage:

1. **Self-describing tool schema.** New families arrive as `metric` enum values on
   `trace_rank`, each with a one-line "answers ..." description. An agent
   enumerating the tool sees `contention` and `wait` as first-class choices with no
   prose knowledge required - precisely why the P0 items ship as metrics, not new
   tools.
2. **Format-capability orientation.** `trace_info` reports *which analyses the
   trace's format can answer* (`availableAnalyses`): allocation, exceptions,
   contention, wait, and the GC / JIT reports are EventPipe-only; thread time,
   classify, and the process inventory are ETW-only; a speedscope export is CPU-only.
   That format constraint is the most common discovery failure - trying `alloc` on an
   `.etl`, or `threadtime` on a `.nettrace`. It is a *format capability, not an
   event-presence guarantee*: whether the specific events were actually captured is a
   separate question each analysis answers with its own "no <x> events found" warning
   (some, like `wait`, need a non-default capture keyword, so a `.nettrace` still lists
   `wait` even when it was captured without it). True event-presence probing - so
   `trace_info` could say "this trace can answer CPU and allocation, but not blocking -
   recapture with X" - would need an event scan and is a further step.
3. **Symptom-to-family hints (highest leverage).** filtrace already returns a
   `hints` channel. Extend it so a *result* points at the next family: a CPU ranking
   whose total self-time is a small fraction of wall-clock hints "mostly off-CPU;
   try `metric=contention` / `wait` / `threadtime`"; a ranking dominated by native
   GC/JIT frames hints "runtime overhead is high; try `classify` and `gcstats`"; an
   exception count over a threshold hints "high throw rate; rank
   `metric=exceptions`." The agent follows a lead it did not have to know in advance.
4. **A single triage entry point (TE-11).** For the bare "why is this slow?" prompt,
   one `trace_triage` call runs orient plus the cheap dominant-resource checks and
   returns a ranked list of likely causes, each with the tool to confirm it - the
   most direct answer to the discovery problem: one vague question, one call, a
   prioritized menu.

### The two loops

- **"Why is this slow?" (diagnose):** orient (content-aware `trace_info`) -> triage
  (TE-11, or rank the dominant metric) -> confirm with the family the symptom map
  names -> drill (`callers` / `lines` / `tree`) to the responsible code. The agent
  narrows from resource to method without the user knowing any profiler vocabulary.
- **"How can I make this faster?" (optimize):** rank *inclusive* to find the biggest
  subtree worth cutting -> change the code -> `diff` against the baseline capture to
  prove the win (or catch a regression) -> `export` a flame graph for the human. The
  `diff` step is what turns a guess into a measured improvement, and it works on any
  family (reduce allocations, then diff the `alloc` profile).

## Prioritized proposals

Stable IDs (TE-n) so items can be tracked and referenced as they move.

| ID | Expansion | Capture | Cost | Surface fit | Priority | Status |
|---|---|---|---|---|---|---|
| TE-1 | Lock-contention family | `.nettrace` (+ `.etl` follow-up) | Low | new `metric` on `rank` / `trace_rank` | P0 | Landed (`.nettrace`) |
| TE-2 | Wait / blocking family | `.nettrace` (.NET 9+) | Low | new `metric` | P0 | Landed (`.nettrace`) |
| TE-3 | GC-report depth (reason / type / %GC / promoted) | `.nettrace` | Low | extend `gcstats` / `trace_gc` | P0 | Landed (`.nettrace`) |
| TE-4 | Exception grouping by type | `.nettrace` | Trivial | extend `exceptions` | P0 | Landed (`.nettrace`) |
| TE-5 | ThreadPool starvation report | `.nettrace` | Med | new structured verb / tool | P1 | Landed |
| TE-6 | Thread-time blocked-leaf split (disk / net / lock / paging) | `.etl` | Low-Med | enrich `threadtime` | P1 | Landed (`.etl`) |
| TE-7 | Disk-I/O and File-I/O families | `.etl` | Med | new `metric`(s) | P1 | Landed (disk) |
| TE-8 | Request / activity scoping (`--activity`) | both | Med-High | scope grammar + family | P2 | Landed (`.nettrace`) |
| TE-9 | PMC / CPU-counter ranking | `.etl` capture | Med | new `metric` | P2 | Proposed |
| TE-10 | Retention / leak (`.gcdump`) - re-scope | shell out | Med + new dependency | separate assembly | P2 | Proposed |
| TE-11 | Agentic discoverability (content-aware `trace_info` + symptom hints + triage) | both | Low-Med | cross-cutting; enables every row | P0 | Landed (info + hints) |
| TE-12 | Raw event query over `.etl` (extend `events` / `trace_query_events`) | `.etl` | Low | extend the `events` reader + guardrail | P3 | Landed |
| TE-13 | Capture size cap (circular buffer) | `.etl` capture | Low | new `collect --max-size-mb` option | P2 | Landed |
| TE-14 | Ship the process-tree `trim` as a verb | `.etl` relog | Med | new verb | P3 | Proposed |
| TE-15 | Time-window scope (`--time`) | both | Low | new `--time` option on `rank` / `trace_rank` | P3 | Landed (analysis-time) |

### P0 - high value, low cost, EventPipe-native, drops into the existing engine

**TE-1. Lock-contention family.** *A developer asks:* "My app won't go faster on
more cores and threads seem stuck - are they fighting over a lock?" *Applicability
to .NET:* very common - a `lock` around a shared cache or singleton, a hot
`ConcurrentDictionary`, connection or `HttpClient` pools, `static` mutable state
under load. `ContentionLatencyComputer` populates a `MutableTraceEventStackSource`
with contention-latency-weighted stacks (`ContentionFlags` separates managed from
native locks); walk it like `ThreadTimeProvider` and the `FoldingAggregator` ranks
it unchanged. *Why it matters here:* contention is visible today only through ETW
`threadtime` blocked time (Windows plus admin) or not at all on EventPipe; a
ready-made computer makes it near-zero work, shipping as `metric=contention` on
`rank` / `trace_rank` with no new tool. *Status:* the `.nettrace` path landed - the
`ContentionProvider` (strips the computer's synthetic `EventData` / `BROKEN` /
process / thread pseudo-frames so the leaf is the real blocking site), the
`metric=contention` wiring on `rank` / `trace_rank`, a `ContentionLoop` fixture, and
tests. The multi-process `.etl` path is the follow-up.

**TE-2. Wait / blocking family.** *A developer asks:* "It just stalls - my `await`
or `.Result` sits there and nothing uses the CPU. What is it waiting on?"
*Applicability to .NET:* ubiquitous in async/await code - blocking on `Task.Wait()`
/ `.Result`, `Monitor.Wait`, `SemaphoreSlim`, `ManualResetEventSlim`.
`WaitHandleWaitLatencyComputer` (same base and pattern) attributes waits from the
.NET 9+ `WaitHandleWait` events. *Why it matters here:* gives EventPipe a
cross-platform blocked-time view with no ETW. *Caveat:* needs the captured app on
.NET 9+ with the events enabled; guard with a clear warning like the existing format
guardrails. Can pair with TE-1 under one `blocking` shortcut. *Status:* the
`.nettrace` path landed - the `WaitProvider` (sharing a `LatencyStackReader` with
contention), the `metric=wait` wiring, a `WaitLoop` fixture captured with the
`WaitHandle` keyword explicitly enabled (it is not in the default set), and tests.

**TE-3. GC-report depth.** *A developer asks:* "Memory keeps climbing and the app
freezes for a beat now and then - is the garbage collector the problem, and why?"
*Applicability to .NET:* one of the most common .NET performance issues -
`string`/LINQ/boxing churn, large-object-heap traffic, gen-2 pressure. `gcstats`
already materializes `TraceGC`; surface the discriminators already parsed: `Reason`
(Induced / AllocSmall / LowMemory), `Type` (Blocking / Background), `Generation` and
gen-2 count, `PromotedMB`, `PercentTimeInGC`, `PauseTimePercentageSinceLastGC`,
`SuspendDurationMSec`. *Why it matters here:* counts and pauses do not tell a
non-expert what to do; "12% of time in GC, mostly gen-2, triggered by allocation"
points straight at cutting allocations. *Status:* landed - `gcstats` / `trace_gc` now
report `% time in GC` and the induced-collection count (which flagged that
BenchmarkDotNet's harness forces 6 of the 7 GCs in the fixture); `Reason`, `Type`,
`Generation`, and `PromotedMB` were already surfaced per collection.

**TE-4. Exception grouping by type.** *A developer asks:* "Something is throwing a
pile of exceptions and I think it's slowing things down - which exception, and from
where?" *Applicability to .NET:* common and often invisible - exceptions as control
flow, first-chance exceptions in parsing/validation, swallowed `catch` blocks.
`ExceptionTraceData.ExceptionType` is read-and-discarded today; add a synthetic
`Type <name>` leaf (mirroring `AllocationProvider`) or a `--by type` grouping. *Why
it matters here:* "which exception dominates" is usually the first question;
near-zero cost and consistent with the allocation UX. *Status:* landed - the thrown
type is a synthetic leaf, so `exceptions` / `rank metric=exceptions` self-time now
ranks exception types (the fixture's `System.InvalidOperationException` about 2:1 over
`ArgumentException`) rather than the runtime dispatch frame; throw sites are one
`callers <type>` away, and the exceptions eval task was re-pointed to the type.

**TE-11. Agentic discoverability (cross-cutting).** *A developer asks:* "Why is this
slow?" - with no idea which view to use. *Applicability to .NET:* universal; it is
the entry point to every other item. Make `trace_info` report which families the
capture can answer, extend the `hints` channel to route a symptom to a family (low
on-CPU fraction -> contention / wait / threadtime; native GC/JIT-dominated ->
classify / gcstats; high throw count -> exceptions), and optionally add a
`trace_triage` tool that returns a ranked list of likely causes each paired with its
confirming tool. *Why it matters here:* this is what lets a non-expert's vague
prompt reach any of TE-1..TE-10 without naming it - the core of the agentic flow
above. Foundational, so it is P0 despite the high ID. *Status:* landed - `trace_info`
now returns `availableAnalyses` (the analyses the trace's format can answer) and a
format-honest symptom-routing hint (CPU-bound -> cpu; slow / low-CPU -> contention /
wait / threadtime; memory or GC -> alloc / gcstats; exceptions -> exceptions), each
route filtered to what the format supports. The `trace_triage` tool was intentionally
deferred: the hints deliver the routing with no new tool and no MCP token-budget cost.

### P1 - high value, moderate cost or ETW-scoped (strengthens the `.etl` story)

**TE-5. ThreadPool starvation report.** *A developer asks:* "Under load everything
crawls but the CPU is barely busy and requests pile up - why?" *Applicability to
.NET:* a classic ASP.NET / server hang - sync-over-async blocking pool threads so
the pool injects new ones only slowly while the queue grows. Aggregate
`ThreadPoolWorkerThreadAdjustmentAdjustment` (Reason = Starvation), thread-injection
rate, and queue growth into a structured report like `gcstats`. Otherwise invisible
to a non-expert. *Status:* landed - the `ThreadPoolProvider` reads the runtime's
`ThreadPoolWorkerThreadAdjustment/Adjustment` and `ThreadPoolMinMaxThreads` events
into a `ThreadPoolResult` (adjustment tally, starvation count, worker-thread range
against the configured min/max, and a per-reason breakdown), wired as the
`threadpool` verb and the `trace_threadpool` tool, listed in `availableAnalyses` and
routed from the "slow but low CPU / does not scale" symptom hint. The Threading
keyword is in the default EventPipe set, so a plain CpuSampling capture records the
events - the `ThreadPoolStarveLoop` fixture forces the pool to start at one worker
thread (via `DOTNET_ThreadPool_ForceMinWorkerThreads`) so a backlog of blocking work
reliably starves it.

**TE-6. Thread-time blocked-leaf split.** *A developer asks:* "I know it's blocked -
but on the disk, the network, a lock, or paging?" *Applicability to .NET:* general -
any I/O- or lock-heavy workload. The `ThreadTimeStackComputer` already emits DISK /
NETWORK / HARD_FAULT / READIED leaves, and filtrace passes them through the ranking
unchanged - only `CPU_TIME` is folded, and `ExcludeReadyThread = true` drops the
ready-thread leaf - so the finer leaves already surface as distinct rows. *Status:*
landed - the blocked-reason leaves (`DISK_TIME`, `HARD_FAULT`, `NETWORK_TIME`) rank on
their own, verified by a thread-time test over the new disk-I/O fixture that asserts
`DISK_TIME` appears. (The assessment's earlier "filtrace collapses them to CPU_TIME /
BLOCKED_TIME" claim was inaccurate: it never collapsed them; they were simply not
exercised by a fixture until TE-7 produced one.)

**TE-7. Disk-I/O and File-I/O families.** *A developer asks:* "Is my code really
waiting on the disk or a file, and which path issues those reads?" *Applicability to
.NET:* common in data-heavy apps, logging, serialization, config loading.
`KernelTraceEventParser` `DiskIORead` / `DiskIOWrite` / `FileIORead` / `FileIOWrite`
carry stacks; weight by bytes or I/O time into the same provider shape. ETW-only,
fits the "reach for `.etl` when..." framing. *Status:* landed for disk I/O - a
`DiskIoProvider` reads the kernel `DiskIO/Read` and `DiskIO/Write` events into a
`DiskIoResult` aggregated by file (read / write bytes, operation counts, disk service
time), wired as the `diskio` verb and the `trace_diskio` tool, listed in
`availableAnalyses` and routed from a "waiting on disk / heavy file I/O" hint. The
committed fixture was the hard part: a machine-wide ETW disk capture is dominated by
the `DiskFileIO` file-name rundown (650K+ events, hundreds of megabytes) regardless of
the capture window, so the `trim` fixture tool was extended to keep just the target
process tree's disk I/O - correlating `DiskIO` completions (which the kernel
misattributes to the idle / System process) back to their issuing thread by IRP, and
keeping the file-name rundown entries for the referenced file keys - shrinking a
1.16 GB capture to a 364 KB fixture whose disk events still resolve to file names.
*File I/O* (logical, largely cache-served) was deliberately deferred: its keyword is an
order of magnitude higher volume, and physical disk time is the pressure that actually
matters.

### P2 - strategic or dependency- / capture-gated

**TE-8. Request / activity scoping.** *A developer asks:* "The app is fine except
this one endpoint or job - can I see just its time?" *Applicability to .NET:*
central to server apps (ASP.NET requests, background jobs, message handlers).
*Status:* landed (`.nettrace`) - two halves over `StartStopActivityComputer`, which
pairs EventSource Start/Stop events into named, timed, nested activities. The
**activity metric** (`rank --metric activity`) weights each activity by its wall-clock
duration and nests it under its parent - the `ActivityProvider` builds each stack from
the activity's `Creator` chain, framed by the clean `TaskName` so instances fold
together - so the existing aggregator ranks which request / job type costs the most
time. The **`--activity <name>` scope** filters the cpu view to the samples taken
inside a matching activity (or one nested under it), async-correct via
`GetCurrentStartStopActivity` rather than a per-thread time window, and is rejected
with a non-cpu metric. A `Filtrace-ActivityBench` EventSource fixture (`ActivityLoop`,
Order { Query, Render }) proves both. Cross-metric activity scoping (alloc,
exceptions, ...) and the `.etl` path are the noted follow-ups.

**TE-9. PMC / CPU-counter ranking.** *A developer asks:* "The CPU is busy but I
can't see why this loop is expensive - is it cache misses or branch mispredicts?"
*Applicability to .NET:* advanced / niche - tight numeric or data-structure loops.
Fully supported analysis-side (`TraceEventProfileSources`, `ProfileSourceInfo`,
`PMCCounterProfTraceData`); the cost is ETW capture-side. Stays backlog, as the plan
already has it.

**TE-10. Retention / leak - re-scope, do not assume it is free.** *A developer
asks:* "My memory never comes back down - what is holding all these objects alive?"
*Applicability to .NET:* common - event-handler leaks, static caches, captured
closures, undisposed scopes. Reflection shows the `.gcdump` analysis types are not in
TraceEvent 3.2.3; run a spike to locate the assembly that carries `MemoryGraph` /
`GCHeapDump` (PerfView-side `Graphs` / `HeapDump`) and treat retention as
dependency-gated. This corrects Addendum A's "analysis ships without the lift" claim.

**TE-12. Raw event query over `.etl`.** *A developer asks:* "I have an ETW capture -
can I inspect its raw events by name the way I can for a `.nettrace`?" *Applicability
to .NET:* niche but low-cost - the escape hatch the structured reports do not cover,
for `.etl` as well as EventPipe. *Status:* landed - the `EventQueryProvider` now opens
an `.etl` via `TraceLog.OpenOrConvert` (the event loop over `TraceLog.Events` is
identical for both formats), the `events` verb and `trace_query_events` accept a
`.nettrace` or an `.etl` through a shared dual-format guardrail (a speedscope export,
which carries no event stream, is still rejected), and `events` is back in the ETW
`availableAnalyses`. Reading an `.etl` stays Windows-only. Surfaced by the TE-7 review.

**TE-13. Capture size cap (circular buffer).** *A developer asks:* "My capture ran
for a while and produced a giant `.etl` - can I keep it bounded?" *Applicability to
.NET:* any open-ended or long capture - a service under load, or a hang you have to
wait for. Without a cap `filtrace collect` writes an unbounded sequential `.etl`,
bounded only by `--duration` (time, not size). *Status:* landed - `collect
--max-size-mb` sets `TraceEventSession.CircularBufferMB`, so the session records into a
fixed-size ring that keeps the last N megabytes (validated as positive, or omitted for
an unbounded file). The one caveat, documented on the option, is that once the ring
fills the oldest events are overwritten - which can drop the early JIT method-name
events and lower symbol resolution - so size the cap to hold the run when managed
frames matter.

**TE-14. Ship the process-tree `trim` as a verb.** *A developer asks:* "This `.etl`
is huge - can I shrink it to just my app before I move it?" *Applicability to .NET:*
common whenever a trace has to leave the capture machine - committing it as a fixture,
attaching it to an issue, handing it to a teammate. The process-tree relog extended
for TE-7 lives only in the `HotLoopBench` fixture tool; shipping it as a filtrace verb
would put the shrink in users' hands. Because analysis-time `--process` scoping is
already lossless, physical trim is a *transport* optimization, not an analysis one,
and it carries a known limitation: the raw relogger does not rebuild the
managed-method address map, so a trimmed `.etl` resolves native modules but drops
JITted managed frames (see [filtrace-etl-trimming.md](filtrace-etl-trimming.md)). P3
until that rebuild is solved - resolving it would make the shrink lossless and raise
the priority. *Status:* proposed.

**TE-15. Time-window scope.** *A developer asks:* "Only the few seconds around
the spike matter - can I keep just that slice?" *Applicability to .NET:* long captures
whose interesting behavior is a brief window - a latency spike, one slow request, a GC
pause. *Status:* landed as an analysis-time `--time <start>,<end>` scope (milliseconds
relative to the trace start, either bound optional), the lossless read-time counterpart
to the physical relog TE-14 would add. Because every sampled event carries a timestamp,
this is the one scope axis that applies to *every* metric - the CPU sampler, allocation
ticks, exception throws, contention and wait pairs, activities, and thread-time
intervals - so unlike the cpu-only `--activity` it is not gated on the metric. It rides
the existing `ScopeRequest` (a new `Window`) onto `rank` and `trace_rank`, keeping the
samples whose anchor time falls in the window; a `.speedscope.json` timeline is in the
profile's own unit, not milliseconds, so `--time` is a no-op there (with a warning).
Extending it to the metric-shortcut and drill-down verbs, and the physical `[t0, t1]`
relog (TE-14's sibling), remain follow-ups.

## Recommended next step

**Progress:** the full "why is this slow?" increment has landed - TE-1 to TE-4 on the
`.nettrace` path (the `contention` and `wait` metrics sharing a `LatencyStackReader`,
GC-report depth, exception-by-type ranking) plus TE-11 (content-aware `trace_info` +
symptom-routing hints), TE-5 (the `threadpool` starvation report), and the ETW
follow-on TE-6 (thread-time blocked-reason leaves) and TE-7 (the `diskio` report,
unblocked by teaching the `trim` fixture tool to keep a process tree's disk I/O). TE-8
to TE-10 remain; TE-13 (the capture-size cap) landed, and TE-12, TE-14, and TE-15 are
proposed lower-priority follow-ups (a raw `.etl` event query, plus the trimming items
surfaced by the TE-7 fixture work).

Land **TE-11 alongside TE-1 through TE-4** as one increment aimed squarely at the
"why is this slow?" flow: the two new stack providers (`ContentionProvider`,
`WaitProvider`) exposed as `metric=contention|wait` on the existing `rank` /
`trace_rank`, the two enrichments to `gcstats` and `exceptions`, and the
discoverability layer (content-aware `trace_info` plus symptom-routing hints) that
lets an agent reach them from a vague prompt. This adds two genuinely new diagnostic
families to the *default cross-platform EventPipe loop*, spends almost nothing on
agent-facing surface (enum values, not tools), and each family reuses the proven
`ThreadTimeProvider` integration pattern. TE-11's hints are what make the new metrics
discoverable, so shipping them together is what turns "why is this slow?" into a
guided path rather than a guess.

## How to re-verify

Reflect over the referenced assembly (no project needed):

```pwsh
$dll = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.diagnostics.tracing.traceevent\3.2.3" `
    -Recurse -Filter 'Microsoft.Diagnostics.Tracing.TraceEvent.dll' |
    Where-Object FullName -match 'netstandard2.0' | Select-Object -First 1
$asm = [System.Reflection.Assembly]::LoadFrom($dll.FullName)
try { $types = $asm.GetTypes() } catch { $types = $_.Exception.Types | Where-Object { $_ } }
$types | Where-Object { $_.IsPublic -and $_.Namespace -eq 'Microsoft.Diagnostics.Tracing.Computers' } |
    Sort-Object Name | ForEach-Object Name
```

Bump the version string when the `Microsoft.Diagnostics.Tracing.TraceEvent` pin in
[../Directory.Packages.props](../Directory.Packages.props) moves, and re-audit the
`Computers` namespace and the event surface for anything new.
