# filtrace v.next product and agent-efficacy plan

**Status:** Proposed

**Date:** 2026-07-14

This is filtrace's single living improvement roadmap now that issue #42 is
complete. It combines two questions that were previously split across overlapping
plans: what analysis filtrace should add, and how an agent should discover, invoke,
and consume that analysis. Completed extraction and capability work is summarized in
[implementation-plan.md](implementation-plan.md) and
[filtrace-improvement-plan.md](filtrace-improvement-plan.md); Git history retains the
original detailed plans.

v.next is the explicit breaking-change decision required by
[AGENTS.md](../AGENTS.md) before any existing `trace_*` MCP tool can be renamed or
removed. Until a v.next surface is selected and versioned, the current names remain
frozen.

## Executive decision

The current surface works: the deterministic eval suite answers all 16 fixture-backed
tasks, most in one call, and ordinary JSON responses are already compact. The first
optimization should therefore be the MCP transport, not deleting useful operations.

Proceed in this order:

1. **Measure transport alternatives.** The live MCP response currently carries the
   same JSON in both `content[0].text` and `structuredContent`, while advertised
   output schemas consume almost half of the permanent tool-list budget. A/B-test
   this against JSON-text-only and structured-content alternatives.
2. **Make results self-describing.** Introduce a schema-v9 envelope with effective
   query context, stable diagnostics, structured next steps, and discriminated
   result kinds. Add summary/detail controls to the report families that produce
   the largest responses.
3. **Then test surface consolidation.** Compare the current 17 MCP tools with a
   proposed 13-tool surface, and the current 24 CLI verbs with a proposed
   15-command surface. Accept consolidation only when multi-model evals show equal
   or better task success, call count, and token use.
4. **Keep two renderings.** Dense text remains the terminal-human default. Compact
   deterministic JSON remains the canonical agent and automation format. Do not
   replace structured data with prose tables.

Do not build one universal `trace_query` tool. It would reduce the visible count at
the cost of a large polymorphic input schema, weaker tool selection, runtime-only
parameter validation, and a union result that is harder for both agents and humans
to understand.

## Baseline

The measurements below were taken from the Release MCP server and deterministic
eval baselines on 2026-07-14.

### Public surface

| Surface | Count | Notes |
|---|---:|---|
| CLI verbs | 24 | Includes four metric shortcuts and three operational cache/capture verbs. |
| MCP tools | 17 | Analysis/export only; capture and ETLX cache operations are CLI-only. |
| Stack metrics behind `rank` / `trace_rank` | 7 | CPU, thread time, allocation, exceptions, contention, wait, activity. |
| Deterministic eval tasks | 16 | All pass; 15 use one call and CPU caller drill uses two. |

The four CLI shortcuts are `cpu`, `alloc`, `exceptions`, and `threadtime`. They
all route to the same ranking engine as `rank --metric`, but expose narrower option
sets that are valid for their metric. They are useful human conveniences, not four
new analysis capabilities. MCP already presents these metrics through one
`trace_rank` tool.

### Permanent MCP schema cost

`tools/list` is presented to the model independently of which operation it will
call. The current list measures 33,764 characters and approximately 8,301 tokens
against the 9,000-token CI ceiling in
[Test-McpServer.ps1](../tools/Test-McpServer.ps1).

The script's comment still cites an older approximately 8,770-token, 16-tool
measurement. That figure predates `trace_batch` and the description compaction in
#52. The live 17-tool `tools/list` round trip is the baseline for this plan; VN0
must update the stale script rationale when it adds the per-tool breakdown.

| Component | Approx. tokens | Share |
|---|---:|---:|
| Output schemas | 3,920 | 47% |
| Input schemas | 3,017 | 36% |
| Tool descriptions | 734 | 9% |
| Names and JSON/schema structure | 630 | 8% |
| **Total** | **8,301** | **100%** |

The largest individual definitions are:

| Tool | Approx. tokens |
|---|---:|
| `trace_diff` | 828 |
| `trace_info` | 627 |
| `trace_rank` | 608 |
| `trace_timeline` | 581 |
| `trace_batch` | 572 |

This changes the optimization priority. Tightening prose alone cannot reclaim
meaningful headroom: descriptions are only about 9% of the list. Output schemas
are the largest lever, followed by input-schema consolidation.

### Per-call response cost

The deterministic JSON baselines in [eval/baselines.json](../eval/baselines.json)
show that most ordinary analysis responses are small:

