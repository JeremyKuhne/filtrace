# filtrace roadmap

**Status:** Living plan. This page prioritizes unshipped work; explicitly deferred
work may retain its detail in a linked GitHub issue.

**Last verified:** 2026-08-24 after VN4 merged.

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

- **The surface is 16 canonical CLI commands, 12 hidden preview aliases, and 18
  `trace_*` MCP tools.** Top-level CLI help is 27 lines / 2,171 characters, down
  from 37 / 3,170. The MCP tool list is ~6,701 estimated tokens against a
  7,000-token CI gate.
- **The permanent schema is dominated by input schemas, not output schemas.**
  Advertising the envelope alone instead of every expanded result type reclaimed
  roughly 3,000 tokens. Measured across the current 18 tools: input schemas 4,061
  (61%), output schemas 1,116 (17%), descriptions 886 (13%), names and JSON
  structure the rest. Input schemas are the largest lever; prose is the smallest.
  Regenerate the breakdown with `tools/Test-McpServer.ps1`, which writes
  `artifacts/mcp-schema-tokens.json`.
- **Ordinary responses are already small.** Ranking, caller, process, and tree
  answers land around 105-199 tokens; GC, timeline, thread-time, source-quality,
  batch, and diff around 292-886. The JIT report (~2,251) and a raw allocation-event
  page (~5,538) are the outliers. The 25,000-token ceiling is not the problem;
  returning detail nobody asked for is - and the same two questions answered with a
  narrower request cost 172 and 79 tokens.
- **The remaining measured duplication is per call, not per list.** A live MCP call
  carries the same payload in both `content[0].text` and `structuredContent`, inside
  a wrapper roughly 4-5x the payload: one measured `trace_query_events` response was
  text 36, structured 36, complete wire result 171. How much of each copy reaches
  model context is client-dependent and still has to be measured per host.
- **The deterministic eval suite answers all 27 fixture-backed tasks**, most in one
  call. The surface works; the open question is efficiency, not correctness.

## Priorities

