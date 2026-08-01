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
call. At the time of this baseline the list measured 33,764 characters and
approximately 8,301 tokens against what was then a 9,000-token CI ceiling in
[Test-McpServer.ps1](../tools/Test-McpServer.ps1). [SC1](#sc1---exact-process-identity-scope)
then added the exact-scope parameters, taking it to approximately 8,950.

The list now measures approximately **6,388** tokens against a 7,000-token ceiling,
after the output schemas were compacted and SC5's `trace_lifecycle` was added - see
[Output schemas were the budget](#output-schemas-were-the-budget). The shares that
motivated the reduction were:

| Component | Approx. tokens | Share |
|---|---:|---:|
| Output schemas | 3,920 | 47% |
| Input schemas | 3,017 | 36% |
| Tool descriptions | 734 | 9% |
| Names and JSON/schema structure | 630 | 8% |
| **Total** | **8,301** | **100%** |

Measured again before the reduction, at 9,044 tokens, the shares held: output schemas
4,014 (44%), input schemas 3,703 (41%), descriptions 734 (8%). Descriptions were always
the smallest share, so prose was never where a breach could be answered.

### Output schemas were the budget

Every tool advertised a fully expanded `outputSchema`: an identical
`schemaVersion` / `warnings` / `hints` envelope repeated seventeen times, plus each
result type expanded in full. `trace_info` spent 518 tokens advertising its output
against 153 describing its inputs.

This was previously recorded as unreclaimable, on the grounds that
ModelContextProtocol couples the advertised schema to structured content. That is not
so: `McpServerToolAttribute.OutputSchemaType` decouples them. Pointing every tool at
[AnalysisEnvelopeSchema](../src/Filtrace.Core/Output/AnalysisEnvelopeSchema.cs) - the
envelope with the payload left unexpanded - took the output-schema share from 4,014 to
1,020 tokens and the list from 9,044 to 6,050, a 33% reduction.

Structured content is unaffected: it is serialized from the returned
`AnalysisResult<T>`, not from the advertised schema, so clients receive the same typed
payload they did before. What a client no longer learns up front is each tool's exact
result shape. The trade was judged worth it because that shape costs context on every
conversation, whereas a caller learns it from the first result, and because the
alternative levers - prose, at 8%, and input schemas, which the model needs to call
correctly - are respectively too small and load-bearing.

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
7. Hold the tool-list and 25,000-token response ceilings. A redesign must create
   headroom rather than move the limits. The one recorded exception,
   [SC5](#sc5---process-lifecycle-report), has been retired: compacting the output
   schemas took the list to approximately 6,050 tokens and the gate to 7,000, below
   both the original 9,000 ceiling and the 7,500 VN3 target. SC5's tool then landed
   inside that headroom, taking the list to approximately 6,388.

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
dependency-gated and must not delay the v.next surface cleanup. The SC items below
are tracked separately because they are issue-driven correctness gaps rather than new
analytical capabilities.

## Startup and short-command capture (#62)

[Issue #62](https://github.com/JeremyKuhne/filtrace/issues/62) records what filtrace
could not do while profiling the cached Native AOT file-run path for
[dotnet/sdk#55529](https://github.com/dotnet/sdk/pull/55529): explain an end-to-end
command that completes in roughly 55 ms and launches a short-lived apphost child.
Filtrace 0.6.3 read the traces, but reaching trustworthy parent CPU and wall-clock
phases required an isolated filtrace build plus investigation-specific capture and
aggregation scripts.

The goal of this initiative is to delete that fork. SC1-SC3 are correctness gaps in
capabilities filtrace already claims, and none of them depend on the transport or
output-schema decisions, so they can ship before VN1. SC4-SC7 add surface and are
therefore subject to the same gates as the VC backlog.

| ID | Gap | Proposed surface | Priority | Status |
|---|---|---|:---:|---|
| SC1 | Scope cannot name exact processes | `--pid` / `--children` on the scope-aware verbs and tools | High | Shipped (#63) |
| SC2 | Local native PDBs are never applied to non-runtime modules | `--symbols` behavior fix plus per-module status | High | Shipped (#65) |
| SC3 | Capture always enables CLR plus disk and network keywords | `collect --profile` | High | Shipped (#64) |
| SC4 | One ETW session per short invocation | `collect --iterations` plus a command-matrix script | Medium | Shipped (#66) |
| SC5 | No wall-clock phase report | Lifecycle verb and tool over process/image events | Medium | Shipped |
| SC6 | Sub-millisecond sampling rejected before collection | Widened range plus effective-interval reporting | Low | Open; needs platform measurement |
| SC7 | No short-startup recipe for agents | `workflow.md`, `traps.md`, shipped skill | Low | Open; gated on SC1-SC6 |

### SC1 - Exact process-identity scope - shipped (#63)

[ProcessScope](../src/Filtrace.Core/Tracing/ProcessScope.cs) stored a process-name
substring, so `--process dotnet` selected every matching root in a machine-wide trace
and, by default, all of their descendants. On a development machine that silently
mixed unrelated hosts into a ranking. Two further limits compounded it: `ScopeRequest`
already carried `IncludeChildren`, but no verb or tool exposed it, and the resolved
scope reached the caller only as warning prose.

What shipped:

Implementation shape:

- Replaced the name string in `ProcessScope` with a `ProcessSelector` union - a name
  selector and a process-id selector - so an exact identity is represented rather
  than encoded into a substring. This broke the public `ProcessScope` constructor and
  the `ScopeRequest.ProcessName` property, which `Selector` replaces.
- Added `ScopeRequest.ForProcessIds` and `ScopeRequest.AutoScope(includeChildren)`.
  [ProcessTree.ResolveScope](../src/Filtrace.Core/Tracing/Readers/ProcessTree.cs) now
  returns a `ScopeResolution` (pids, label, prose phrase, advisories) so the three
  consumers (`TraceLogReader`, `ThreadTimeProvider`, `TimelineProvider`) report an id
  scope without duplicating message construction.
- Exposed `--pid` (one comma-separated list; ConsoleAppFramework binds an array option
  from a single value, and a repeated option silently keeps only the last) and
  `--children include|exclude` on the scope-aware verbs, and the same two parameters
  on the corresponding `trace_*` tools. `--children exclude` is what produces a
  parent-only CPU ranking.
- Guarded process-id reuse: a reused pid appears as more than one `TraceProcess` in
  the same trace, so a `--pid` selector that matches several fails with the candidate
  start times rather than quietly unioning them. Ids absent from the trace are named.
- Warned when a name selector resolves to more than one independent root tree,
  listing the matched roots and naming `--pid` as the exact alternative.

Decided: descendants stay included by default for both selector kinds, so `--pid`
differs from `--process` only in how roots are chosen. Interactive discovery keeps
the name selector; exact scope is for manifests and automation.

Two defects surfaced only under a real CLI/server run, not under unit tests: a
repeated `--pid` silently keeps the last value (so every generated hint must join ids
with a comma), and the MCP head needs each tool-parameter type registered in the
source-generated `FiltraceJsonContext` or the server fails at startup. Both are pinned
by tests now. The descendant mode also has to key every axis that resolves to a
process set - the cache key, the manifest-scope fallback, and the drill-down hints all
had to stop branching on `Selector` alone.

The resolved scope must also be readable without parsing prose. That is the same
requirement as [effective query context](#effective-query-context) in output contract
v9, so SC1's structured reporting is deferred to VN2, which extends that `scope`
object with the applied selector and the resolved root and descendant ids rather than
adding a second channel.

### SC2 - Local native symbols for arbitrary modules

`--symbols` adds a directory to the `SymbolReader` symbol path, but
[ResolveNativeRuntimeSymbols](../src/Filtrace.Core/Tracing/Readers/TraceLogReader.cs)
is the only caller of `LookupSymbolsForModule`, it runs only under `--native-symbols`,
and it filters modules through a runtime allowlist (`coreclr`, `clr`, `clrjit`,
`ntdll`, `kernelbase`, `kernel32`, `ucrtbase`, `msvcrt`). Nothing ever asks TraceEvent
to apply a local PDB to a product-specific native module, so a matching local
`dotnet-aot.pdb` can sit in the supplied directory and never resolve
`dotnet-aot.dll`. In the investigation that hid the inclusive Native AOT ancestors -
`NativeEntryPoint.Execute` among them - that separate host startup from command code.

Implementation shape:

- Add a local native lookup pass that runs whenever `--symbols` is supplied, ordered
  before the `srv*` element is appended to the symbol path, so the pass runs against
  a path with no server element on it.
- Bound the pass by unresolved sample weight per module rather than by module count.
  The pre-pass that ranks modules by unresolved frames is also what produces the
  "highest-sample unresolved native modules" list the issue asks for.
- Report per-module lookup status - resolved, no PDB found, identity mismatch, not
  attempted - with the module's unresolved sample share. `SymbolReader` is currently
  constructed with `TextWriter.Null`; capturing that log is where the identity
  mismatch detail comes from.
- Keep `--native-symbols` as the only network gate. Local paths and the public cache
  already compose, because native resolution appends the server rather than replacing
  the local path.

`pdb_identity_mismatch` is already a planned stable diagnostic code in
[structured diagnostics](#structured-diagnostics); SC2 supplies its `data` payload.

Shipped in #65, with two departures from the shape above worth recording.

Identity mismatch is decided from the module's recorded `PdbName`, `PdbSignature`, and
`PdbAge` rather than by capturing the `SymbolReader` log - a structural comparison
instead of one that depends on message prose. A failed lookup gained its own reported
category; modules below the share the pass spends a lookup on stay unreported, so
`UnresolvedFrameCount` documents that it can exceed what the reported lists account for.

End-to-end verification is a CI gate
([tools/Test-NativeSymbolResolution.ps1](../tools/Test-NativeSymbolResolution.ps1)) that
builds a small C++ workload, captures it, and asserts the frames resolve with `--symbols`
and do not without. It cannot be a committed fixture: a filtrace capture records no PDB
identity of its own, so TraceEvent resolves a native module by reading the binary back
from the absolute path recorded in the trace, and a committed capture therefore only
resolves on the machine that took it. Making such a fixture possible needs the
cross-machine symbol injection (the PerfView "merge" step) that
[EtwCollector](../src/Filtrace.Core/Tracing/EtwCollector.cs) notes as a follow-up.

### SC3 - Capture provider profiles

[EtwCollector](../src/Filtrace.Core/Tracing/EtwCollector.cs) always enables the CLR
`Default` provider at `Verbose` after the kernel provider. For a Native AOT parent
those events are pure perturbation. The issue reports a default capture showing a
197.2 ms root lifetime and 138.7 ms before child creation, against 27.86 ms for a
comparable kernel-only recapture and a 24.33 ms uninstrumented wrapper-overhead
estimate, and roughly 8 GB across the discarded capture set. Those captures were
discarded rather than compensated for.

The CLR provider is only half of it. `KernelTraceEventParser.Keywords.Default` is
`0x0101270F` in the pinned TraceEvent 3.2.3 - `Process | Thread | ImageLoad |
ProcessCounters | DiskIO | DiskFileIO | DiskIOInit | MemoryHardFaults |
NetworkTCPIP | Profile` - so today's CPU capture also enables the machine-wide
`DiskFileIO` name rundown that
[traceevent-surface-assessment.md](traceevent-surface-assessment.md) measures at over
650,000 events, plus TCP/IP, for a capture that reads none of them.

Decided: named capture profiles rather than a single CLR toggle.

| Profile | Kernel keywords | CLR | Purpose |
|---|---|---|---|
| `default` | `Keywords.Default` | Default at Verbose | Today's behavior, unchanged |
| `startup` | `Process \| Thread \| ImageLoad \| Profile` (`0x01000007`) | Off | Low-perturbation startup and CPU |
| `threadtime` | `Keywords.ThreadTime` | Default at Verbose | Today's `--metric threadtime` |

`startup` keeps every event the CPU, thread-time, and lifecycle analyses read.
Record the selected and observed providers, the sample interval, and the effective
sample interval in the collect result and in the capture sidecar, so a trace can be
audited after the fact. Reconcile `--profile` with the existing `--metric` before
implementation; two options selecting overlapping keyword sets is worse than one.

### SC4 - Repeated invocations in one session

An ETW session costs roughly 900 ms of startup and flush, which dominates a 30-100 ms
process and produces many thin traces and ETLX conversions. The investigation instead
ran 25 sequential invocations inside one session per scenario: eight sessions, eight
conversions, hundreds of parent samples.

`EtwCollector` already creates and enables the session before launching, so this is a
loop around the launch and wait, not a restructure. Split the work:

- The CLI owns session-level correctness: an iteration count, and a per-invocation
  record carrying ordinal, executable, arguments, root process id, start and stop
  timestamps, and exit code.
- A `Capture-CommandTrace.ps1` skill script owns the scenario matrix, one tested
  elevation handoff for the whole run, `capture.log`, environment and working
  directory identity, symbols, tool version, and a partial manifest when one
  invocation fails. [Capture-BenchmarkTrace.ps1](../.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1)
  is the working precedent for all of those.

The manifest records root process ids only. Descendants are resolved from the trace
by SC1, so capture never has to track children. Decided: extend the existing capture
manifest with an `invocations` array and a `kind` discriminator and bump
`schemaVersion`, so `batch` and `diff` keep working across command scenarios instead
of needing a parallel consumer.

Shipped in #66, plus one addition and one gap worth recording.

`collect` also gained `--format json`, which every other verb already had. The capture
script has to record each launch accurately, and reading them back out of the human
summary is not a contract worth depending on - the launched command shares that stream.

Scoping remains by process *name*, so a command matrix warns that the name matched
several unrelated trees and ranks them together. The manifest records each launch's
exact process id, but the batch analyzer does not thread a per-case scope through, so
that data is captured and unused. Consuming it is what would make a command capture
exact rather than name-approximate, and it is a Core change rather than a script one.

### SC5 - Process lifecycle report

The `events` verb exposes the kernel `Process/Start`, `Process/Stop`, and image-load
events, but deriving phases and medians required a custom aggregator. The useful
per-invocation split is root start to child start, child lifetime, child stop to root
stop, and optional image milestones such as hostfxr or the target native module load.

The report emits per-invocation values plus p50, min, and max, presents process-event
wall time separately from sampled CPU, and states that inclusive CPU rows overlap.
This is the item that answers where a 50 ms command sits blocked, in the loader, in a
child, and in teardown - which sampled CPU alone cannot.

Decided: SC5 originally required raising the `tools/list` ceiling, because SC1's
exact-scope parameters took the list from approximately 8,301 to approximately 8,950
tokens and left no room for a new tool. That raise is retired. Compacting the output
schemas took the list to approximately 6,050 tokens against a 7,000-token gate in
[Test-McpServer.ps1](../tools/Test-McpServer.ps1), so a lifecycle tool the size of the
largest existing definition (`trace_diff`, approximately 830 tokens) fits without
touching the gate.

SC5 is still bound by the backlog rule that no capability adds a standalone MCP tool
before VN3, and VN3 must evaluate folding lifecycle into the report family the same way
it evaluates every other tool. The VN3 targets of 7,500 and 5,000 are unchanged; 7,500
is already met.

What shipped: a `lifecycle` verb and a `trace_lifecycle` tool over
[LifecycleProvider](../src/Filtrace.Core/Tracing/Providers/LifecycleProvider.cs). The
selector chooses invocation *roots* rather than filtering samples - each matched process
instance is one invocation, keyed on TraceEvent's `ProcessIndex` so a capture matrix that
reuses process ids keeps its invocations apart. Descendants are always followed, because
the phases are defined against them, so `--children` and `--all-processes` do not apply
and the report is documented under its own scope-inventory entry.

Three decisions are worth recording:

- The three child phases are measured against the *span* of every descendant - earliest
  child start to latest child stop - rather than against a single child. With one child
  that is exactly the split the plan called for, with several it still partitions the
  root's lifetime, and the partition is asserted by test.
- `lastChildStopToRootStop` is signed. The committed ETW fixture has a console host that
  outlives its parent by 0.7 ms, so clamping at zero would have hidden a real shape.
- An invocation whose start or stop the capture did not observe is listed and marked but
  excluded from every median, because TraceEvent clips an unobserved edge to the capture
  window and a clipped lifetime is a lower bound, not a measurement. The stop signal is
  the recorded exit status rather than a timestamp comparison, since only a decoded
  `Process/Stop` sets it.

The tool cost approximately 338 tokens, taking the list from 6,050 to 6,388 against the
7,000 gate - which is what the retired ceiling had been raised to admit.

### Tool consolidation - candidates for VN3

Compacting the output schemas removed the pressure that would have forced a
restructure, so nothing here is needed for the budget. These are recorded because
consolidation may be right on its own merits, and VN3 is where the surface is decided
with the eval evidence to judge it. Every option renames or removes a `trace_*` name,
which [AGENTS.md](../AGENTS.md) holds as a frozen contract, so each needs a deliberate
breaking-change decision rather than a quiet merge.

Current measurements, post-reduction, for the tools involved:

| Tool | Tokens | Params |
|---|---:|---:|
| `trace_rank` | 588 | 14 |
| `trace_tree` | 470 | 10 |
| `trace_callers` | 446 | 10 |
| `trace_lines` | 419 | 8 |
| `trace_jit` | 206 | 2 |
| `trace_diskio` | 202 | 2 |
| `trace_gc` | 201 | 2 |
| `trace_threadpool` | 161 | 1 |

**Option A - fold the four provider reports into `trace_report(kind)`.**
`trace_gc`, `trace_jit`, `trace_diskio`, and `trace_threadpool` are near-identical in
shape: a path, a bound, and a provider-specific result. One tool with a `kind`
discriminator replaces four.
*For:* saves roughly 500 tokens; the four already read as one family; it mirrors how
`trace_rank` unifies seven metrics.
*Against:* four names disappear from the contract; a `kind` parameter hides which
providers a trace actually supports, which the separate names advertise for free; and
the per-kind result shapes differ enough that the result type becomes a union.

**Option B - fold `trace_callers`, `trace_lines`, and `trace_tree` into a drill family.**
All three answer "having found a frame, show me more about it" and share most
parameters.
*For:* saves roughly 700 tokens; the shared scope and folding parameters stop being
described three times.
*Against:* these are the most-used drill operations, and collapsing them behind a mode
makes the common path less discoverable - the opposite of what the surface is for. The
eval would need to show the mode does not cost calls.

**Option C - leave the surface alone.**
*For:* the budget no longer requires a change; the names are a frozen contract and each
one is individually discoverable; the eval currently shows the surface working.
*Against:* the list still carries eighteen definitions, and the 5,000-token stretch
target is not reachable without either this or a further schema reduction.

The 5,000 target is worth noting against these: at 6,388 the gap is about 1,390 tokens,
which Option A alone does not close and Option B roughly does. That is an argument for
evaluating B on its merits in VN3, not for adopting it now.

### SC6 - CPU sample interval

The request layer accepts any positive finite interval; the CLI applies
`[Range(1, 1000)]`, which is what rejected an attempted 0.125 ms capture before
collection. For a 30-100 ms process, 1 ms leaves few samples and is one reason
consolidation was necessary.

Do not pick a floor from documentation. Set the interval, read the effective interval
back from the session, and record requested and effective values in the collect
result and sidecar. Then either widen the CLI range to the measured floor and warn
when the OS clamps, or keep 1 ms and state the reason in help, in API validation, and
in the skill. Either outcome is acceptable; an unexplained 1 ms minimum is not.

### SC7 - Short-startup workflow and skill guidance

Add a short-process recipe to [workflow.md](workflow.md), which is the source the
shipped skill embeds from, and add the traps this investigation exposed to
[traps.md](traps.md):

- verify instrumentation did not materially change process lifetime before trusting a
  trace, by comparing captured lifetime against an uninstrumented smoke run;
- prefer a kernel-only profile when the parent is native or CLR events are not needed;
- put repeated invocations in one session rather than opening many;
- record exact process identities and analyze parent and child separately;
- combine local product-native PDBs with public host, runtime, and OS symbols;
- use inclusive CPU rankings for Native AOT ancestor attribution;
- derive wall-clock phases from process and image events, never from sampled CPU;
- treat 1 ms samples as approximate counts and do not add overlapping inclusive rows.

Every one of these is only writable after the capability it describes exists, so SC7
lands last. Run `tools/Test-Docs.ps1 -Fix` after editing a marked block.

### SC contract and test impact

- `tools/Test-CliHelp.ps1` requires each verb's help to stay within 60 lines, every
  verb to appear in top-level help with a README example, and the README scope
  inventory to list every verb implementing a scope option. SC1's two extra options
  across thirteen verbs fit; `rank` is the one to re-check first on any further
  option addition, since it is the largest.
- `tools/Test-McpServer.ps1` gates the tool-list token budget; see
  [Output schemas were the budget](#output-schemas-were-the-budget).
- `tools/Test-CaptureBenchmarkTrace.ps1` is the model for a `Capture-CommandTrace.ps1`
  contract test, and SC4's manifest change touches its schema assertions.
- Fixture coverage is the open item for the remaining items. SC1 was covered from the
  existing corpus; SC3 needs a kernel-only capture to compare against the default one.
  SC2 is covered by a capture taken during the CI run, and SC5 by the committed
  `etw.etl`, which carries an observed parent-and-child launch with both process edges.
  Hosted Windows runners run elevated, so a capture can be taken during the run rather
  than committed - which is the only option when the check depends on symbol identity,
  since a committed capture records no PDB identity of its own and resolves native
  modules by reading the binary back from the absolute path it recorded.

### SC sequencing

SC1 shipped in #63. SC2 and SC3 remain independent of each other and of the transport
and schema decisions; together with SC1 they remove the investigation-specific
filtrace fork. SC4 follows, since its manifest is only useful once exact scope and a
low-perturbation profile exist. SC5 followed SC4 and needed no budget change.
SC6 and SC7 close the initiative.

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
- the tool-list stays at or below the 7,000-token gate;
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
| Reclaimed schema headroom is spent on tool sprawl rather than kept | Hold the 7,000-token gate, keep the VN3 targets unchanged, and require VN3 to re-evaluate every tool - including a future lifecycle tool - for consolidation. |
| SC1 scope options inflate per-verb help past the 60-line budget | Add the options to `rank` first and measure; consolidate child control into one enum option rather than a flag pair. |

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