- ranking, caller, process, and tree tasks: approximately 105-199 tokens;
- GC, timeline, thread-time, source-quality, batch, and diff tasks:
  approximately 292-886 tokens;
- JIT report: approximately 2,251 tokens;
- raw allocation-event query: approximately 5,538 tokens.

The current 25,000-token response ceiling in
[OutputBudget.cs](../src/Filtrace.Core/Output/OutputBudget.cs) remains appropriate.
The useful optimization is to avoid returning detail the question did not request,
not to raise or globally lower the ceiling.

### MCP response duplication

A live `trace_info` round trip returned these result members:

```text
content, structuredContent
```

`content[0].text` and `structuredContent` serialized to the same 121-token JSON
payload. The complete result wrapper measured approximately 257 tokens. This is a
wire-level duplication; how much of each copy a client puts in model context is
client-dependent and must be measured per host.

The current live-agent harness estimates the serialized completion result. Before
using it to compare transports, extend
[Invoke-AgentEval.ps1](../eval/Invoke-AgentEval.ps1) to record separately:

- text-content tokens;
- structured-content tokens;
- complete MCP result tokens;
- the client-visible value supplied to the model, where the host exposes it.

## Goals

v.next should improve efficacy, not merely reduce counts.

1. Preserve or improve correct tool selection and answer accuracy across multiple
   models.
2. Reduce context paid before the first call and payload tokens paid after each
   call.
3. Reduce avoidable trial-and-error calls caused by incompatible parameters or
   ambiguous defaults.
4. Make every result self-contained enough to quote later without reconstructing
   the original invocation.
5. Keep human CLI workflows terse and discoverable.
6. Keep the Core result model typed, deterministic, AOT-safe, and shared by both
   heads.
7. Keep the existing 9,000-token tool-list and 25,000-token response ceilings. A
   redesign must create headroom rather than move the limits.

## Non-goals

- Do not merge unrelated analysis families into one universal operation.
- Do not replace JSON objects with Markdown or fixed-width tables for agents.
- Do not abbreviate property names into opaque wire codes merely to save tokens.
- Do not remove precomputed percentages or deltas that prevent agent arithmetic
  errors unless eval evidence shows they are unused and costly.
- Do not make MCP capture, elevation, or ETLX-cache mutation operations. Those
  remain explicit CLI responsibilities.
- Do not add server-side opaque trace handles as the only way to address a trace;
  paths and manifest identities remain reproducible across sessions.

## Design principles

### Separate human and agent surfaces

CLI and MCP share analysis semantics, not necessarily the same discovery shape.
Humans benefit from short commands and grouped help. Agents benefit from explicit
intent-bearing tool names, constrained schemas, and machine-readable results.
Forcing one surface to mirror the other has created avoidable aliases in the CLI
and avoidable permanent schemas in MCP.

### Consolidate by intent, not implementation

A good consolidation has one user intent and compatible inputs. Examples:

- `gc`, `jit`, `threadpool`, and `diskio` are all bounded structured reports;
- `lines` and `heatmap` are both source-attribution views.

A bad consolidation combines different arity or side-effect contracts merely
because they share a helper. `diff`, `batch`, raw event paging, export, and ranking
should remain distinct.

### Prefer compile-time constraints

Use JSON-schema enums for metric, measure, report kind, source view, detail level,
and export format. Do not rely on a free-form string plus an error listing valid
values when the vocabulary is closed.

Mutually exclusive inputs such as `root` and `benchmark` should be represented or
described consistently in every affected operation. When JSON Schema cannot express
the exact conditional cleanly, the parameter descriptions and error must use the
same wording.

### Optimize total investigation cost

The relevant cost is:

```text
permanent tool definitions
+ all tool responses
+ retries caused by misunderstanding
+ final answer context
```

A smaller schema that causes an extra orientation or repair call can be a net loss.
Every surface change is therefore eval-gated on success, calls, response tokens, and
wall time.

## Proposed v.next CLI surface

The recommended CLI has 15 advertised commands. It removes metric aliases from the
canonical surface and groups only operations with closely related intent.

