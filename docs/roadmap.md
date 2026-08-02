# filtrace roadmap

**Status:** Living plan. This is the only page that holds unshipped work.

**Last verified:** 2026-08-01 against `main`.

Shipped work is not tracked here. Git history and the release tags record what
landed; the durable lessons from finished initiatives live in
[design.md](design.md), and the cross-tool observations that motivate several items
live in [competitive-analysis.md](competitive-analysis.md).

Every item is judged against the goals, principles, and measures in
[design.md](design.md). An item is not "ready" because it is implementable; it is
ready when its gate is answered.

## What the plan rests on

Facts that set today's priorities. Re-measure before treating any of them as still
true.

- **The surface is 25 CLI verbs and 18 `trace_*` MCP tools.** The tool list measures
  ~6,388 estimated tokens against a 7,000-token CI gate.
- **The permanent schema is no longer dominated by output schemas.** Advertising the
  envelope alone instead of every expanded result type reclaimed roughly 3,000
  tokens. At the 17-tool measurement the remaining split was input schemas ~3,700,
  output schemas ~1,020, descriptions ~730. Input schemas are now the largest lever,
  and prose is the smallest.
- **Ordinary responses are already small.** Ranking, caller, process, and tree
  answers land around 105-199 tokens; GC, timeline, thread-time, source-quality,
  batch, and diff around 292-886. The JIT report (~2,251) and a raw allocation-event
  page (~5,538) are the outliers. The 25,000-token ceiling is not the problem;
  returning detail nobody asked for is.
- **The remaining measured duplication is per call, not per list.** A live
  `trace_info` round trip carried the same JSON payload in both `content[0].text`
  and `structuredContent`. How much of each copy reaches model context is
  client-dependent and has to be measured per host.
- **The deterministic eval suite answers all 17 fixture-backed tasks**, most in one
  call. The surface works; the open question is efficiency, not correctness.

## Priorities

| When | Items | Why now |
|---|---|---|
| Now | VN0 | Nothing else can be decided without a repeatable multi-model baseline. |
| Next | VN1, VN2, SC8 | Transport and output-contract decisions determine how every later capability is exposed; SC8 closes a correctness gap in data already captured. |
| Then | VN3, VN4, VC1 | Surface selection, then the first capability that fits it. |
| Later | VC2-VC8, LP-1..LP-5, VN5 | Demand-, dependency-, or decision-gated. |
| Upstream | TE-P1..TE-P5 | Not actionable in this repository alone. |

Ordering rule: no capability adds a standalone MCP tool before VN3 selects the
final surface, because the tool list is permanent context.

---

## Track A - surface, transport, and output contract

The v.next line. It is also the explicit breaking-change decision
[AGENTS.md](../AGENTS.md) requires before any existing `trace_*` name may be
renamed or removed. Until a surface is selected and versioned, the current names
stay frozen.

### VN0 - baseline and instrumentation

**Priority:** Now. Everything after this is unmeasurable without it.

- Freeze the current 25-verb / 18-tool results across more than one model family.
- Extend [Invoke-AgentEval.ps1](../eval/Invoke-AgentEval.ps1) to record, separately:
  text-content tokens, structured-content tokens, complete MCP result tokens, and
  the client-visible value supplied to the model where the host exposes it.
- Record the per-tool schema-token breakdown (input, output, description, total) in
  the test artifact.
- Add an experimental server path or config so a baseline and a candidate surface
  can run without editing committed task expectations between runs.
- Grade expected *operation intent* as well as exact tool name, so old and
  consolidated surfaces can be compared at all. `expectTools` stays accepted while
  the current surface is the baseline.
- Run at least three iterations per task and compare medians.
- Add the comprehension tasks below.

**Exit:** repeatable baseline artifacts that separate permanent schema cost, wire
response cost, and model-visible cost.

#### Comprehension tasks to add

Each as a normal `eval/tasks/*.json` task with a canonical deterministic step, plus
the matching `eval/mcp-qa.jsonl` row. Extend the live-agent task schema with
`expectOperations`, `forbidOperations`, and an optional maximum-call override.

