# Filtrace parallelism and performance opportunities

**Status:** Proposed — unshipped. Candidates are ranked by value/effort below.

**Date:** 2026-07-28

This document records the CPU-bound work in filtrace's loading and analysis pipeline,
identifies where parallelism can reduce wall-clock time today (local repo changes),
and lists the TraceEvent library additions that would unlock the remaining
opportunities. It is a living tracking document; update **Status** on each item as
work progresses.

---

## Where the CPU goes today

Every `.nettrace` or `.etl` analysis runs through three sequential phases.

### Phase 1: ETLX conversion

`TraceConverter.ConvertWithState` calls `TraceLog.CreateFromEventPipeDataFile` or
`CreateFromEventTraceLogFile`. This is a single-threaded CPU + I/O operation inside
TraceEvent that rewrites the source to the indexed ETLX format. A named mutex per
canonical source path serializes concurrent callers so they do not race; the first
caller converts and subsequent callers wait, then hit the cache.

### Phase 2: Event enumeration and frame walking

`TraceLogReader.ReadCore` iterates `traceLog.Events`, identifies CPU sample events
(`SampledProfileTraceData` or `ClrThreadSampleTraceData`), and walks every
`TraceCallStack` frame-by-frame to build `SampleStack` objects. Per-frame work is:
two string property reads (`FullMethodName`, `ModuleName`), a string concatenation,
and (with local symbols) a `ResolveLocation` call that is cached by code-address
index. This is the dominant CPU + memory-allocation phase for a trace with tens of
thousands of samples.

### Phase 3: Aggregation

`FoldingAggregator` methods iterate the materialized `_samples` list and build
`Dictionary<string, double>` accumulators. Per-sample work per pass involves:
a `TryFindRootStart` linear frame scan, one or more `ShortOf` calls (regex match
plus two string replaces, memoized into `_shortCache`), and one `IsFolded` check per
non-folded frame (tests each compiled fold regex in sequence). With the default 7
fold patterns and a 20-frame average stack depth, a single aggregation query over
10,000 samples calls `Regex.IsMatch` on the order of 200,000-400,000 times.

### Cost summary

| Phase | CPU character | Parallelizable today |
|---|---|---|
| ETLX conversion | CPU + sequential I/O | Not within one file (TraceEvent limitation) |
| Activity pre-pass | CPU, full event scan | Merge with main read (see LP-3) |
| Event enumeration + frame walk | CPU + I/O | Not within one file (TraceEvent limitation) |
| ResolveLocation per frame | CPU + I/O (PDB lookup) | Not with current SymbolReader (see TE-P3) |
| EmbeddedPdbExtractor.Extract | CPU (deflate) + I/O | Yes, trivially (see LP-4) |
| FoldingAggregator methods | Pure CPU | Yes, partition-then-reduce (see LP-2) |
| Batch/diff case loading | CPU + I/O per independent trace | Yes, cases are fully independent (see LP-1) |
| Native symbol resolution | I/O (symbol server) | Yes, modules are independent (see LP-5 / TE-P3) |

---

## Local-repo opportunities

### LP-1 — Batch and diff parallel case loading

**Status:** Proposed

**Where:** `CaptureManifestBatchAnalyzer.Analyze` and
`CaptureManifestDiffAnalyzer.Analyze`

**What happens today:** Both iterate their case lists with a sequential `foreach`.
Each case loads one (batch) or two (diff) traces independently, runs a ranking
query, and appends a result. With 24 cases the iterations are fully independent:
no shared mutable state between iterations. The `load` delegate goes through
`TraceStore.Get`, whose `LruCache` already runs the factory outside its lock and is
tested for concurrent access.

**The change:** Replace the inner `foreach` with `Parallel.ForEach` (or
`Parallel.For` with index-based writes into a preallocated result array). Each case
loads and ranks on a thread-pool thread. On a 4-core machine with 24 cases each
taking ~200 ms, the current ~4,800 ms batch wall time becomes ~1,200 ms.

**Thread-safety notes:**
- The two result lists must be written by index (preallocate to `Count` and assign
  `results[i]`) or collected into a `ConcurrentBag` and sorted at the end.
- The per-iteration `List<string> warnings` is freshly allocated per case — safe.
- `TraceStore.Get` may run the factory twice when two cases share the same trace path
  (the LruCache documented race: "first to re-acquire the lock wins"). For batch
  analysis this is a tolerable transient double-load, not a correctness bug.

**Estimated gain:** High value, low effort.

---

### LP-2 — FoldingAggregator PLINQ partition-and-reduce

**Status:** Proposed

**Where:** `FoldingAggregator.SelfTime`, `InclusiveTime`, `CallersOf`, `HotLines`,
`SourceHeatmap`, `CallTree`, `Classify`

**What happens today:** Every method is a sequential `foreach` over the read-only
`_samples` list, building local `Dictionary<string, double>` accumulators and a
running `double total`.