| Proposed command | Replaces or retains | Purpose |
|---|---|---|
| `info` | retains `info` | Trace identity, capability, process, and quality orientation. |
| `rank` | retains `rank`; absorbs `cpu`, `alloc`, `exceptions`, `threadtime` | Rank any stack metric. |
| `callers` | retains `callers` | Immediate caller/callee drill around one CPU frame. |
| `tree` | retains `tree` | Top-down CPU call tree. |
| `source` | combines `lines`, `heatmap` | Source ranking or one-file line heat map via `--view`. |
| `processes` | retains `processes` | Multi-process trace inventory. |
| `classify` | retains `classify` | Stack-scoped CPU runtime-work classification. |
| `report` | combines `gcstats`, `jitstats`, `threadpool`, `diskio` | Structured provider report selected by `--kind`. |
| `timeline` | retains `timeline` | Time-bucketed correlation and window discovery. |
| `diff` | retains `diff` | Direct or manifest-paired normalized comparison. |
| `batch` | retains `batch` | One ranking query across manifest cases. |
| `events` | retains `events` | Count or page raw events. |
| `export` | retains `export` | Write a human-viewable profile. |
| `collect` | retains `collect` | Record a Windows ETW trace. |
| `cache` | combines `convert`, `clean` | Inspect/build/remove the ETLX cache via `--action`. |

### CLI compatibility policy

Because the project is pre-1.0, v.next may remove old names. Still, use one preview
release to test migration ergonomics:

- old metric shortcuts may remain hidden aliases that print the canonical `rank`
  equivalent to stderr;
- `gcstats`, `jitstats`, `threadpool`, and `diskio` may remain hidden aliases for
  `report --kind`;
- `convert` and `clean` may remain hidden aliases for `cache --action`;
- aliases must not appear in top-level help, generated docs, or agent examples;
- remove the aliases before declaring the v.next surface stable.

If ConsoleAppFramework cannot hide aliases without polluting help or completion,
prefer a clean break over carrying two advertised surfaces.

### Why not remove all specialized CLI names

`callers`, `tree`, `timeline`, `diff`, `events`, and `export` communicate a clear
human intent more effectively than modes on `rank`. Keeping them avoids a large
`rank --view` option matrix and makes shell completion useful.

## Proposed v.next MCP surface

The recommended MCP candidate has 13 tools:

| Proposed tool | Status | Purpose |
|---|---|---|
| `trace_info` | retain | Orientation and quality/capability inspection. |
| `trace_rank` | retain | Seven stack metrics, self or inclusive. |
| `trace_callers` | retain | Immediate caller/callee CPU drill. |
| `trace_source` | combine `trace_lines` + `trace_heatmap` | Source attribution selected by `view=lines|heatmap`. |
| `trace_tree` | retain | Top-down CPU tree. |
| `trace_processes` | retain | Process inventory before explicit ETW scope. |
| `trace_classify` | retain | Runtime-work classification with optional native symbols. |
| `trace_report` | combine GC + JIT + thread-pool + disk-I/O tools | Bounded report selected by `kind`. |
| `trace_timeline` | retain | Temporal overview. |
| `trace_diff` | retain | Two-input comparison, including paired manifests. |
| `trace_batch` | retain | Manifest-wide ranking summary. |
| `trace_query_events` | retain | Count or page raw events. |
| `trace_export` | retain | Explicit file-writing operation. |

`trace_classify` remains separate from `trace_report`: it consumes CPU stacks,
supports root/process/benchmark scope, and can opt into networked native symbols.
The report families consume provider-specific structured events and have different
format support.

### Consolidation constraints

`trace_source` must use a discriminated input:

```json
{
  "view": "lines",
  "path": "app.nettrace",
  "method": "MyApp.Parse"
}
```

or:

```json
{
  "view": "heatmap",
  "path": "app.nettrace",
  "file": "Parser.cs"
}
```

The generated schema should express the two valid branches with `oneOf` if the MCP
SDK supports it. If the SDK flattens both into a bag of optional strings, retain the
two current source tools; saving one definition is not worth runtime-only grammar.

Likewise, `trace_report` should expose only report-common controls at the top level:
`path`, `kind`, and detail/cardinality. Kind-specific controls should be a typed
options branch or omitted. Do not create a tool where unrelated parameters are
silently ignored.

### Breaking-name policy

The existing `trace_*` names are frozen in the current line. Ship the consolidated
surface only as a deliberate major/pre-1.0 v.next contract. Do not advertise old and
new MCP tools together: doing so would increase schema cost and selection ambiguity
at the exact point the redesign is intended to improve.

## MCP transport experiment

Run three implementations behind an experimental build property or branch. Do not
change the public contract before the multi-model comparison.