- choose `rank metric=alloc` without inventing a nonexistent `trace_alloc`;
- skip source-line tools for speedscope input;
- distinguish enabled-with-zero-events, disabled, and unknown capture status;
- preserve process and root scope from a ranking into a caller drill;
- reject or repair `root` plus `benchmark`;
- disambiguate multiple matching frames;
- request a report summary first and escalate to detail only when needed;
- count raw events without returning an event page;
- escalate from a batch case reference to one detailed ranking;
- choose `classify` rather than a generic report for native runtime CPU work.

### VN1 - transport selection

**Priority:** Next. **Gate:** multi-model comparison, not preference.

Run three implementations behind a build property `FiltraceMcpTransport`
(`Structured`, `StructuredMinimal`, `JsonText`), each published to
`artifacts/vnext/<variant>/`, with an `-McpDll` override in the live agent harness
so every run points at the exact variant and stamps it into the result label. Do
not overwrite `eval/baselines.json`; transport comparisons live in the ignored
`eval/results/` artifacts.

| Variant | Shape | What it tests |
|---|---|---|
| A | typed `structuredContent` plus SDK-generated text mirror, current schemas | correctness baseline |
| B | typed `structuredContent`, minimal text block | removes per-call duplication; keeps advertised typing |
| C | compact JSON text only, no advertised output schema | removes duplication and ~1,000 list tokens; loses advertised typing |

**Note the changed arithmetic.** When this experiment was proposed, output schemas
were ~3,900 tokens and variant C looked like a large permanent win. After the
envelope-only reduction they are ~1,020. C's case now rests almost entirely on
per-call duplication, so B is the cheaper hypothesis to test first.

Record per variant: tool-list characters and tokens; per-tool input/output/
description/total; text, structured, complete-wire, and client-visible result
tokens; task success, expected-tool success, calls, wall time, answer accuracy; and
behavior in Copilot CLI plus at least one other MCP client or model family.

Selection rule, in order: reject any variant with a success regression on any model;
reject any variant that increases median calls; among the rest choose the lowest
total investigation tokens; retain typed structured output when the difference is
inconclusive.

**Exit:** one documented transport decision. No result-shape changes in the same
run.

### VN2 - output contract v9

**Priority:** Next, after VN1. **Gate:** do not mix transport and result-shape
changes in one A/B run.

The envelope is at `schemaVersion` 8. v9 is one semantic revision covering all of
the following.

**Effective query context.** Every result identifies what actually ran, not only
what was requested - resolved operation, metric, measure, unit, and scope. This is
also where a resolved process scope (auto-scope, `--process`, or an exact `--pid`
set with `--children`) becomes machine-readable instead of prose: the `scope` object
carries the applied selector and the resolved root and descendant ids.

```json
{
  "operation": "rank",
  "metric": "cpu",
  "measure": "self",
  "unit": "ms",
  "scope": { "process": "MyApp", "root": "WorkloadAction" }
}
```

Include only fields meaningful to the operation; omit nulls.

**Structured diagnostics.** Replace `warnings: string[]` with stable records that
keep a human message:

```json
{
  "code": "thin_scope",
  "severity": "warning",
  "message": "Only 32 periodic CPU records contribute to this method scope.",
  "data": { "contributingRecords": 32, "recommendedMinimum": 200 }
}
```

Initial stable codes: low frame-name resolution; low source mapping; PDB identity
mismatch; unknown or disabled capture status; thin method or line scope; ambiguous
frame or root match; truncated rows or payload; ignored format-specific scope;
case-local manifest failure.

**Structured next steps.** Replace CLI-shaped hint strings with operation-neutral
records that the CLI renders as a shell command and MCP maps to a tool call.
Scope-preserving arguments belong in the record so a follow-up cannot silently lose
process or root context.

```json
{
  "operation": "callers",
  "reason": "drill into the hottest CPU frame",
  "arguments": { "frame": "MyApp.Inner" }
}
```