**The change:** Partition `_samples` across threads using PLINQ's `Aggregate`
overload. Each partition gets its own local `Dictionary<string, double>` and `double
total`. A final sequential merge sums matching keys across partitions. The merge
step is O(unique frame names), which is much smaller than O(N_samples), so it does
not become the new bottleneck.

**Thread-safety notes:**
- `_shortCache` is already a `ConcurrentDictionary` (the comment on line 53 of
  `FoldingAggregator.cs` explicitly acknowledges concurrent query access), so
  `ShortOf` is safe to call from multiple threads.
- `IsFolded` reads only the compiled `Regex[]` and calls `Regex.IsMatch` — both
  read-only operations and safe for parallel calls.
- The per-iteration local `Dictionary<string, double>` is thread-local by design
  in the partition-and-reduce pattern; no shared mutable state.

**When this pays off:** For traces with more than roughly 5,000 samples and more
than 10 frames average depth (where per-aggregation cost exceeds ~10 ms on a single
core). For small traces, thread-pool overhead dominates. A sample count threshold
check before enabling PLINQ avoids regressing the fast path.

**Estimated gain:** Medium-high value, medium effort.

---

### LP-3 — Merge activity pre-pass into the main read

**Status:** Proposed

**Where:** `TraceLogReader.Read` and `ReadCore`

**What happens today:** When an activity scope is requested,
`ComputeActivitySampleFilter` runs a complete `source.Process()` pass over the trace
to build a `HashSet<EventIndex>` of matching CPU sample events. `ReadCore` then runs
a second full iteration over `traceLog.Events`. Large ETL captures pay for two full
event-stream scans.

**The change:** Refactor `ReadCore` to drive a `TraceLogEventSource` (as
`ComputeActivitySampleFilter` already does) rather than iterating `traceLog.Events`
directly. Register both the activity-computer callbacks and the CPU-sample handler on
one source, then call `source.Process()` once. Emit CPU samples into the result list
in the same pass that the activity computer is marking matching event indices.

**Estimated gain:** Medium value, medium effort. Largest benefit on machine-wide
multi-process ETL captures where the event count is high.

---

### LP-4 — Parallel DLL scanning in EmbeddedPdbExtractor

**Status:** Proposed

**Where:** `EmbeddedPdbExtractor.Extract`

**What happens today:** Sequential `foreach` over DLLs in a build output directory.
Each DLL is read entirely into memory (`File.ReadAllBytes`), PE-parsed, and if an
embedded PDB blob is found, decompressed with `DeflateStream`. The decompression is
the CPU-bound part.

**The change:** Replace with `Parallel.ForEach`. The only shared mutable state is
`tempDirectory`; protect its lazy initialization with a `lock` or
`LazyInitializer.EnsureInitialized`. Each DLL's extraction is otherwise fully
independent.

**Scale:** A typical `Release/net10.0` directory contains 10-30 DLLs. Absolute gain
is modest (tens of ms) but noticeable in agent workflows that call `trace_rank
--symbols` repeatedly.

**Estimated gain:** Low-medium value, low effort.

---

### LP-5 — Parallel native runtime symbol module lookups

**Status:** Blocked on TE-P3 (thread-safety contract for `LookupSymbolsForModule`)

**Where:** `TraceLogReader.ResolveNativeRuntimeSymbols`

**What happens today:** Sequential `foreach` over `traceLog.ModuleFiles` filtered to
~7 runtime/OS modules, calling
`traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, moduleFile)` for each.
Each call may involve a network round-trip to `msdl.microsoft.com` or a local cache
lookup.

**The change (pending TE-P3):** Parallel module lookups with `Task.WhenAll`. Each
module gets its own `SymbolReader` instance (sharing the same symbol path) so
network connections are independent. Requires confirmation that concurrent calls to
`LookupSymbolsForModule` against the same `TraceLog.CodeAddresses` are safe.

**Estimated gain:** Medium value for the native-symbols path (`--native-symbols`
flag), low effort once the thread-safety question is resolved.

---

## TraceEvent library requests

These require upstream changes to `Microsoft.Diagnostics.Tracing.TraceEvent`
(currently pinned at 3.2.3 in `Directory.Packages.props`).

### TE-P1 — Embedded portable PDB reading in SymbolReader

**Status:** Proposed — upstream follow-up exists in docs/traceevent-embedded-pdb.md

**What happens today:** `EmbeddedPdbExtractor.Extract` reads every DLL in a build
output directory, decompresses the embedded portable PDB blob (the `.MPDB` section),
and writes standalone `.pdb` files to a temp directory. The temp directory is added
to `SymbolReader.SymbolPath`. The files are deleted when the read completes.

**The ask:** `SymbolReader.GetSourceLine` should detect and read the `.MPDB` section
from a PE image mapped via its PDB GUID, the same way Visual Studio and Roslyn
resolve embedded symbols.

**Why it matters:** Eliminating the extract step removes 5-30 ms of I/O +
decompression time on every `--symbols` run, eliminates the temp-directory
lifecycle, and removes a class of resource-leak risk on abnormal exits. It also
unblocks LP-4 from being necessary at all.