Use one build-time property, `FiltraceMcpTransport`, with values
`Structured`, `StructuredMinimal`, and `JsonText`. Conditional registration may
use compile constants if the SDK attribute requires a constant
`UseStructuredContent` value. Build each variant into a separate
`artifacts/vnext/<variant>/` directory and add an `-McpDll` override to the live
agent harness so every run generates a temporary MCP config pointing at the exact
variant. Stamp the variant into the result label and file name. Do not overwrite
`eval/baselines.json`: the deterministic gate continues to validate semantic JSON,
while transport comparisons live in the ignored `eval/results/` artifacts.

### Variant A: current typed structured content

- `UseStructuredContent = true`;
- full per-tool output schema;
- SDK-generated JSON text mirror plus structured content.

This is the correctness baseline.

### Variant B: structured content with minimal text

- keep typed `structuredContent` and output schemas;
- return only a short compatibility text block if the SDK/client permits it;
- verify clients do not require the JSON mirror.

This can remove per-call duplication but does not reduce the permanent 3,920-token
output-schema cost.

### Variant C: compact JSON text only

- advertise no output schema;
- return `OutputJson.Serialize(envelope)` as text content;
- preserve the same deterministic JSON field names and schema version;
- document the schema in the package/docs rather than repeating it in every
  `tools/list` response.

Based on the current breakdown, removing output schemas alone would reduce the
permanent list from approximately 8,301 to approximately 4,400 tokens before any
tool consolidation. It would also avoid `structuredContent` duplication. The tradeoff
is loss of MCP-advertised result typing, so this variant wins only if agent accuracy
and composition remain neutral or improve.

### Transport acceptance

For each variant, record:

- tool-list characters and estimated tokens;
- per-tool input, output, description, and total definition tokens;
- text, structured, complete-wire, and client-visible result tokens;
- task success, expected-tool success, calls, wall time, and final-answer accuracy;
- behavior in Copilot CLI and at least one additional MCP client/model family.

Recommended selection rule:

1. reject any variant with a success regression on any model;
2. reject any variant that increases median calls;
3. among the remaining variants, choose the lowest total investigation tokens;
4. retain typed structured output when the difference is inconclusive.

## Output contract v9

After selecting the transport, introduce one semantic output revision. Avoid mixing
transport and result-shape changes in the same A/B run.

### Effective query context

Every result should identify what actually ran, not only what the caller requested:

```json
{
  "operation": "rank",
  "metric": "cpu",
  "measure": "self",
  "unit": "ms",
  "scope": {
    "process": "MyApp",
    "root": "WorkloadAction",
    "startMs": null,
    "endMs": null
  }
}
```

Include only fields meaningful to the operation, and omit null values. The resolved
process is important for auto-scoped ETW traces; the resolved metric/unit prevents a
result copied out of its invocation from becoming ambiguous.

### Structured diagnostics

Replace the JSON `warnings: string[]` channel with stable diagnostic records while
retaining a human message:

```json
{
  "code": "thin_scope",
  "severity": "warning",
  "message": "Only 32 periodic CPU records contribute to this method scope.",
  "data": {
    "contributingRecords": 32,
    "recommendedMinimum": 200
  }
}
```

Initial stable codes should cover:

- low frame-name resolution;
- low source mapping;
- PDB identity mismatch;
- unknown/disabled capture status;
- thin method or line scope;
- ambiguous frame/root match;
- truncated rows/payload;
- ignored format-specific scope;
- case-local manifest failure.

Text renderers continue to print the message. Agents may branch on `code` without
parsing prose.

### Structured next steps

Replace CLI-shaped hint strings with operation-neutral records:

```json
{
  "operation": "callers",
  "reason": "drill into the hottest CPU frame",
  "arguments": {
    "frame": "MyApp.Inner"
  }
}
```

The CLI adapter renders this as a shell command. The MCP adapter maps `operation` to
`trace_callers` and passes the arguments directly. Scope-preserving arguments belong
in the record so an agent does not accidentally lose process/root context.

### Discriminated results

Use a `kind` field where one result currently represents unrelated shapes. The most
important case is diff:

```json
{
  "kind": "trace",
  "beforeScopeWeight": 10,
  "afterScopeWeight": 12,
  "rows": []
}
```

versus:

```json
{
  "kind": "manifest",
  "cases": []
}
```

Do not serialize empty `cases` on direct diffs or empty direct-trace totals on
manifest diffs. Apply the same rule to consolidated source/report results.