| When | Items | Why now |
|---|---|---|
| Done | VN0-VN4, VC2, SC8, SC13 | The output contract and CLI/MCP surfaces are selected; point-in-time snapshots, capture acceptance, ancestry coverage, and decisive-query replay are implemented. |
| Now | VC3, LT1 | Add temporal shape to CPU rankings, and replace the overgrown local checkout activation draft with a narrow repository-only design. |
| Later | VC4-VC8, SC9-SC12, LP-1..LP-5, VN5 | Complete broadly applicable, demand-, dependency-, or stabilization-gated work before the specialized backlog. |
| Upstream | TE-P1..TE-P5 | Not actionable in this repository alone. |
| Backlog | VC1 ([issue #92](https://github.com/JeremyKuhne/filtrace/issues/92)) | DATAS applies only to modern server-GC workloads; retain the design without scheduling it ahead of broader capabilities. |

VN3 retained the current MCP surface. New capabilities extend a compatible existing
operation unless a measured task demonstrates that a standalone tool is better.

---

## Track A - surface, transport, and output contract

The v.next line. It is also the explicit breaking-change decision
[AGENTS.md](../AGENTS.md) requires before any existing `trace_*` name may be
renamed or removed. Until a surface is selected and versioned, the current names
stay frozen.

### VN0 - baseline and instrumentation

**Status:** Complete. The instrumentation shipped, and the baseline it enables has
been run.

**Done:**

- [Invoke-AgentEval.ps1](../eval/Invoke-AgentEval.ps1) records text, structured, and
  complete-wire response tokens separately per call and per task, plus the host's own
  reported usage. The client-visible value is not inferred - Copilot's transcript
  reports session usage rather than per-call context, so that is recorded verbatim
  instead of a fabricated number.
- [Test-McpServer.ps1](../tools/Test-McpServer.ps1) writes a per-tool schema-token
  breakdown (input, output, description, total, parameter count) to
  `artifacts/mcp-schema-tokens.json` on every run.
- `-McpDll` points the mcp arm at an explicitly built server, so a variant published
  under `artifacts/` runs against committed tasks unmodified.
- `expectOperations`, `forbidOperations`, and `maxCalls` in
  [mcp-qa.jsonl](../eval/mcp-qa.jsonl) grade surface-neutral intent through
  [Get-OperationName.ps1](../eval/Get-OperationName.ps1), so a baseline and a
  consolidated candidate can be compared. `expectTools` still grades exact names
  while the current surface is the baseline. The deterministic gate validates the
  vocabulary, so a typo fails CI instead of never matching.
- Six comprehension tasks landed (23 total): allocation-metric choice, speedscope
  source limits, capture-status honesty, count-without-paging, JIT summary before
  detail, and scope-preserving drill.
- A labeled run with fewer than three iterations per task now warns.

**The baseline, run 2026-08-02:**

Two models, 23 tasks, three iterations each - 138 sessions, 28 minutes, 22.8 premium
requests. Both models answered **86%** of iterations correctly (`gpt-5.6-sol` 59/69
at zero premium cost, `claude-haiku-4.5` 59/69 at 22.8).

Choose models by *tier*, not by name: the available set changes, and ids are the
picker label lowercased and hyphenated. Cost per session varies enormously by model
and is roughly constant across task shapes, so calibrate before a long run. Measured
the same day, premium requests per session: `claude-opus-5` 15, `gemini-3.6-flash`
14, `claude-haiku-4.5` 0.33, `gpt-5.6-sol` 0. At 69 sessions per model a frontier
pairing costs roughly 1,000 premium requests against roughly 20 for a cheap one, so
prefer one zero-multiplier and one cheap model for routine runs - that cheaper models
still succeed is the contract's own thesis - and spend a frontier model on a reduced
subset when the question is specifically about frontier behavior.

```pwsh
# Calibrate the current ids and their cost, then re-run the baseline.
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models <candidates> -Tasks event-count-only,cpu-hotspot -N 1
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models gpt-5.6-sol,claude-haiku-4.5 -N 3 -Label baseline
```

**What the baseline showed:**

- **VN1's number, corrected.** The payload ships twice - the text block is
  byte-identical to the serialized `structuredContent` at every size measured - so
  the MCP result costs **2.02x** the payload
  ([tools/Measure-McpResultSplit.ps1](../tools/Measure-McpResultSplit.ps1), six calls
  from 127 to 23,713 tokens, ratio 2.01-2.12).

  The baseline first reported this as **4.11**, read from the Copilot CLI's JSONL
  transcript. That figure was measuring the host, not the wire: its
  `tool.execution_complete` result carries four copies of the payload - `content`,
  `structuredContent`, and the host's own `detailedContent` and `contents` - and the
  harness had been serializing the whole object. The two host copies never crossed
  the MCP boundary and no transport variant can remove them, so the duplication a
  transport change can actually recover is half of a result, not three quarters.
  The harness now reports the protocol result as `wireTokens` and the host's object
  separately as `hostResultTokens`; do not read a transport decision off the latter.
- Input schemas are 61% of the permanent list against 17% for output schemas and 13%
  for prose. Consolidating parameters is the remaining lever; tightening
  descriptions is not.
- **It found a product defect.** `OutputBudget` had no callers anywhere in `src/`, so
  the 25,000-token ceiling had never been applied; an agent asking for 8,000 event
  records produced a 550,215-token response. Fixed by bounding the page as it is
  built.
- The strict tool-grounding check earns its keep: on the first run of the
  count-without-paging task the agent answered correctly from `trace_info`'s
  `analyses.alloc.eventCount` without ever querying events. The task was retargeted
  at a payload-filtered count only the events operation can answer - a reminder that
  an answer-substring match alone would have scored a pass.
- **`-AgentHost copilot` with no `-Model` silently did nothing.** The model list was
  built from a statement value, and PowerShell unrolls a single-null array to
  `$null`, which iterates zero times - while `@($modelList).Count` still reports 1,
  so the emptiness guard could not catch it. Every default-model Copilot run exited
  0 having measured nothing. Fixed; it is the reason a baseline must be read from
  its artifact rather than from an exit code.
- **A call budget cannot see a restraint failure.** On the count-without-paging task
  one model asked for ten thousand event records in a single call, answered
  correctly from the total, and scored 100% within its two-call budget. Restraint is
  a response-size property, so the task schema gained `maxResponseTokens`, graded
  against the largest single payload copy. That check is also how the acceptance
  gate below becomes enforceable rather than aspirational.
- **The recorded text figure is small on purpose, and VN1 settled why.** Large
  responses recorded text 261 against structured 10,057. That is not a transcript
  artifact: Copilot CLI spills an oversized tool result to a temp file and gives the
  model a pointer plus a head, suggesting `rg`, `head`, and `jq` for the rest.
  Measured directly, a 23,298-token `trace_query_events` result reached the model as
  about 230 tokens. So the harness's headline token figure is the right one - it is
  what the agent actually consumed - while `wireTokens` carries the protocol cost.
- **Three tasks were measuring their own phrasing, not the surface.** Both models
  wrote "4,309" where a task expected "4309", and a prompt that did not name the
  process scope sent both models to a different process than the expectations
  encoded. Correcting them took two tasks from 0% to 100% and one from 0% to 67%,
  and lifted both models from 80% and 77% overall to 86%.
- **What still fails is signal.** Five of six iterations reached for source-line
  tools on a speedscope profile, across both models and both runs - a surface
  problem, since `trace_info` had already told them the format supports `cpu` only.
  Resolved by rewording, and the measurement is below. The JIT summary task fails
  because the tool's *default* detail costs 2,251 tokens against the 172 the question
  needs, which is the detail-profile case stated as a measurement.

**Exit:** repeatable baseline artifacts that separate permanent schema cost, wire
response cost, and model-visible cost.

#### The tuning loop, exercised on the speedscope finding

The first use of `baseline -> candidate -> Compare-EvalRuns` against a real change
rather than a synthetic regression, and it settled the finding above.

Both models called `trace_info` first, read `availableAnalyses: ["cpu"]`, and reached
for `trace_lines` anyway. The cause was wording: tools that reject a speedscope input
say "rejected", but `trace_lines` and `trace_heatmap` said "speedscope is empty",
which reads as a blank result worth confirming rather than a reason not to call. The
candidate says the tool can only ever return nothing on that format and points at
`availableAnalyses`.

| Model | Baseline | Candidate | Median calls | Median tokens |
|---|---:|---:|---|---|
| `gpt-5.6-sol` | 10% | 90% | 2 -> 1 | 165 -> 127 |
| `claude-haiku-4.5` | 10% | 80% | 3 -> 1 | 209 -> 127 |

N=10 per model; 2 of 20 passing became 17 of 20. It costs 65 permanent schema tokens
(descriptions 799 -> 858), paid on every request, and is worth it here because the
failure was not a token overrun but a wrong conclusion: an empty result on a format
that carries no source data reads as "no line is hot".

**Run it at N=10, not N=3.** The same comparison at N=3 reported 33% -> 67% and
0% -> 33% and put the `gpt-5.6-sol` baseline at 33% where ten iterations put it at
10%. The direction happened to be right, but the effect size was wrong in both
directions, and a smaller effect would have been indistinguishable from noise.

#### Comprehension tasks

Six of the ten shipped. Four cannot be expressed against today's surface and are
recorded in [eval/README.md](../eval/README.md#coverage-boundary) rather than
faked - the `root` plus `benchmark` conflict is an error path the deterministic gate
cannot hold, frame ambiguity has no diagnostic to assert, `classify` has no fixture
whose CPU frames resolve, and a batch case reference is a VN2 feature. Three of the
four become expressible as VN2 and SC9 land, which is the honest sequencing.

### VN1 - transport selection - closed, variant A retained

**Decided on measurement, 2026-08-02.** Keep variant A (typed `structuredContent`
plus the SDK's text mirror). Variants B and C are not worth building, and the
three-variant `FiltraceMcpTransport` harness was never built - the probe answered the
question first.

**The mechanism works.** A tool may declare a `CallToolResult` return type for full
control of the response. Prototyped on `trace_info`, that cut the MCP result from 269
to 145 estimated tokens - the 2.02x duplication collapsing to about 1.14x, exactly as
predicted.

**The client undoes it.** Copilot CLI concatenates its own serialization of
`structuredContent` onto whatever text block the tool supplies. Under variant B its
recorded `content` was `"See structuredContent.\n\n{...full envelope...}"` - 132
tokens against 127 for variant A. The model sees the same payload plus the pointer,
so variant B is about five tokens per call *worse*. An A/B over all 23 tasks at N=5 on
`gpt-5.6-sol` agreed: 87% -> 88% overall, flat, with every `trace_info`-using task up
about five tokens.

**And model-visible cost is already bounded by the client.** A `trace_query_events`
response measuring 23,298 tokens at the server reached the model as roughly 230 -
Copilot CLI spills a large result to a temp file and hands the model a pointer plus a
head, suggesting `rg`, `head`, and `jq` to read the rest. Transport duplication is
real on the wire and largely invisible in context.

Variant C rests on the same premise as B - that removing the server's text copy
reduces what the model consumes - which this client falsifies directly, while
additionally giving up advertised typing.

**What it would have cost, recorded so nobody re-derives it:** `CallToolResult` is
not in `FiltraceJsonContext`, and `Filtrace.Core` deliberately does not reference the
MCP SDK, so the server fails *at startup* with `NotSupportedException: JsonTypeInfo
metadata for type ...CallToolResult` until
`ModelContextProtocol.McpJsonUtilities.DefaultOptions.TypeInfoResolver` is chained
behind filtrace's in `Program.cs`. All 18 tools would lose their typed return,
breaking the sync wrappers `TraceToolsTests` asserts against, and
[tools/Test-McpServer.ps1](../tools/Test-McpServer.ps1)'s round-trip check looks for
the envelope in the text block.

**The one question left open** is narrower than a transport experiment: does any
other MCP client fail to re-materialize structured content, or fail to bound a large
result? If they all behave like Copilot CLI, the transport track is moot. That is a
compatibility question to answer by reading client behaviour when a second client is
wired up, not by building variants.

### VN2 - output contract evolution

**Priority:** Now. **Gate:** each shape change is graded by the tuning loop before it
ships, not argued.

VN1 raised this item's value rather than lowering it. Transport turned out not to be
a lever - the client re-materializes structured content and already spills an
oversized result to a file - so the only way to reduce what an investigation costs is
to send fewer rows and to make a result route its own follow-up. That is this item.

The envelope is at `schemaVersion` 16. Effective context is v9; structured
diagnostics is v10; structured next steps is v11; discriminated results is v12;
null/default omission is v13; manifest case references is v14; root-scope ancestry
and coverage is v15; point-in-time timeline snapshots are v16. Each remaining slice
below changes the serialized shape and therefore gets its own schema version when it
ships; do not mutate an earlier version in place.

**Effective query context - complete.** Every result identifies the surface-neutral
operation that ran. Stack-backed results also carry normalized metric, measure, unit,
and effective scope. Process resolution is machine-readable instead of prose:
selector mode, requested exact ids, matched roots, included descendants, and the
children flag. Activity, time-window, and root-frame refinements are included only
when meaningful; null fields are omitted. Each serialized PID category is capped at
32 entries, with its full count and `processIdsTruncated: true` when shortened, so a
machine-wide capture cannot turn context into an unbounded response.

```json
{
  "operation": "rank",
  "metric": "cpu",
  "measure": "self",
  "unit": "ms",
  "scope": {
    "root": "WorkloadAction",
    "processMode": "ids",
    "requestedProcessIds": [9144],
    "rootProcessIds": [9144],
    "descendantProcessIds": [40356],
    "includeChildren": true
  }
}
```

The runtime result keeps the full typed context. The advertised MCP envelope leaves
`context` unexpanded, like `result`: expanding its nested PID arrays in all 18 tool
schemas measured 9,495 tokens and breached the 7,000 gate; the compact form measures
6,597.

**Structured diagnostics - complete in v10.** Serialized `warnings` are stable
records that keep a human message:

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
frame or root match; truncated rows or payload; clamped caller limits; applied or
ignored format-specific scope; case-local manifest failure.

The bridge is deliberately one-way: Core producers and text renderers keep their
existing string messages, while `AnalysisResult.Diagnostics` classifies them for
JSON. Standardized thin-scope and frame-resolution messages also carry numeric data;
known families get stable codes, and anything not yet classified uses the stable
generic `warning` code rather than losing the message. This lets producers migrate
to richer data incrementally without duplicating text output or breaking source
callers in the same revision.

As with context and result, the advertised MCP envelope leaves warning records
unexpanded. Runtime structured content remains typed, and tools/list measures 6,543
tokens - lower than v9's 6,597 because an object array is smaller than the old string
schema.

**Structured next steps - complete in v11.** Serialized `hints` are
operation-neutral records that retain the human reason. Source callers and CLI text
renderers keep the existing string messages through the same snapshot bridge used
by diagnostics.

Complete follow-ups carry typed arguments: frame/root, metric/measure, bounded exact
process ids and descendant mode, activity/time window, event paging/filter values,
and path where the producer knows it. Explanatory guidance has no `operation` rather
than being forced into a tool call with missing arguments. Schema v14 completes the
one deferred branch: compact batch guidance now uses a manifest case reference, so
the rank follow-up replays SC8's exact per-case scope rather than dropping it.

```json
{
  "operation": "callers",
  "reason": "drill into the hottest CPU frame",
  "arguments": { "frame": "MyApp.Inner" }
}
```

The advertised MCP envelope leaves next-step arguments unexpanded, like context,
diagnostics, and result. Runtime structured content remains typed; tools/list is
6,489 tokens, down from v10's 6,543 because object records advertise more compactly
than strings.

**Discriminated results - complete in v12.** Diff now carries `kind: "trace"` or
`kind: "manifest"`. A direct trace diff serializes its totals and `rows` but no
`cases`; a manifest diff serializes `cases` but no top-level direct totals or
`rows`. Applicable empty arrays remain present, so `rows: []` means a direct diff
ran and found no changes while `cases: []` means no manifest cases paired.

```json
{ "kind": "trace", "beforeScopeWeight": 10, "afterScopeWeight": 12, "scopeDelta": 2, "rows": [] }
```

```json
{ "kind": "manifest", "cases": [] }
```

Diff is the only current result type that represents unrelated shapes. Any future
consolidated source or report result must use the same explicit discriminator rule
instead of becoming a bag of nonapplicable optional fields. The advertised MCP
envelope still leaves `result` unexpanded; naming the two kinds in the `trace_diff`
description puts tools/list at 6,498 tokens.

**Null and default omission - complete in v13.** The source-generated serializer
omits null optional properties across every payload. `budgetTruncated: false` is
also omitted from event pages; the property remains present when truncation occurs.
Semantically meaningful zeros and empty strings remain, as do empty arrays that mean
"the query ran and found none". An array is omitted only when it does not apply -
for example, an unrequested timeline lane or `callees` on a callers-only query.

Across the 23 deterministic tasks, 12 responses shrink by 235 estimated tokens with
no answer or call-count change. Timeline drops from 919 to 822 tokens, and the three
trace-info tasks drop by 18 to 36 tokens each. Canonical responses contain no
explicit nulls or false default flags after the change. The advertised MCP result
remains opaque. Documenting the event count-only endpoint puts tools/list at 6,503
tokens.

**Detail profiles - measured; no enum adopted.** Existing cardinality controls cover
the operations where response size materially varies. Adding a second axis would
create precedence rules without adding capability.

The two outliers already have cheap answers through their cardinality options. The
JIT report costs 2,273 tokens at its default and 194 with `top: 0`. A filtered
90-match event query costs 3,810 tokens for all rows with payloads omitted, 137 for
one row, and 34 for the count-only `take: 0` result. The remaining question is
discoverability and default selection, not whether a summary capability exists.

| Operation | Proposed MCP default | Behavior |
|---|---|---|
| info | no enum | current evidence lists are bounded; a summary/rows candidate regressed compatibility selection |
| rank / callers / tree / source | current bounded rows | `top` and depth remain the natural control |
| GC / JIT / disk / lifecycle | no enum | `top: 0` is summary; positive `top` returns rows |
| thread pool | `summary` | already small |
| events | no enum | `take: 0` is count-only; positive `take` returns paged rows |
| timeline | current bounded buckets | lanes and bucket count remain the control |
| diff / batch | current structural caps | already compact agent summaries |

The CLI and MCP use the same profiles: zero rows means summary/count-only where a
cardinality axis exists, and positive values return bounded rows. `trace_info` keeps
its current bounded evidence because the default-summary candidate regressed agent
behavior.

Grade each default with `baseline -> candidate -> Compare-EvalRuns` at **N=10 per
model**, the way the speedscope wording change was graded. `gpt-5.6-sol` costs no
premium requests, so a full-suite arm is wall time only; add `claude-haiku-4.5` as
the overfitting detector when a candidate looks worth keeping. N=3 is not enough to
size an effect - it put one baseline at 33% where ten iterations put it at 10%.

##### Probed 2026-08-03: align event `take: 0` count-only - kept

The provider and MCP tool already accepted `take: 0`, but the CLI parser required
at least one row. That asymmetry is removed: both heads now return the same compact
count envelope, and neither emits a paging next step whose `take: 0` could never
advance. Negative values remain errors.

For the payload-filtered count task, v13 null/default omission first reduced the
response from 153 to 147 tokens; this count-only endpoint then reduces 147 to 34.
The exact CLI and MCP structured envelopes are both 131 characters / 34 estimated
tokens. Naming the endpoint in the MCP parameter description costs five permanent
schema tokens. This removes events from the detail-enum experiment: its existing
`take` axis already expresses count-only and rows without a precedence rule.

##### Probed 2026-08-03: `trace_info` summary/rows - rejected

Candidate: add `detail: summary|rows` to MCP `trace_info`, default to summary, and
omit source/PDB/native evidence unless rows is requested. `full` was excluded because
the source tracker already bounds retained evidence (5 methods, 8 modules, 16 matching
PDB modules), so it has no distinct implementable meaning. The candidate cost 44
permanent schema tokens (6,503 -> 6,547).

Measured on all four info tasks at N=10 for `gpt-5.6-sol` and
`claude-haiku-4.5`. Evidence selection worked: source-quality stayed 100%/one call on
sol and rose 60% -> 90% on haiku; capture-status stayed correct while its median
response fell 809 -> 529/598 tokens. But haiku's speedscope compatibility task fell
90% -> 40%, median calls rose 1 -> 2, and median tokens rose 119 -> 176. The
repository comparator rejected the candidate.

The summary payload itself was not the problem on speedscope - it is the same 119
tokens because that format has no source/native sections. The added profile grammar
and changed tool description made the agent more likely to attempt a forbidden source
operation. The candidate is reverted. `trace_info` retains one bounded view.

##### Probed 2026-08-03: lowering the two outlier defaults - rejected

Simply shrinking the defaults, without adding a vocabulary, was measured and
rejected. Candidate: `trace_jit` `top` 25 -> 5 and `trace_query_events` `take`
100 -> 10, one variable per tool, graded on `gpt-5.6-sol` at N=10.

| Task | Baseline | Candidate | |
|---|---:|---:|---|
| `jit-report` | 100%, 2,666 tokens | 100%, **950** tokens | the prize is real |
| `jit-summary-first` | 90%, 2 calls | **40%**, 3 calls | rejected on calls |
| `event-count-only` | 90%, 255 tokens | 80%, 468 tokens | rejected |

**What actually went wrong is not what the selection rule anticipated.** The extra
call is not the agent fetching more after too small a page. It is the agent asking
for `trace_jit` with `top: 0` - "give me the aggregate and no rows" - and filtrace
rejecting it, then retrying with `top: 1`. That rejected call appeared in 1 of 10
baseline iterations and **7 of 10** candidate iterations.

So the agent already reaches for a summary level that the surface does not offer, and
a smaller default makes it reach more often. That is direct evidence for a detail
vocabulary rather than tuned defaults: the missing thing is a way to *say* summary,
not a better guess at how many rows to send. Whether the vocabulary removes the extra
call is the next probe's question, not a conclusion from this one - as is whether a
gentler reduction keeps `jit-report`'s 64% saving without the regression, since only
one candidate pair was tried.

A cheap intermediate exists and is worth measuring first: let `top: 0` mean "aggregate
only" on the report tools instead of throwing. It gives the agent exactly what it is
already asking for, needs no envelope change, and is testable against these same five
tasks.

##### Probed 2026-08-03: `top: 0` means aggregate only - kept

Measured against the same baseline, same five tasks, `gpt-5.6-sol` at N=10.

| | `trace_jit` calls | using `top: 0` | rejected |
|---|---:|---:|---:|
| Before | 21 | 1 | **1** |
| After | 20 | **6** | **0** |

`jit-summary-first` went 90% -> **100%** at 587 -> 501 tokens. `jit-report` did not
move: still 100% at 2,666 tokens, because an agent that wants the detail still asks
for it. An aggregate-only call costs 86 estimated tokens against 2,251 at the default.

**No description was added.** Agents reached for `top: 0` unprompted in both arms;
adoption rose only because it stopped failing. So this bought a summary level for
zero permanent schema tokens, which is the opposite of the trade a `detail` enum
would make.

**Why this rather than a `detail` vocabulary.** `top` already means "how many detail
rows", and zero is a coherent endpoint on that axis. A `detail: summary|rows|full`
parameter would sit *beside* `top` and create combinations with no obvious meaning -
`detail: summary` with `top: 25` needs a precedence rule, and a rule an agent has to
learn is a rule it can get wrong. One axis with a defined endpoint beats two axes
with an interaction. The enum stays on the table only for operations where cardinality
is not already expressed by a row count.

**The untested risk** is that some tools use `0` to mean *unlimited*, so an agent
could read `top: 0` as "everything" and receive nothing. Nothing in this run showed
it, and nothing in this run tested it. The CLI documents the meaning in `--help`,
which is free; adding it to the MCP parameter description costs schema tokens and
would change the string this probe measured, so it is a separate question with its own
before-and-after.

**Also worth knowing:** `Compare-EvalRuns` returned REJECT on this run, for
`event-count-only` tokens rising 255 -> 468 at *identical* 90% success - on the events
tool, which this change does not touch. That is the second time the comparison has
flagged a task the candidate cannot affect. It has no notion of blast radius, so read
its verdict alongside what the change actually reaches.

**Do not grade this family at N=5.** The same comparison at N=5 flagged
`scope-preserving-drill` as the regression - a task these defaults cannot affect - and
missed the `jit-summary-first` regression entirely. It also read that baseline as 40%
where ten iterations read 90%.

**Manifest case references - complete in v14.** `BatchRankingCaseResult` exposes the
`id` that manifest schema v1 already required. `trace_rank` accepts either a direct
`path` or the mutually exclusive `manifestPath` plus `caseId`; CLI uses
`rank <manifest> --case-id <id>`. The resolver selects the recorded trace and symbols
and then applies the same scope precedence as batch: explicit process/PID/all-processes
override, otherwise exact recorded invocation ids, otherwise the legacy manifest
process. Children, root, metric, measure, custom folds, and explicit symbols survive
in the structured next step when they fit its bounded argument vocabulary. If an
otherwise valid override is too large to repeat safely, analysis still succeeds and
the guidance remains reason-only rather than emitting an incomplete or oversized
operation.

The batch row keeps its resolved `tracePath` for audit and text display, while the
action uses the stable case address. Existing manifests require no migration. The
24th deterministic task runs `batch -> rank(case reference)` and pins both the
structured arguments and the resulting hottest frame. Existing `manifest-batch`
stays under its 15% response-growth budget at 358 tokens; the two-call drill is 463.

The live MCP workflow also passes at N=10 on both `gpt-5.6-sol` and
`claude-haiku-4.5`: 20/20 successful investigations, exactly two calls each, with a
487-token median. This closes the reliability question in the acceptance criteria;
agents consume the batch reference and use it for the follow-up without parsing the
resolved path or losing case scope.

**Exit:** results are self-describing, compact by default, and can route a follow-up
without parsing prose.

### VN3 - MCP surface experiment

**Status:** Complete. **Decision:** retain the 18 intent-bearing tools.

ModelContextProtocol 1.3 can generate an honest `anyOf` discriminator for source
and report requests, but only beneath a required `request` object. A flat
`trace_source(view, ...)` schema requires a custom `AIFunction`, hand-written schema
transformation, and parameter binding. The repository also has no committed trace
with a positive portable-PDB source oracle, so a live source-selection A/B would
grade only rejection paths. `trace_lines` and `trace_heatmap` therefore stay
separate.

The measurable candidate replaced `trace_gc`, `trace_jit`, `trace_threadpool`,
`trace_diskio`, and `trace_lifecycle` with
`trace_report(request: { kind, ... })`. Its generated five-branch union reduced the
surface from 18 tools / 26,118 characters / ~6,590 tokens to 14 / 24,077 / ~6,065:
525 tokens, or 8%, below the 20% threshold for a token-motivated breaking change.
All 27 deterministic tasks passed before the live arm.

The N=10 live A/B on `gpt-5.6-sol` and `claude-haiku-4.5` was rejected with five
regressions. On haiku, disk-I/O success fell 100% -> 90%, with median calls 1 -> 2
and response tokens 433 -> 1,069; lifecycle fell 70% -> 30%, calls 2 -> 6, and
tokens 660 -> 2,168. GPT lifecycle remained at its noisy 20% baseline and four
calls. Transcripts showed the nested grammar itself failing: one haiku run encoded
the `request` object as a JSON string and then used four raw-event queries to
recover. Lifecycle runs frequently omitted its root selector and measured the
outer 6.6-second process instead of the 619 ms benchmark job. Separate tool names
and top-level parameter descriptions preserve those constraints.

The candidate was reverted. The 7,000-token gate has enough headroom, and lower
tool count is not worth extra orientation, repair calls, or weaker scope selection.

### VN4 - CLI surface

**Status:** Complete. **Decision:** retain 16 canonical commands and 12 hidden
preview aliases for one release.

The selected surface is `info`, `rank`, `callers`, `tree`, `source`, `processes`,
`classify`, `report`, `lifecycle`, `timeline`, `diff`, `batch`, `events`, `export`,
`collect`, and `cache`. `rank` absorbs the four metric shortcuts; `source --view`
selects lines or heatmap; `report --kind` selects GC, JIT, thread-pool, or disk I/O;
and `cache --action` selects convert or clean.

`lifecycle` stays standalone. Its invocation roots always follow descendants, and
its optional image milestones and per-invocation rows do not share the other report
kinds' contract. Putting it behind `report` would recreate the conditional option
bag that weakened lifecycle scope selection in VN3.

`callers`, `tree`, `timeline`, `diff`, `events`, and `export` stay as named
commands: they communicate a human intent better than modes on `rank`, and keeping
them avoids a large `rank --view` option matrix.

**Prototype evidence, 2026-08-23:** ConsoleAppFramework 5.7.13's `[Hidden]`
attribute keeps a command callable, including its own `--help`, while omitting it
from top-level help. The 12 prior names use that mechanism and print the canonical
equivalent to stderr. Top-level help fell from 25 listed commands / 37 lines / 3,170
characters to 16 / 27 / 2,171. The framework exposes no completion command or
generator: completion-like probes route to ordinary help, so there was no completion
contract to regress. Nested paths were rejected because they still advertise every
leaf; required `--view`, `--kind`, and `--action` enums preserve one visible intent
per command instead.

The help contract now requires canonical commands to be listed and documented,
hidden aliases to remain callable but absent from the list, and top-level help not
to exceed the pre-VN4 line or character baseline. Aliases are migration-only text,
not runnable examples in README or the packaged skill.

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
| VC3 | Per-frame temporal buckets | `rank --temporal` or `detail=full` | Medium | response and aggregation cost |
| VC4 | PMC / CPU-counter ranking | new `rank` metric | Medium | ETW capture support and a fixture |
| VC5 | Retention / leak analysis | dedicated retention result | Medium | PerfView graph dependency |
| VC6 | Net surviving heap | new stack metric | Low | `GCHeapSimulator` extraction |
| VC7 | Physical ETL trim | `trim` or `cache --action trim` | Low | preserving JITted managed frames |
| VC8 | Activity and file-I/O follow-ups | extend existing scopes and reports | Low | demand and capture volume |
| VC1 | DATAS server-GC tuning | extend `report --kind gc` / `trace_gc` | Backlog | [issue #92](https://github.com/JeremyKuhne/filtrace/issues/92) |

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

Because this is a distinct data model, it may justify a dedicated tool, but only
after a measured task shows that extending an existing report would be misleading.

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

### VC1 - DATAS server-GC tuning - backlog

Tracked in [issue #92](https://github.com/JeremyKuhne/filtrace/issues/92). DATAS
explains heap-count and budget decisions only for modern server-GC workloads, so it
follows every more broadly applicable capability. Extend the existing GC report if
concrete demand later justifies scheduling it; do not add another command or MCP
tool.

---

## Track C - correctness and capture follow-ups

The remaining gaps from the short-command capture initiative
([issue #62](https://github.com/JeremyKuhne/filtrace/issues/62)). Its seven original
items shipped; SC8 completed the immediate exact-scope follow-up, while SC9-SC13
track the residual portability, composition, reproducibility, and observer-effect
work without holding the original initiative open.

### SC8 - per-case exact scope in batch and diff - complete

`collect --iterations` already recorded each launch's exact root process id in the
capture manifest. Batch and diff now replay those ids for each case instead of
falling back to a process-name match that could include unrelated trees.

`CaptureManifest.ResolveCaseScope` owns one precedence rule for both heads: an
explicit caller process or all-processes override wins; otherwise the case's
invocation ids win; a legacy case with no invocations falls back to the manifest
process name. The caller's children, activity, and time-window refinements survive a
fallback. The four loader lambdas call that resolver, and the three duplicate
`ManifestScope` helpers are gone.

The end-to-end batch and diff regressions supply a valid manifest process name and
unknown recorded pids. Both operations report those exact ids as missing; the old
name fallback would have silently analyzed the matching process instead.

### SC9 - cross-machine native symbol fixtures

**Priority:** Later. **Gate:** requires a merge/symbol-injection capture step.

A filtrace capture records no PDB identity of its own, so `TraceEvent` resolves a
native module by reading the binary back from the absolute path in the trace. A
committed capture therefore resolves native symbols only on the machine that took
it, which is why native symbol resolution is verified by capturing during the CI run
instead. Adding the PerfView-style "merge" step to
[EtwCollector](../src/Filtrace.Core/Tracing/EtwCollector.cs) would make a portable,
committable fixture possible.

### SC10 - manifest-addressed lifecycle and agent reliability

**Priority:** Later. **Gate:** one exact-scope manifest call must beat manual PID or
name selection without a success, call-count, or response-budget regression.

A command manifest records every invocation root, and batch, diff, and rank replay
those ids exactly. `lifecycle` still accepts only a trace path plus a process name or
PID list, so a caller starting from the manifest must extract the ids itself or fall
back to a name selector. That leaves the wall-clock half of the short-command workflow
less composable than its CPU half, and the live lifecycle task remains noisy even
though the deterministic provider result is exact.

- Add a manifest case address to CLI `lifecycle` and `trace_lifecycle`, reusing the
  existing mutually exclusive direct-path versus `manifestPath` + `caseId` grammar.
- Accept command cases with recorded invocations and use those ids as invocation
  roots; reject a case that has no lifecycle-capable ETW trace with a specific
  diagnostic rather than silently broadening scope.
- Emit a structured lifecycle next step from a command-manifest result where it can
  carry the case reference without parsing paths or PID prose.
- Add a deterministic manifest-to-lifecycle task and an N=10 multi-model task that
  grades the selected operation, exact case scope, phase values, and call count.

Extend the existing lifecycle verb and MCP tool. VN3 showed that hiding lifecycle
inside a report union weakens selector use, so this does not reopen consolidation.

### SC11 - command-capture reproducibility and contract

**Priority:** Later. **Gate:** a side-effect-free contract test must exercise the
matrix, elevation handoff, partial-manifest, and multi-executable paths.

`Capture-CommandTrace.ps1` made the successful investigation repeatable, but its
orchestration contract is not tested as deeply as `Capture-BenchmarkTrace.ps1`, and
its manifest is intentionally smaller than the issue's full provenance wish list.

- Add a dedicated command-capture contract script using a fake filtrace executable,
  including one failed scenario, quoted arguments, bounded elevated wait/log
  propagation, and exact invocation records.
- Remove the stale multi-executable warning that says per-case invocation ids are not
  consumed; SC8 now consumes them for batch and diff. Pin a mixed-executable manifest
  to exact per-case ids.
- Record structured executable and arguments, working-directory identity, filtrace
  version, and an explicit allowlisted environment fingerprint. Never serialize the
  full environment, which can contain credentials.
- Keep descendants authoritative in the ETW process graph rather than duplicating a
  child-id snapshot in the manifest; document that boundary and test its lifecycle
  reconstruction.

### SC12 - kernel-only profile and observer-effect measurement

**Priority:** Later. **Gate:** add a profile only if repeated measurement shows a
material lifetime reduction over `startup` for a supported short-command scenario.

The shipped `startup` profile removes unused machine-wide kernel traffic and most CLR
events, but deliberately retains CLR method-naming keywords. It is low perturbation,
not the literal kernel-only option issue #62 proposed. The skill correctly requires
an uninstrumented baseline, but no automated check measures whether `startup` still
changes the target materially.

- Measure uninstrumented, `startup`, and true kernel-only runs over the same
  AotStartup command matrix, using medians across repeated invocations rather than a
  one-shot lifetime.
- If kernel-only is materially better, add a profile containing only Process, Thread,
  ImageLoad, and sampled Profile events, and state explicitly that managed method
  naming is unavailable.
- Record enough baseline and captured-lifetime evidence for the command-capture script
  to flag a large observer-effect ratio without claiming a universal threshold.
- Keep the existing elevated keyword-presence tests and add an end-to-end lifetime
  experiment; keyword absence alone does not prove low perturbation.

### SC13 - accepted-trace and analysis-record workflow

**Status:** Complete. No CLI verb or MCP tool was added. The four phases share
fixture-backed contracts: incompatible recorder profiles fail before project build,
`info` can reject unusable evidence while retaining its envelope, root-aware results
identify stack-ancestry coverage, and a bundled script records and replays decisive
read-only queries against hashed input bytes.

This proposal comes from the Orchard Core evaluation-globbing investigation, which
used filtrace at commit `0d121156fd9eb66506c81601ed458716587542ca`. The supplied
post-mortem separates its observed session record from recommendations; the product
assessment below applies the same distinction. Claims about what the investigation
ran remain claims of that record. Claims about the current product were checked
against this repository on 2026-08-23.

#### What the assertions establish

| Post-mortem assertion | Baseline evidence at `0d12115` | Implemented disposition |
|---|---|---|
| `info` should enforce a machine-checkable quality policy | `info` reported the 0.8 frame-name threshold, capture status, and event counts, but always exited 0 after a successful load. | `--strict`, `--require-enabled`, and `--require-events` retain distinct diagnostics and return quality-gate exit 3 after rendering the full result. |
| Root scoping may omit parallel sibling work | [RootScope](../src/Filtrace.Core/Tracing/RootScope.cs) and [FoldingAggregator](../src/Filtrace.Core/Tracing/FoldingAggregator.cs) kept only samples whose stack contained the selected frame. | Schema v15 identifies `stackAncestry` and reports exact pre-root/retained coverage without calling omitted stacks causal. |
| The accepted analysis should be replayable | Capture manifests retained generated suggestions and capture identity, but not the commands actually run, outputs, exits, or trace hashes. | `Invoke-FiltraceAnalysis.ps1` writes a separate bounded analysis record with exact argv and input/output hashes; replay checks plan and trace bytes first. |
| Recorder/profile compatibility should fail before a long workload | [Capture-ProjectTrace.ps1](../.agents/skills/filtrace/scripts/Capture-ProjectTrace.ps1) hard-coded `cpu-sampling`, which `dotnet-trace` 9.0.661903 rejects for `collect`. | The helper preflights advertised profiles before build/launch, selects the proven current pair or an advertised legacy mapping, and records the effective recorder contract. |
| Activity/time boundaries, ETW wall clock, and matched comparison are needed | Activity and time scopes, ETW `threadtime`, capture manifests, manifest pairing, and `diff` already exist. The Orchard evidence did not retain or use all of them. | Improve routing and preservation; do not propose duplicate analysis capabilities. |

The post-mortem also reports a failed `dotnet-trace` profile name, a nested process
that inherited startup diagnostics, and missing retained ETW/source-line evidence.
Only the profile failure is currently actionable as a filtrace defect. A preflight
can validate recorder syntax and known profiles; it cannot prove that an arbitrary
application EventSource will emit events, nor can filtrace repair a workload's child
process environment before that workload is described to the helper.

#### Phase 0 - recorder compatibility before launch - complete

Recorder selection is capability-based and runs before the project build or
benchmark workload starts:

1. Resolve `dotnet-trace`, record its version, and query `list-profiles` once. Select
   only a profile set whose exact names are advertised for `collect`.
2. Prefer the repository-proven `dotnet-common,dotnet-sampled-thread-time` pair for
   CPU capture. Permit a legacy mapping only when that installed recorder advertises
   it; do not guess a profile from version numbers.
3. Keep `gc-verbose` for allocation only when advertised. If no known semantic
   mapping exists, fail with the discovered recorder version and available profiles
   before launching the target.
4. Record the effective profile names, explicit providers, and recorder version in
  the capture sidecar or manifest. The separate analysis record pins the Filtrace
  version that interpreted those bytes. Recorder defaults must not be reconstructed
  later from whichever version happens to be installed.
5. Synchronize the EventPipe recipes in [workflow.md](workflow.md), the shipped
   skill, fixture scripts, and product comments from the same tested mapping.

`Capture-BenchmarkTrace.ps1` uses BenchmarkDotNet's profiler rather than
`dotnet-trace` profile aliases, so this preflight must not be applied to it by name
alone. Its existing Filtrace version/schema probe remains useful and separate.

**Acceptance:** a fake-recorder contract covers a current profile set, a supported
legacy set, missing `list-profiles`, and no compatible CPU profile. Every rejected
case proves that the target command was never launched. One real smoke test records
a short CPU trace with the selected current profile and requires `info` to report
CPU enabled with at least one record.

#### Phase 1 - explicit trace acceptance - complete

Extend `info` rather than teaching each capture script to parse an evolving JSON
shape. Implemented CLI surface:

```pwsh
filtrace info app.nettrace --strict --require-enabled cpu --require-events cpu --format json
```

- `--strict` keeps its existing meaning: exit 3 when frame-name resolution is below
  `SymbolGate.MinimumResolutionRate` and the trace has CPU samples.
- `--require-enabled <names>` requires each comma-separated analysis to be
  format-supported with `captureStatus: enabled`; enabled with zero events passes.
- `--require-events <names>` additionally requires a known positive `eventCount`;
  disabled, unknown, unsupported, and enabled-zero each fail with distinct stable
  diagnostics. It implies `--require-enabled` for those names.
- Unknown analysis names are usage errors. A policy failure still renders the full
  normal `info` result, then returns exit 3. Rename the internal exit-code meaning
  from a symbol-only strict gate to a quality gate without changing its numeric
  value.

This remains opt-in. The aggregate frame-name rate includes unresolved native ETW
frames, so an unconditional 0.8 rejection would discard usable managed evidence.
Capture helpers may apply a profile-specific policy after recording, but must write
the rejected artifact and its reasons rather than deleting it. In particular,
enabled-zero remains valid when the policy asks only whether a provider was enabled.

**Acceptance:** CLI tests pin success, usage error, and exit 3 independently; an
enabled-zero fixture passes `--require-enabled` and fails `--require-events`; unknown
and disabled states retain their distinct diagnostic codes; and JSON output is still
the same bounded `info` envelope when no policy options are supplied.

#### Phase 2 - quantify ancestry-only root scope - complete

Make the effective scope say `rootKind: stackAncestry` whenever `--root` or
`--benchmark` is applied. Report coverage against the already-applied process,
activity, and time scope, before the root filter:

- available and retained metric weight;
- available and retained record count when the source has meaningful records;
- retained percentage, with zero handled explicitly.

Attach an informational `root_scope_ancestry` diagnostic and a reason-only next step:
the root retained stacks containing the frame and may omit sibling workers; use an
instrumented activity or a validated time window to cover a parallel phase, and use
ETW `threadtime` when elapsed time remains unexplained. This is scope semantics, not
a warning that the result is wrong. Filtrace must not infer that omitted samples are
causally related to the selected root.

Build the coverage calculation once in Core and reuse it across root-aware results;
do not let ranking, callers, tree, classify, diff, batch, and export define different
denominators. The output-contract change gets the next schema version and remains
absent when no root is applied.

**Acceptance:** a synthetic parallel source has one worker stack below the selected
root and one simultaneous sibling stack without it. The result retains only the
first, reports the exact pre-root/post-root counts and weights, and names ancestry
semantics. A mutation that counts the sibling as rooted must fail. Text and JSON
must not say that the sibling is part of the operation.

#### Phase 3 - retain decisive analyses as an analysis record - complete

A bundled `Invoke-FiltraceAnalysis.ps1` script proves the script-first contract. Its
input is a versioned plan containing structured argument arrays, not shell command
strings. Its output directory contains:

- the unchanged plan;
- an inventory of each input path, byte length, SHA-256, capture-manifest/case
  identity when present, and verified symbols directory;
- Filtrace version and an allowlisted runtime/OS fingerprint, never the full
  environment;
- one bounded JSON stdout file and stderr file per query;
- a record of the exact argument array, start/end time, exit code, and output hashes
  for each query.

Version 1 accepts read-only, JSON-producing analyses only and validates every query
before running any of them. It does not capture an interactive shell transcript,
copy large traces, run `collect`/cache mutation/export, or invent a canonical query
set. The analyst retains the small decisive set - for example accepted `info`, scoped
CPU, the caller query that established attribution, and scoped allocation - rather
than all exploratory dead ends. Replay verifies every input hash before execution
and writes a new result directory; it preserves both runs but makes no automatic
equivalence claim across Filtrace schema versions.

Keep this separate from `manifest.json`: a capture manifest says what was recorded
and is input to multiple investigations, while an analysis record says what questions
one investigation asked. Existing generated `commands` remain suggestions, not
evidence that those commands ran.

**Acceptance:** a fixture-backed plan runs `info`, a root-scoped CPU ranking,
`callers`, and allocation analysis; paths with spaces remain single arguments; a
quality-gate exit is retained as a rejected result; trace-byte mutation prevents
replay before any query runs; and a second run records its own Filtrace version and
outputs without overwriting the first. Promote this to a CLI operation only after at
least two real investigations use the script and show that a verb would remove
material orchestration or quoting risk. Do not add an MCP tool: recording files is an
explicit local side effect.

#### Exit

The accepted trace can be distinguished from a merely readable trace, every root
result states the boundary it actually applied, and the portable handoff contains
enough identity and exact query evidence to rerun the conclusions. This does not
retroactively make the Orchard captures manifest-backed, recover missing ETW or
source data, or turn a serial diagnostic control into production-throughput evidence.

### Output-budget coverage for row-capped producers - complete

A producer whose row count comes from the caller cannot bound its response with a row
cap alone. Every producer that returns a variable-length list now bounds it against
`OutputBudget.DefaultRowBudgetTokens` and reports what it dropped: `events`,
`jitstats`, `rank`, `diskio`, `callers`, `lines`, `heatmap`, `gcstats`, and
`lifecycle`. `timeline` (buckets clamped 5-200), `tree`
([FoldingAggregator](../src/Filtrace.Core/Tracing/FoldingAggregator.cs)'s maximum
tree depth), and `threadpool` were already bounded by construction.

Two of those were reproduced breaches rather than precautions. `events --take 8000`
returned 550,215 tokens, and asking the committed 840-method JIT fixture for every
method measured 78,993 - three times the ceiling, where a startup trace jits far
more. The rest are bounded on measured per-row cost (`rank` ~27 tokens, crossing at
~940 rows; `diskio` ~60 per file, ~412 files) and pinned against constructed results,
because no committed fixture is broad enough to reach those counts.

`heatmap` was the odd one: it takes no row cap at all, so its size follows the source
file and the budget is the only bound it has.

The shared mechanism is `OutputBudget.TakeWithinBudget`, which always keeps the first
row. That places an obligation on each producer - whatever scales a single row's size
must itself be bounded - and only `EventQueryProvider.MaxPayloadChars` discharges it
today. The other producers' row sizes come from the trace (symbol names, file names,
child process lists), not from caller input.

**Do not move a bound into `FoldingAggregator.RankRows`.** The diff paths rank with
`int.MaxValue` deliberately and pair the full row sets before capping their own
output, so bounding there truncates diff *inputs* - a wrong-answer bug, not a size
fix. Bound where a result becomes a response.

### Skill packaging headroom

The shipped `SKILL.md` is roughly 270 lines of embedded catalog against 230 lines of
its own guidance, and further trimming has already spent the redundancy that was
available. The next catalog addition needs the validator's own remedy - move a
catalog into a sibling reference file - which is a packaging change, because the MCP
nupkg packs only `SKILL.md`.

---

## Track D - performance and parallelism

**Status:** Phase 0 in progress; no optimization shipped. Aggregation, activity-read,
embedded-PDB, and warm/cold single/manifest CLI benchmarks are implemented, together
with sequential degree seams, a CPU/activity workload, corpus archiver, and
per-launch child-process telemetry. **Date of analysis:** 2026-07-28. The remaining
durable corpus restore, exact no-op reconstruction, Layer C wiring, sequencing, and
keep/reject plan is in
[parallelism-opportunities.md](parallelism-opportunities.md).

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
| Native symbol resolution | symbol-server I/O | blocked pending TraceEvent thread-safety contract (LP-5 / TE-P3) |

Aggregation cost is worth a number: with the default 7 fold patterns and a 20-frame
average depth, one aggregation over 10,000 samples calls `Regex.IsMatch` on the
order of 200,000-400,000 times.

### LP-1 - parallel case loading in batch and diff

**Value:** high. **Effort:** low.

`CaptureManifestBatchAnalyzer.Analyze` and `CaptureManifestDiffAnalyzer.Analyze`
iterate their case lists sequentially. Their public load delegates may capture state,
and the CLI strict-symbol gate currently does, so existing overloads must remain
sequential. Add an explicitly concurrent overload with bounded degree, make each head
callback thread-safe, and write results by case/pair index into preallocated slots so
manifest order stays deterministic.

Notes: the per-iteration warning list is already allocated per case; `TraceStore.Get`
may run its factory twice when two cases share a trace path (the documented LruCache
race), which is a tolerable transient double-load here, not a correctness bug. The
detailed callback, strict-gate, memory, and degree tests are in the measurement plan.

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

## Track G - repository engineering

### LT1 - repository-scoped local checkout activation redesign

**Status:** Phase 1 merged in PR #98, Phase 2 baseline capture and bounded overlay
input merged in PR #99, and the fixed per-worktree lock merged in PR #100.
Prepared CLI package validation and fresh private installation are in progress;
the implementation plan and current validation status are in
[local-testing-redesign.md](local-testing-redesign.md).

Replace PR #94's review-era implementation with one fixed-path, one-schema
workflow rooted in the consumer repository's Git directory. Keep the useful
failure corpus, but remove arbitrary managed paths, global CLI mutation, implicit
schema migration, and the machine-wide ownership registry from V1.

**Gate:** review and validate the isolated CLI increment on Linux ARM64 before
beginning structured MCP and skill mutation.

---

## Acceptance gates for a v.next candidate

The enforced gates and efficacy measures live in
[design.md](design.md#measures-of-success). A v.next candidate additionally
requires:

- deterministic tests and parity remain exact;
- summary-mode JIT and raw-event count tasks fall below 500 response tokens - already
  met through existing options at 172 and 79, so the current schema must preserve it rather than
  reach it, and the two tasks now carry a `maxResponseTokens` of 500 so a live run
  enforces it;
- duplicate payload copies are eliminated wherever the chosen clients permit it;
- tool-list target: at most 7,500 tokens if typed output schemas are retained, at
  most 5,000 if JSON-text-only wins;
- the 20% total-token reduction applies to a token-motivated breaking
  consolidation - not to every semantic output-contract improvement. VN0 records the repeatable
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
| Reclaimed schema headroom is spent on tool sprawl | hold the 7,000-token gate and require a measured task before adding a standalone tool |
| Parallelism regresses small traces | gate LP-2 on a sample-count threshold and measure the fast path |

## Open decisions

Resolve these with VN0 and VN1 evidence rather than opinion:

1. ~~Does JSON-text-only preserve agent composition well enough to remove advertised
   output schemas?~~ **Moot.** VN1 showed the client re-materializes structured
   content into the model's view, so removing the server's text copy saves nothing
   there; the remaining ~1,020 tokens are a tool-list question, not a transport one.
2. ~~Can the MCP SDK express useful discriminated `trace_source` and `trace_report`
  schemas without a large optional-parameter bag?~~ **Resolved.** It generates an
  honest union only under a nested request object; the report A/B rejected that
  grammar, and a flat union requires custom binding.
3. Should CLI report defaults stay detailed while MCP defaults to summary?
4. Does a manifest case reference improve follow-up reliability enough to justify a
   new addressing form?
5. Can global CLI format and detail options be implemented without making per-command
   help less clear?
6. ~~Is one preview release of hidden aliases useful, or is a clean pre-1.0 break
  less confusing?~~ **Resolved.** ConsoleAppFramework hides aliases without breaking
  direct routing or help; retain them for one preview and remove them in VN5.
7. ~~Where does `lifecycle` belong in a consolidated surface?~~ **Resolved.** Keep
  `trace_lifecycle` separate; hiding its root selectors caused wrong-scope calls.

## Immediate next step

VC3, per-frame temporal buckets. Prototype CPU periodic samples behind an explicit
option, cap both rows and bucket count, and retain it only if the bounded histogram
saves a follow-up `timeline` plus `rank --time` call without slowing or inflating an
ordinary ranking.