**Discriminated results.** Add `kind` where one result type represents unrelated
shapes - most importantly diff (`trace` versus `manifest`) - and stop serializing
empty `cases` on a direct diff or empty direct totals on a manifest diff. The same
rule applies to any consolidated source or report result.

**Null and default omission.** Omit null optional properties. Keep empty arrays that
mean "the query ran and found none"; omit an array only when the concept does not
apply to the selected result kind.

**Detail profiles.** A closed vocabulary - `summary`, `rows`, `full` - only where it
changes response cardinality. Do not add `detail` to operations whose result is
already small.

| Operation | Proposed MCP default | Behavior |
|---|---|---|
| info | `summary` | source/PDB method and module lists need `rows` |
| rank / callers / tree / source | current bounded rows | `top` and depth remain the natural control |
| GC / JIT / disk reports | `summary` | per-GC, per-method, per-file records need `rows` |
| thread pool | `summary` | already small |
| events | count or summary | event records need `rows`; paging stays `skip`/`take` |
| timeline | current bounded buckets | lanes and bucket count remain the control |
| diff / batch | current structural caps | already compact agent summaries |

The CLI-detailed / MCP-summary asymmetry is a candidate, not a decision. VN0 must
compare both defaults on questions that need only aggregates and on questions that
need evidence rows; reject a summary default whose saved first-response tokens are
offset by escalation calls. Deterministic tasks pass an explicit detail level so
goldens do not depend on host defaults.

**Manifest case references.** Expose the `id` that manifest schema v1 already
requires on each case in `BatchRankingCaseResult`, and let follow-up operations
accept `manifestPath` plus `caseId`. Keep the resolved path at `full` detail for
audit and CLI display. Existing manifests need no migration; the envelope changes
because batch output gains `caseId`.

**Exit:** results are self-describing, compact by default, and can route a follow-up
without parsing prose.

### VN3 - MCP surface experiment

**Priority:** Then. **Gate:** operation-intent grading shows no selection or call
regression.

Candidate 13-tool surface: retain `trace_info`, `trace_rank`, `trace_callers`,
`trace_tree`, `trace_processes`, `trace_classify`, `trace_timeline`, `trace_diff`,
`trace_batch`, `trace_query_events`, `trace_export`; combine `trace_lines` +
`trace_heatmap` into `trace_source(view=...)`; combine the provider reports into
`trace_report(kind=...)`. `trace_lifecycle` must be evaluated for the same folding.

`trace_classify` stays separate from `trace_report`: it consumes CPU stacks,
supports root/process/benchmark scope, and can opt into networked native symbols.

Measured cost of the tools involved, post-reduction: `trace_rank` 588 tokens / 14
params, `trace_tree` 470/10, `trace_callers` 446/10, `trace_lines` 419/8,
`trace_jit` 206/2, `trace_diskio` 202/2, `trace_gc` 201/2, `trace_threadpool`
161/1.

| Option | Saves | Argument against |
|---|---|---|
| A - fold the four provider reports into `trace_report(kind)` | ~500 tokens | four names leave the contract; a `kind` parameter hides which providers a trace supports, which separate names advertise for free; per-kind results become a union |
| B - fold `trace_callers`, `trace_lines`, `trace_tree` into a drill family | ~700 tokens | these are the most-used drills; hiding them behind a mode makes the common path less discoverable |
| C - leave the surface alone | 0 | the list still carries 18 definitions and the 5,000-token stretch target stays out of reach |

The budget no longer forces any of these. At 6,388 the gap to the 5,000 stretch
target is ~1,390 tokens, which A alone does not close and B roughly does - an
argument for evaluating B, not for adopting it.

**Consolidation constraint.** `trace_source` must express its two branches
(`view=lines` with `method`, `view=heatmap` with `file`) as a discriminated schema.
If the MCP SDK flattens them into a bag of optional strings, keep the two current
tools: saving one definition is not worth a runtime-only grammar. `trace_report`
exposes only report-common controls at the top level (`path`, `kind`, detail or
cardinality); kind-specific controls go in a typed options branch or are omitted.
Never ship a tool whose unrelated parameters are silently ignored.