### Null and default omission

Configure the v9 serializer to omit null optional properties. Consider omitting
semantically absent default fields only when the schema makes the omission
unambiguous. Keep empty arrays when they mean "the query ran and found none"; omit an
array only when that concept does not apply to the selected result kind.

### Detail profiles

Use a small, closed detail vocabulary where it changes response cardinality:

- `summary`: aggregates and counts only;
- `rows`: aggregates plus the normal bounded rows;
- `full`: the largest supported bounded detail.

Do not add `detail` to operations whose result is already intrinsically small.
Recommended defaults:

| Operation | MCP default | Detail behavior |
|---|---|---|
| info | `summary` | Source/PDB method/module lists require `rows` or an explicit source section. |
| rank/callers/tree/source | current bounded rows | `top`/depth remains the natural control. |
| GC/JIT/disk reports | `summary` | Per-GC, per-method, or per-file records require `rows`. |
| thread-pool report | `summary` | Already small; `rows` may expose adjustment reasons if useful. |
| events | count/summary | Event records require `rows`; paging remains `skip`/`take`. |
| timeline | current bounded buckets | Lanes and bucket count remain the natural controls. |
| diff/batch | current structural caps | Already designed as compact agent summaries. |

CLI text may default to `rows` for interactive reports while MCP defaults to
`summary`; both must accept an explicit detail selection and serialize the same
result contract.

That asymmetry is a candidate, not a predetermined decision. VN0 must compare
`summary` and `rows` defaults on questions that need only aggregates and on questions
that require evidence rows. Reject a summary default when the saved first-response
tokens are offset by enough detail-escalation calls to increase total tokens or
median calls. Deterministic tasks should pass an explicit detail level so their
goldens do not depend on host-specific defaults.

### Manifest case references

Batch currently repeats a trace path per case so the agent can call `rank`. Prefer a
stable case reference containing `manifestPath` plus `caseId`, and allow follow-up
operations to accept that pair. Keep the resolved path in `full` detail for audit and
CLI display, but avoid requiring an agent to reconstruct or copy long absolute paths.

Manifest schema v1 already requires each case to have an `id`; this change exposes
that existing identity in `BatchRankingCaseResult` rather than inventing a new
manifest field. Follow-up operations resolve the id through the bounded manifest
reader and reject missing or duplicate ids. Existing valid manifests therefore need
no schema migration. The result-envelope schema still changes in v9 because batch
output gains `caseId` and may omit its repeated path at lower detail levels.

## Agent-comprehension improvements

### Canonical vocabulary

Agent-facing docs and server instructions should use canonical operations only:

- `rank` with a metric, not the CLI aliases;
- `report` with a kind;
- `source` with a view.

Human shortcut aliases, while they exist, belong in a CLI-only compatibility section.
This avoids teaching `cpu` and then inviting an agent to invent a nonexistent
`trace_cpu` tool.

### Conditional orientation

Change "always call `trace_info` first" to:

> Call `trace_info` first when format, provider availability, process scope, or
> symbol/source quality is unknown. Skip it when the prompt and prior result already
> establish those facts.

This preserves the quality gate without imposing an unnecessary call on every
single-purpose query.

### Explicit compatibility

Every operation description should state:

- accepted formats;
- default process behavior;
- whether it is CPU-only;
- whether `root` and `benchmark` conflict;
- whether native symbols use the network;
- what detail is returned by default.

Keep this compact and consistent. Prefer shared generated wording or tests over
copying near-identical prose that drifts.

### Actionable ambiguity diagnostics

An ambiguous frame/root diagnostic should include:

- match count;
- selected definition and selection policy;
- a bounded list of candidate definitions;
- a structured next step recommending a narrower selector.

The agent should not need to infer how to repair an ambiguity from a paragraph.

## Capability and platform backlog

These are the remaining analysis and distribution opportunities consolidated from
the former capability plan and the TraceEvent surface audit. They are ordered by
expected user value, implementation cost, and fit with the proposed v.next surface.
None should add a standalone MCP tool before VN3 selects the final surface.