---

### TE-P2 — Async / cancellable ETLX conversion

**Status:** Proposed

**What happens today:** `TraceLog.CreateFromEventPipeDataFile` and
`CreateFromEventTraceLogFile` are synchronous and not cancellable.
`TraceConverter.ConvertWithState` wraps them with a named `Mutex` and polls a
`CancellationToken` around the mutex-acquire loop, but once the conversion call
starts no cancellation is possible until it returns.

**The ask:** An async overload accepting a `CancellationToken` that checks it at
natural checkpoints during the ETLX write (e.g., between event blocks or between the
main pass and the sort/index phase). This would let the MCP server cancel a slow
conversion when the client disconnects without leaving a zombie process holding the
mutex.

---

### TE-P3 — Thread-safety contract for LookupSymbolsForModule

**Status:** Proposed

**What happens today:** `traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader,
moduleFile)` mutates shared internal state in `TraceLog.CodeAddresses`. Whether
concurrent calls for different modules are safe is undocumented.

**The ask:** Either document the thread-safety contract explicitly (allowing filtrace
to parallelize module lookups using one `SymbolReader` per thread against a shared
`TraceLog`), or expose a per-module async variant that internalizes the
synchronization (`LookupSymbolsForModuleAsync(moduleFile, cancellationToken)`).

---

### TE-P4 — Event stream partitioning for parallel ReadCore

**Status:** Proposed — highest value, highest complexity

**What happens today:** `traceLog.Events` is a sequential `IEnumerable<TraceEvent>`.
There is no way to partition the event stream so two readers can process different
time slices of the same ETLX file concurrently.

**The ask:** A `traceLog.Events.Partition(n)` API that slices the ETLX event array
into `n` disjoint, time-ordered ranges, each independently enumerable. This would
allow filtrace to run `ReadCore` on `n` threads, each processing a time slice and
emitting its own `List<SampleStack>`, then concatenate results.

**The constraint:** Symbol resolution state in TraceEvent is typically cumulative:
rundown events that register method names must precede the CPU sample events that
reference those addresses. Partitioning is only safe if TraceEvent can seek to an
arbitrary ETLX event index while reconstructing the symbol state for that point in
the stream, or if the pre-resolved ETLX format guarantees that every code address
reference in a sample event can be resolved without prior-event context. This
question must be answered before implementation begins.

---

### TE-P5 — Pre-computed CPU sample count per process in TraceLog

**Status:** Proposed

**What happens today:** `ProcessTree.FindBusiestProcessName` does a full sequential
event scan to count CPU samples per process ID before the main `ReadCore` loop,
effectively adding a second complete event enumeration.

**The ask:** Expose `TraceProcess.CpuSampleCount` as a precomputed property on the
`TraceLog.Processes` table, populated during the ETLX build phase (which already
walks all events once). This would replace the pre-pass with a single
`traceLog.Processes.Max(p => p.CpuSampleCount)` query.

**Note:** TraceEvent already exposes `TraceProcess.CPUMSec`, but filtrace
deliberately does not use it for auto-scoping (see the comment in
`ProcessTree.FindBusiestProcessName`: a long-lived background service accumulates
more CPUMSec than a short benchmark but carries a tiny fraction of the profile's
samples, making CPUMSec the wrong discriminant). A sample count is the correct
metric.

---

## Priority matrix

| ID | Opportunity | Value | Effort | Blocker |
|---|---|---|---|---|
| LP-1 | Batch/diff parallel case loading | High | Low | None |
| LP-3 | Activity pre-pass merge | Medium | Medium | None |
| LP-2 | FoldingAggregator PLINQ reduce | Medium-High | Medium | None |
| LP-4 | EmbeddedPdbExtractor parallel DLLs | Low-Medium | Low | None |
| LP-5 | Parallel native symbol module lookups | Medium | Low | TE-P3 |
| TE-P1 | Embedded PDB reading in SymbolReader | High | TraceEvent change | Upstream |
| TE-P5 | Pre-computed CpuSampleCount | Medium | TraceEvent change | Upstream |
| TE-P2 | Async / cancellable ETLX conversion | Medium | TraceEvent change | Upstream |
| TE-P3 | LookupSymbolsForModule thread safety | Medium | TraceEvent change | Upstream |
| TE-P4 | Event stream partitioning | High (if feasible) | Large TraceEvent change | Symbol-state ordering |

The safe first moves with no external blockers are **LP-1** (batch/diff
parallelism) and **LP-3** (activity pre-pass merge). LP-1 is a small
`Parallel.ForEach` with indexed result writes; LP-3 eliminates a full second ETLX
scan without thread-safety risk. **LP-2** (FoldingAggregator PLINQ) yields the
largest gains on large traces and the code already has `ConcurrentDictionary`
infrastructure validating the intent.

The TraceEvent requests with the greatest local leverage are **TE-P1** (embedded
PDB) and **TE-P5** (pre-computed sample counts): both eliminate entire extra passes
that have no local-only remedy.