**Breaking-name policy.** Ship a consolidated surface only as a deliberate pre-1.0
v.next contract. Never advertise old and new MCP tools together - that raises schema
cost and selection ambiguity at exactly the point the redesign is meant to improve.

**Exit:** a selected MCP surface that meets the success and call gates and the
applicable 7,500 (typed schemas retained) or 5,000 (JSON-text) tool-list target.

### VN4 - CLI surface

**Priority:** Then. **Gate:** top-level help and completion do not get worse.

Candidate 15-command surface: `info`, `rank` (absorbing `cpu`, `alloc`,
`exceptions`, `threadtime`), `callers`, `tree`, `source` (combining `lines` and
`heatmap`), `processes`, `classify`, `report` (combining `gcstats`, `jitstats`,
`threadpool`, `diskio`), `timeline`, `diff`, `batch`, `events`, `export`, `collect`,
`cache` (combining `convert` and `clean`). `lifecycle` is the open placement
question - it is a structured report, but its scope semantics differ from the other
report kinds.

`callers`, `tree`, `timeline`, `diff`, `events`, and `export` stay as named
commands: they communicate a human intent better than modes on `rank`, and keeping
them avoids a large `rank --view` option matrix.

Compatibility, for one preview only: hidden aliases for the metric shortcuts, the
four report verbs, and `convert`/`clean`, each printing the canonical equivalent to
stderr. Aliases must not appear in top-level help, generated docs, or agent
examples, and must be removed before the surface is declared stable. If
ConsoleAppFramework cannot hide an alias without polluting help or completion,
prefer a clean pre-1.0 break over two advertised surfaces.

**Exit:** one canonical path per intent in top-level help; no alias leaks into agent
guidance.

### VN5 - stabilization

Remove preview aliases, run every contract and eval gate in Debug and Release,
publish a migration table from every old verb and tool to the new surface, and
freeze the selected names and schema.

### Agent-comprehension work (folds into VN2-VN4)

- **Canonical vocabulary in agent-facing text.** `rank` with a metric, `report` with
  a kind, `source` with a view. Human shortcut aliases belong in a CLI-only
  compatibility section, so nothing teaches `cpu` and invites an agent to invent
  `trace_cpu`.
- **Conditional orientation.** Replace "always call `trace_info` first" with: call
  it when format, provider availability, process scope, or symbol/source quality is
  unknown; skip it when the prompt and prior results already establish those facts.
- **Explicit compatibility on every operation.** Accepted formats, default process
  behavior, whether it is CPU-only, whether `root` and `benchmark` conflict, whether
  native symbols use the network, and what detail is returned by default. Prefer
  shared generated wording or a test over near-identical prose that drifts.
- **Actionable ambiguity diagnostics.** An ambiguous frame or root diagnostic should
  carry the match count, the selected definition and the selection policy, a bounded
  candidate list, and a structured next step recommending a narrower selector.

---

## Track B - capability backlog

Ordered by expected user value against implementation cost and fit with the
selected surface.

| ID | Capability | Proposed surface | Priority | Main gate |
|---|---|---|:---:|---|
| VC1 | DATAS server-GC tuning | `report --kind datas` / `trace_report(kind=datas)` | High | capture fixture and parser parity |
| VC2 | Point-in-time snapshot | `timeline --mode snapshot` | Medium | prove it beats timeline + rank |
| VC3 | Per-frame temporal buckets | `rank --temporal` or `detail=full` | Medium | response and aggregation cost |
| VC4 | PMC / CPU-counter ranking | new `rank` metric | Medium | ETW capture support and a fixture |
| VC5 | Retention / leak analysis | dedicated retention result | Medium | PerfView graph dependency |
| VC6 | Net surviving heap | new stack metric | Low | `GCHeapSimulator` extraction |
| VC7 | Physical ETL trim | `trim` or `cache --action trim` | Low | preserving JITted managed frames |
| VC8 | Activity and file-I/O follow-ups | extend existing scopes and reports | Low | demand and capture volume |