| ID | Capability | Proposed v.next surface | Priority | Main gate |
|---|---|---|:---:|---|
| VC1 | DATAS server-GC tuning | `report --kind datas` / `trace_report(kind=datas)` | High | Capture and parser parity |
| VC2 | Point-in-time snapshot | `timeline --mode snapshot` / `trace_timeline(mode=snapshot)` | Medium | Prove it beats timeline + rank |
| VC3 | Per-frame temporal buckets | `rank --temporal` or `detail=full` | Medium | Response and aggregation cost |
| VC4 | PMC / CPU-counter ranking | New `rank` metric | Medium | ETW capture support and fixture |
| VC5 | Retention / leak analysis | Dedicated retention result; surface decided in VN3 | Medium | PerfView graph dependency |
| VC6 | Net surviving heap | New stack metric | Low | `GCHeapSimulator` extraction |
| VC7 | Physical ETL trim | `trim` or `cache --action trim` | Low | Preserve JITted managed frames |
| VC8 | Activity and file-I/O follow-ups | Extend existing scopes/reports | Low | Demand and capture volume |

### VC1 - DATAS server-GC tuning

DATAS is the highest-value remaining analytical gap. It explains Dynamic Adaptation
To Application Sizes decisions in modern server GC: heap-count transitions,
per-collection budget/throughput-cost/wait samples, and gen-2 backstop tuning.

Implementation shape:

- Read `TraceGC.DynamicEvents` and parse the packed little-endian DATAS payloads
  into immutable result records.
- Return aggregate heap-count min/max/transitions and bounded tuning/sample rows;
  support a changes-only detail mode for long traces.
- Capture and commit a small DATAS-enabled `.nettrace`; unit-test the binary offsets
  independently of the trace fixture.
- Verify whether an appropriately configured `.etl` exposes the same dynamic events
  before declaring the report EventPipe-only.
- Route heap-count churn to the existing GC report through a structured next step.
- If implementation is ported from pvanalyze, carry its exact MIT copyright notice
  in the source file, add third-party notice text, and retain source provenance.

The old plan proposed `datas` / `trace_datas`. Under v.next it belongs in the
consolidated report family unless the VN3 schema experiment proves that a typed
report discriminator is worse for agents.

### VC2 - Point-in-time snapshot

A snapshot answers "what was happening around this millisecond?" with a bounded
window containing GC activity, top CPU work, exceptions, allocations, JIT activity,
and event counts. All underlying readers now exist, and timeline already identifies
the interesting window.

Implement this as `timeline` mode rather than a new tool. Before committing to it,
add an eval task comparing one snapshot call with the existing timeline-then-rank
flow. Accept the mode only when it reduces calls or gives materially better
cross-lane evidence. The result must identify the exact window and preserve process
scope.

### VC3 - Per-frame temporal buckets

pvanalyze can attach a small temporal histogram to each hot CPU method. Filtrace can
already find a busy window globally and rerun `rank --time`; per-frame buckets would
make bursty methods visible in one ranking but increase aggregation work and repeat
data per row.

Prototype buckets only for CPU periodic samples, behind an explicit option or full
detail profile. Cap both row count and bucket count. Reject the feature if it breaks
the 25,000-token response bound, materially slows ordinary ranking, or fails to save
a follow-up call in evals.

### VC4 - PMC / CPU-counter ranking

TraceEvent exposes profile-source metadata and PMC sample events for cache misses,
branch mispredicts, retired instructions, and related hardware counters. The
analysis can fit the existing `{stack, weight}` engine as another metric; the hard
part is a reliable Windows ETW capture path, machine support detection, and a
committed or deterministic test fixture.

Do not expose the metric until capture metadata names the counter and unit. An
unsupported machine or trace must produce a capability diagnostic rather than an
empty ranking that looks authoritative.

### VC5 - Retention / leak analysis

Retention answers which live objects remain and what root path keeps them alive. It
is not an allocation ranking and does not fit `FoldingAggregator` without a separate
heap-graph object model.

TraceEvent 3.2.3 does not package the required `MemoryGraph`, `GCHeapDump`, or
reference-graph implementation. `dotnet-gcdump` vendors roughly 173 KB of the
relevant PerfView graph source, and path-to-root analysis needs additional work.
Before implementation:

1. decide whether to vendor the MIT PerfView graph subset, consume a future factored
   package, or integrate an external tool;
2. verify unsafe, trimming, and AOT implications;
3. define bounded type/root/path summaries and a capture handoff through
   `dotnet-gcdump`;
4. keep allocation-rate and retention terminology separate in every result and
   hint.

Because this is a distinct data model, VN3 may retain a dedicated MCP tool even if
the rest of the report family consolidates.

### VC6 - Net surviving heap

Net-memory stacks estimate surviving bytes by allocation site across collections.
The required `GCHeapSimulator` is PerfView-side rather than available in the pinned
TraceEvent package. Treat this as a dependency/extraction investigation, not a
small provider addition. Allocation rate remains the supported answer until the
simulator can be reused with bounded memory and verified parity.

### VC7 - Physical ETL trim

Analysis-time process and time scoping is lossless and remains the default. A
physical relog is valuable only for transport, committed fixtures, and repeated
analysis of very large machine-wide captures. The existing fixture relog preserves
disk events and native modules but does not rebuild the JITted managed-method address
map, so managed stacks become unresolved.

Do not ship the trim until that limitation is fixed or the command is explicitly
limited to native/event transport scenarios. The current implementation and the
managed-frame failure are documented in
[filtrace-etl-trimming.md](filtrace-etl-trimming.md).

### VC8 - Lower-priority enrichments

- Extend activity scope beyond CPU only when allocation/exception event correlation
  can preserve async activity identity accurately.
- Consider logical File I/O separately from physical disk I/O only when a concrete
  cache-served-I/O question justifies its much higher event volume.
- Re-audit the TraceEvent public surface whenever its pinned version changes; the
  current assessment is recorded in
  [traceevent-surface-assessment.md](traceevent-surface-assessment.md).

### Platform and release work

- **Native AOT remains blocked by TraceEvent.** It relies on reflection, dynamic
  event parsers, and ETW native interop and is not annotated as trim/AOT safe. Do
  not set `IsAotCompatible` or `PublishAot` on filtrace projects until a real publish
  succeeds across the analysis graph.
- **Run the live tuning rounds.** The deterministic and live-agent harnesses exist;
  v.next requires repeatable multi-model baselines rather than one-off smoke runs.
- **Stable release and registry work follows v.next selection.** Freeze names,
  publish migration guidance, and add registry/badge collateral only after the
  transport, output schema, and command/tool surface are selected.

### Capability sequencing

VN0 and VN1 come first because transport and schema decisions determine how new
capabilities should be exposed. VC1 (DATAS) is the first capability candidate after
the report surface is selected. VC2 and VC3 are eval-gated temporal alternatives;
build at most one before measuring real demand. VC4-VC8 remain demand- or
dependency-gated and must not delay the v.next surface cleanup.

## Eval and measurement plan

The eval harness is the decision mechanism, not a final regression check.

### Harness changes

1. Record schema-token breakdown per tool in the test artifact.
2. Record MCP text/structured/wire/client-visible response tokens separately.
3. Add an experimental server path/config so baseline and candidate surfaces can be
   run without editing committed task expectations between runs.
4. Grade expected operation intent as well as exact tool name, allowing a controlled
   comparison of old and consolidated surfaces.
5. Run at least three iterations per task and compare medians; one-shot success is
   too noisy for surface decisions.

Add each comprehension scenario as a normal `eval/tasks/*.json` task with a
canonical deterministic step and `prompt`/`expect` fields. Add the matching row to
`eval/mcp-qa.jsonl`. Extend the live-agent-only task schema with `expectOperations`,
`forbidOperations`, and an optional maximum-call override so selection behavior can
be graded without changing the deterministic CLI runner. `expectTools` remains
accepted while the current surface is the baseline.

### New comprehension tasks

Add tasks that exercise failure-prone decisions rather than only happy-path numeric
answers:

- choose `rank metric=alloc` without inventing `trace_alloc`;
- skip source-line tools for speedscope;
- distinguish enabled-zero, disabled, and unknown capture status;
- preserve process/root scope from ranking into callers;
- reject or repair `root` plus `benchmark`;
- disambiguate multiple matching frames;
- request report summary first, then detail only when needed;
- count raw events without returning an event page;
- escalate from batch case reference to one detailed ranking;
- choose `classify` rather than a generic report for native runtime CPU work.

### Acceptance gates

A v.next candidate is acceptable only when:

- deterministic tests and parity remain exact;
- no model/task success rate regresses;
- median tool calls do not increase;
- p95 calls remain within the current six-call ceiling;
- total investigation tokens fall by at least 20% on the multi-model suite;
- the tool-list stays below 9,000 tokens without raising the ceiling;
- no standard result exceeds 25,000 tokens;
- summary-mode JIT and raw-event count tasks fall below 500 response tokens;
- CLI help remains within its line budget and documents every advertised command;
- all JSON remains deterministic, AOT-safe, and schema-versioned.