### VC1 - DATAS server-GC tuning

The highest-value remaining analytical gap, and the one capability a comparable
tool has that filtrace does not. DATAS explains Dynamic Adaptation To Application
Sizes decisions in modern server GC: heap-count transitions, per-collection budget,
throughput-cost and wait samples, and gen-2 backstop tuning.

- Read `TraceGC.DynamicEvents` and parse the packed little-endian payloads into
  immutable result records.
- Return aggregate heap-count min/max/transitions plus bounded tuning and sample
  rows; support a changes-only detail mode for long traces.
- Capture and commit a small DATAS-enabled `.nettrace`; unit-test the binary offsets
  independently of the trace fixture.
- Verify whether an appropriately configured `.etl` exposes the same dynamic events
  before declaring the report EventPipe-only.
- Route heap-count churn to the existing GC report through a structured next step.
- If any implementation is ported from another MIT project, carry its exact
  copyright notice in the source file, add third-party notice text, and retain
  source provenance.

It belongs in the consolidated report family unless VN3 proves a typed report
discriminator is worse for agents.

### VC2 - point-in-time snapshot

"What was happening around this millisecond?" - a bounded window containing GC
activity, top CPU work, exceptions, allocations, JIT activity, and event counts.
Every underlying reader exists, and `timeline` already finds the interesting window.

Implement as a `timeline` mode, not a new tool. Before committing, add an eval task
comparing one snapshot call against the existing timeline-then-rank flow. Accept it
only if it reduces calls or gives materially better cross-lane evidence. The result
must identify the exact window and preserve process scope.

### VC3 - per-frame temporal buckets

A small histogram per ranked CPU frame makes bursty methods visible in one ranking,
instead of finding a busy window and rerunning `rank --time`. It also repeats
time-series data on the hottest output path.

Prototype for CPU periodic samples only, behind an explicit option or `full` detail.
Cap row count and bucket count. Reject it if it breaks the 25,000-token response
bound, materially slows ordinary ranking, or fails to save a follow-up call.

### VC4 - PMC / CPU-counter ranking

`TraceEvent` exposes profile-source metadata and PMC sample events for cache misses,
branch mispredicts, and retired instructions, and the analysis fits the existing
`{stack, weight}` engine as another metric. The cost is all capture-side: a reliable
Windows ETW capture path, machine-support detection, and a committed or
deterministic fixture.

Do not expose the metric until capture metadata names the counter and its unit. An
unsupported machine or trace must produce a capability diagnostic, never an empty
ranking that looks authoritative.

### VC5 - retention / leak analysis

"What is holding these objects alive?" is not an allocation ranking and does not fit
`FoldingAggregator`; it needs a heap-graph object model.

The pinned `TraceEvent` package does not ship `MemoryGraph`, `GCHeapDump`, or the
reference graph. `dotnet-gcdump` vendors roughly 173 KB of MIT PerfView graph source
because factoring it into TraceEvent proved too disruptive, and path-to-root analysis
is not in that vendored set. Before implementing:

1. decide between vendoring the MIT PerfView subset, waiting for a factored package,
   or integrating an external tool;
2. verify unsafe-code, trimming, and AOT implications;
3. define bounded type, root, and path summaries plus a capture handoff through
   `dotnet-gcdump`;
4. keep allocation-rate and retention terminology distinct in every result and hint.

Because this is a distinct data model, VN3 may keep a dedicated tool for it even if
the rest of the report family consolidates.

### VC6 - net surviving heap

Estimating surviving bytes by allocation site across collections needs
`GCHeapSimulator`, which is PerfView-side rather than in the pinned package. Treat it
as a dependency and extraction investigation, not a provider addition. Allocation
rate remains the supported answer until the simulator can be reused with bounded
memory and verified parity.

### VC7 - physical ETL trim