Transport-specific targets:

- if typed output schemas remain, target at most 7,500 tool-list tokens after
  consolidation;
- if JSON-text-only wins, target at most 5,000 tool-list tokens;
- eliminate duplicate payload copies where the chosen clients permit it.

The 20% total-token reduction is the acceptance gate for a token-motivated breaking
surface consolidation, not for every semantic v9 improvement. VN0 records the
repeatable baseline before locking that threshold. A structured diagnostic or query
context change may proceed with a smaller token win when it measurably improves
accuracy or removes repair calls, but a rename/removal justified as simplification
must clear the 20% gate.

## Delivery milestones

### VN0 - Baseline and instrumentation

- Freeze current 24/17 surface results across multiple models.
- Extend schema and result token accounting.
- Add the comprehension tasks above.
- Record current success, calls, tokens, and wall time as the v.next baseline.

**Exit:** repeatable baseline artifacts identify permanent schema, wire response,
and model-visible costs separately.

### VN1 - Transport selection

- Implement variants A, B, and C behind an experimental build path.
- Run the complete multi-model suite.
- Select one transport by the acceptance rule.

**Exit:** one documented transport decision with measured accuracy and total-token
tradeoffs; no result-shape changes yet.

### VN2 - Output contract v9

- Add effective query context.
- Add structured diagnostics and next steps.
- Add discriminators and null omission.
- Add summary/detail behavior to report/event/info outliers.
- Update source generation, golden files, budgets, and both renderers.

**Exit:** results are self-describing, compact by default, and can route a follow-up
without parsing prose.

### VN3 - MCP surface experiment

- Prototype `trace_source` and `trace_report`.
- Compare 17-tool and 13-tool variants using operation-intent grading.
- Keep split tools when conditional schemas are weak or selection regresses.

**Exit:** selected MCP surface meets success/call gates and the applicable 7,500 or
5,000 tool-list target.

### VN4 - CLI surface

- Advertise the 15-command surface.
- Add hidden compatibility aliases for one preview only when the framework supports
  them cleanly.
- Move format/detail controls to shared/global option handling where feasible.
- Regenerate help, workflow docs, README, and the shipped skill.

**Exit:** top-level help presents one canonical path per intent; aliases do not leak
into agent guidance.

### VN5 - Stabilization

- Remove preview aliases selected for removal.
- Run Debug/Release tests and every repository contract/eval gate.
- Publish a migration table from every old CLI verb/MCP tool to v.next.
- Freeze the selected v.next names and schema.

**Exit:** one documented, eval-backed surface is ready for the next stable package.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Removing output schemas harms composition | Transport A/B/C multi-model eval; retain typed output when inconclusive. |
| Consolidated tools become bags of optional parameters | Require discriminated schemas; retain separate tools when the SDK cannot express them. |
| Summary defaults hide evidence | Include counts and truncation diagnostics; provide explicit `rows`/`full` escalation. |
| Query context increases every response | Omit inapplicable/null fields and measure total investigation cost after transport selection. |
| Structured diagnostics become rigid | Keep a human message and extensible data object; version codes through schema revisions. |
| CLI grouping hurts shell discoverability | Compare top-level help/completion and retain intent-bearing commands such as `diff` and `timeline`. |
| Compatibility aliases erase token gains | Never advertise old and new MCP tools together; bound CLI aliases to one preview. |
| Eval overfits one model | Run multiple model families and repeat each task; reject any per-model success drop. |

## Open decisions

Resolve these with VN0/VN1 evidence rather than opinion:

1. Does JSON-text-only preserve agent composition well enough to remove advertised
   output schemas?
2. Can the MCP SDK express useful discriminated `trace_source` and `trace_report`
   schemas without a large optional-parameter bag?
3. Should CLI report defaults remain detailed while MCP defaults to summary?
4. Does a manifest case reference improve follow-up reliability enough to justify a
   new addressing form?
5. Can global CLI format/detail options be implemented without making per-command
   help less clear?
6. Is one preview release of hidden aliases useful, or is a clean pre-1.0 break less
   confusing?

## Recommended immediate next step

Implement VN0 only. In particular, fix the live MCP eval accounting so the current
text/structured duplication is measured accurately per client, add the comprehension
tasks, and produce a multi-model baseline. That evidence decides the transport and
therefore determines how aggressively v.next can improve both permanent schema cost
and per-call output without sacrificing agent success.