Analysis-time process and time scoping is lossless and remains the default. A
physical relog is valuable only for transport, committed fixtures, and repeated
analysis of very large machine-wide captures. The existing fixture relog preserves
disk events and native modules but does not rebuild the JITted managed-method
address map, so managed stacks become unresolved - see
[filtrace-etl-trimming.md](filtrace-etl-trimming.md).

Do not ship a trim verb until that limitation is fixed or the command is explicitly
scoped to native and event transport scenarios. A shipped version should combine
process-tree and optional time-window selection.

### VC8 - lower-priority enrichments

- Extend activity scope beyond CPU only when allocation and exception correlation
  can preserve async activity identity accurately.
- Consider logical File I/O separately from physical disk I/O only when a concrete
  cache-served-I/O question justifies its much higher event volume.
- Surface `TraceEvent` payload filtrace reads and discards where a question needs it
  - the exception message alongside the type, and the remaining `TraceGC`
  discriminators; see
  [traceevent-surface-assessment.md](traceevent-surface-assessment.md).

---

## Track C - correctness and capture follow-ups

The remaining gaps from the short-command capture initiative
([issue #62](https://github.com/JeremyKuhne/filtrace/issues/62)). Its seven original
items shipped; these are what they exposed.

### SC8 - per-case exact scope in batch and diff

**Priority:** Next. **Cost:** low-medium. **Where:** Core, not the capture script.

`collect --iterations` records each launch's exact root process id in the capture
manifest, but the batch analyzer does not thread a per-case scope through, so the
recorded ids are captured and unused. A command matrix therefore still scopes by
process *name*, which warns when the name matches several unrelated trees and ranks
them together.

Consuming the recorded ids is what makes a command capture exact rather than
name-approximate. It composes with VN2's effective query context, which is where the
resolved scope becomes machine-readable.

### SC9 - cross-machine native symbol fixtures

**Priority:** Later. **Gate:** requires a merge/symbol-injection capture step.

A filtrace capture records no PDB identity of its own, so `TraceEvent` resolves a
native module by reading the binary back from the absolute path in the trace. A
committed capture therefore resolves native symbols only on the machine that took
it, which is why native symbol resolution is verified by capturing during the CI run
instead. Adding the PerfView-style "merge" step to
[EtwCollector](../src/Filtrace.Core/Tracing/EtwCollector.cs) would make a portable,
committable fixture possible.

### Skill packaging headroom

The shipped `SKILL.md` is roughly 270 lines of embedded catalog against 230 lines of
its own guidance, and further trimming has already spent the redundancy that was
available. The next catalog addition needs the validator's own remedy - move a
catalog into a sibling reference file - which is a packaging change, because the MCP
nupkg packs only `SKILL.md`.

---

## Track D - performance and parallelism

**Status:** all proposed, none shipped. **Date of analysis:** 2026-07-28. The
long-form version of this analysis, with the per-method thread-safety notes it was
condensed from, is `docs/parallelism-opportunities.md` on the
`copilot/improve-filtrace-performance` branch.

Where the CPU goes on every `.nettrace` or `.etl` analysis:

| Phase | Character | Parallelizable today |
|---|---|---|
| ETLX conversion (`TraceLog.CreateFrom*`) | CPU plus sequential I/O inside TraceEvent | no, within one file |
| Activity pre-pass (`ComputeActivitySampleFilter`) | CPU, full event scan | merge with the main read (LP-3) |
| Event enumeration and frame walk (`TraceLogReader.ReadCore`) | CPU plus I/O; dominant phase | no, within one file |
| `ResolveLocation` per frame | CPU plus PDB I/O | not with the current `SymbolReader` (TE-P3) |
| `EmbeddedPdbExtractor.Extract` | CPU (deflate) plus I/O | yes (LP-4) |
| `FoldingAggregator` passes | pure CPU | yes, partition then reduce (LP-2) |
| Batch and diff case loading | CPU plus I/O per independent trace | yes, cases are independent (LP-1) |
| Native symbol resolution | symbol-server I/O | yes, modules are independent (LP-5) |

Aggregation cost is worth a number: with the default 7 fold patterns and a 20-frame
average depth, one aggregation over 10,000 samples calls `Regex.IsMatch` on the
order of 200,000-400,000 times.

### LP-1 - parallel case loading in batch and diff

**Value:** high. **Effort:** low.

`CaptureManifestBatchAnalyzer.Analyze` and `CaptureManifestDiffAnalyzer.Analyze`
iterate their case lists sequentially, and each case loads its trace or traces and
ranks them with no shared mutable state. Replace the inner loop with
`Parallel.ForEach`, writing results by index into a preallocated array or collecting
into a `ConcurrentBag` and sorting at the end.

Notes: the per-iteration warning list is already allocated per case; `TraceStore.Get`
may run its factory twice when two cases share a trace path (the documented LruCache
race), which is a tolerable transient double-load here, not a correctness bug.

### LP-2 - partition-and-reduce in FoldingAggregator

**Value:** medium-high. **Effort:** medium.

`SelfTime`, `InclusiveTime`, `CallersOf`, `HotLines`, `SourceHeatmap`, `CallTree`,
and `Classify` are sequential folds over `_samples` building a
`Dictionary<string, double>` and a running total. Partition with PLINQ's `Aggregate`
overload - thread-local dictionary and total per partition, one sequential merge at
the end, which is O(unique frame names) rather than O(samples).

Notes: `_shortCache` is already a `ConcurrentDictionary`; `IsFolded` only reads the
compiled `Regex[]`. Gate on a sample-count threshold so small traces do not pay
thread-pool overhead - roughly 5,000+ samples at 10+ frames average depth is where it
starts to pay.

### LP-3 - merge the activity pre-pass into the main read

**Value:** medium. **Effort:** medium. Largest benefit on machine-wide multi-process
ETL captures.

An activity scope currently costs two full event-stream scans:
`ComputeActivitySampleFilter` runs a complete `source.Process()` pass to build a
`HashSet<EventIndex>`, then `ReadCore` iterates `traceLog.Events` again. Drive
`ReadCore` from a `TraceLogEventSource` instead, register both the activity-computer
callbacks and the CPU-sample handler, and call `source.Process()` once.

### LP-4 - parallel DLL scanning in EmbeddedPdbExtractor

**Value:** low-medium. **Effort:** low.

Sequential `foreach` over 10-30 DLLs in a build output directory, each read fully
into memory, PE-parsed, and deflate-decompressed. `Parallel.ForEach` with the lazy
temp-directory initialization protected by a lock or `LazyInitializer`. Absolute gain
is tens of milliseconds, visible in agent workflows that call `--symbols` repeatedly.
TE-P1 would remove the need for this path entirely.

### LP-5 - parallel native symbol module lookups

**Value:** medium for the `--native-symbols` path. **Blocked on TE-P3.**

`ResolveNativeRuntimeSymbols` calls `LookupSymbolsForModule` sequentially per module,
each potentially a network round trip. Parallelizing needs one `SymbolReader` per
task and a confirmed thread-safety contract for concurrent calls against the same
`TraceLog.CodeAddresses`.

---

## Track E - upstream TraceEvent asks

These require changes in `Microsoft.Diagnostics.Tracing.TraceEvent` (pinned at 3.2.3
in [Directory.Packages.props](../Directory.Packages.props)).

| ID | Ask | Why filtrace wants it |
|---|---|---|
| TE-P1 | `SymbolReader.GetSourceLine` reads the embedded portable PDB (`.MPDB`) section directly from the PE image | deletes `EmbeddedPdbExtractor`, its 5-30 ms per `--symbols` run, its temp-directory lifecycle, and a resource-leak class |
| TE-P2 | async, cancellable ETLX conversion | lets the MCP server abandon a slow conversion when a client disconnects instead of holding the named mutex |
| TE-P3 | documented thread-safety contract for `LookupSymbolsForModule`, or a per-module async variant | unblocks LP-5 |
| TE-P4 | partitionable event stream (`traceLog.Events.Partition(n)`) | the highest-value and highest-complexity ask: parallel `ReadCore`. Only safe if TraceEvent can reconstruct symbol state at an arbitrary event index, or if ETLX guarantees every code-address reference resolves without prior-event context. Answer that first. |
| TE-P5 | precomputed `TraceProcess.CpuSampleCount` populated during the ETLX build | replaces the auto-scope pre-pass, which is a second full event scan. `TraceProcess.CPUMSec` is not a substitute: a long-lived background service accumulates more CPU milliseconds than a short benchmark while carrying a tiny fraction of the samples |

---

## Track F - platform and release

- **Native AOT stays blocked by TraceEvent** and is not a compatibility claim. See
  [design.md](design.md#known-constraints).
- **Stable release and registry work follows the v.next selection.** Freeze names,
  publish migration guidance, and add registry and badge collateral only after the
  transport, output contract, and surface are selected.
- **Re-audit the TraceEvent public surface whenever the pin moves**, and enter new
  findings here only after checking them against agent value, capture feasibility,
  dependency cost, and response bounds.

---

## Acceptance gates for a v.next candidate

The enforced gates and efficacy measures live in
[design.md](design.md#measures-of-success). A v.next candidate additionally
requires:

- deterministic tests and parity remain exact;
- summary-mode JIT and raw-event count tasks fall below 500 response tokens;
- duplicate payload copies are eliminated wherever the chosen clients permit it;
- tool-list target: at most 7,500 tokens if typed output schemas are retained, at
  most 5,000 if JSON-text-only wins;
- the 20% total-token reduction applies to a token-motivated breaking
  consolidation - not to every semantic v9 improvement. VN0 records the repeatable
  baseline before that threshold is locked.

## Risks

| Risk | Mitigation |
|---|---|
| Removing output schemas harms composition | transport A/B/C multi-model eval; retain typed output when inconclusive |
| Consolidated tools become bags of optional parameters | require discriminated schemas; keep separate tools when the SDK cannot express them |
| Summary defaults hide evidence | include counts and truncation diagnostics; provide explicit `rows`/`full` escalation |
| Query context inflates every response | omit inapplicable and null fields; measure total investigation cost after transport selection |
| Structured diagnostics become rigid | keep a human message and an extensible `data` object; version codes through schema revisions |
| CLI grouping hurts shell discoverability | compare top-level help and completion; retain intent-bearing commands |
| Compatibility aliases erase token gains | never advertise old and new MCP tools together; bound CLI aliases to one preview |
| Eval overfits one model | run multiple model families, repeat each task, reject any per-model success drop |
| Reclaimed schema headroom is spent on tool sprawl | hold the 7,000-token gate, keep the VN3 targets unchanged, and require VN3 to re-evaluate every tool including `trace_lifecycle` |
| Parallelism regresses small traces | gate LP-2 on a sample-count threshold and measure the fast path |

## Open decisions

Resolve these with VN0 and VN1 evidence rather than opinion:

1. Does JSON-text-only preserve agent composition well enough to remove advertised
   output schemas, now that they cost ~1,020 tokens rather than ~3,900?
2. Can the MCP SDK express useful discriminated `trace_source` and `trace_report`
   schemas without a large optional-parameter bag?
3. Should CLI report defaults stay detailed while MCP defaults to summary?
4. Does a manifest case reference improve follow-up reliability enough to justify a
   new addressing form?
5. Can global CLI format and detail options be implemented without making per-command
   help less clear?
6. Is one preview release of hidden aliases useful, or is a clean pre-1.0 break less
   confusing?
7. Where does `lifecycle` belong in a consolidated surface, given that its scope
   semantics differ from the other report kinds?

## Immediate next step

Implement VN0 only. Fix the live MCP eval accounting so the text/structured
duplication is measured accurately per client, add the comprehension tasks, and
produce a multi-model baseline. That evidence decides the transport, and the
transport decides how aggressively everything after it can change.
