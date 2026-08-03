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
- **The permanent schema is dominated by input schemas, not output schemas.**
  Advertising the envelope alone instead of every expanded result type reclaimed
  roughly 3,000 tokens. Measured across the current 18 tools: input schemas 3,883
  (61%), output schemas 1,080 (17%), descriptions 799 (13%), names and JSON
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
- **The deterministic eval suite answers all 23 fixture-backed tasks**, most in one
  call. The surface works; the open question is efficiency, not correctness.

## Priorities

| When | Items | Why now |
|---|---|---|
| Done | VN0, VN1 | The baseline exists, and the transport question is answered; see below for what each measured. |
| Now | VN2, SC8 | The output-contract decision determines how every later capability is exposed; SC8 closes a correctness gap in data already captured. |
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

### VN2 - output contract v9

**Priority:** Now. **Gate:** each shape change is graded by the tuning loop before it
ships, not argued.

VN1 raised this item's value rather than lowering it. Transport turned out not to be
a lever - the client re-materializes structured content and already spills an
oversized result to a file - so the only way to reduce what an investigation costs is
to send fewer rows and to make a result route its own follow-up. That is this item.

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

The two outliers already have a cheap answer through existing options, which sizes
the prize before any schema work: the JIT report costs 2,251 tokens at its default
`--top 25` and 172 at `--top 1`; a raw event page costs 5,538 and the same count
with `--take 1 --max-payload 0` costs 79. A `detail` vocabulary is worth adding for
the defaults and the discoverability, not because the capability is missing.

| Operation | Proposed MCP default | Behavior |
|---|---|---|
| info | `summary` | source/PDB method and module lists need `rows` |
| rank / callers / tree / source | current bounded rows | `top` and depth remain the natural control |
| GC / JIT / disk reports | `summary` | per-GC, per-method, per-file records need `rows` |
| thread pool | `summary` | already small |
| events | count or summary | event records need `rows`; paging stays `skip`/`take` |
| timeline | current bounded buckets | lanes and bucket count remain the control |
| diff / batch | current structural caps | already compact agent summaries |

The CLI-detailed / MCP-summary asymmetry is a candidate, not a decision. Compare both
defaults on questions that need only aggregates and on questions that need evidence
rows; reject a summary default whose saved first-response tokens are offset by
escalation calls. Deterministic tasks pass an explicit detail level so goldens do not
depend on host defaults.

Grade each default with `baseline -> candidate -> Compare-EvalRuns` at **N=10 per
model**, the way the speedscope wording change was graded. `gpt-5.6-sol` costs no
premium requests, so a full-suite arm is wall time only; add `claude-haiku-4.5` as
the overfitting detector when a candidate looks worth keeping. N=3 is not enough to
size an effect - it put one baseline at 33% where ten iterations put it at 10%.

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

**Priority:** Now. **Cost:** low - smaller than first assumed, see below. **Where:**
Core, not the capture script.

`collect --iterations` records each launch's exact root process id in the capture
manifest, but the batch analyzer does not thread a per-case scope through, so the
recorded ids are captured and unused. A command matrix therefore still scopes by
process *name*, which warns when the name matches several unrelated trees and ranks
them together.

**No plumbing is needed.** `CaptureManifestBatchAnalyzer.Analyze` already takes its
loader as `Func<CaptureManifest, CaptureManifestCase, LoadedTrace>`, so the case is
already in hand at the point the scope is chosen, and `CaptureManifestCase.Invocations`
already carries each `ProcessId`. The change is confined to the four `load` lambdas
that call `ManifestScope` - batch and diff in each head - plus the three near-identical
`ManifestScope` helpers in [BatchExecutor](../src/Filtrace/Cli/BatchExecutor.cs),
[DiffExecutor](../src/Filtrace/Cli/DiffExecutor.cs), and
[TraceTools](../src/Filtrace.Mcp/TraceTools.cs), which are worth collapsing to one
while they are being touched.

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
- summary-mode JIT and raw-event count tasks fall below 500 response tokens - already
  met through existing options at 172 and 79, so v9 must preserve it rather than
  reach it, and the two tasks now carry a `maxResponseTokens` of 500 so a live run
  enforces it;
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

1. ~~Does JSON-text-only preserve agent composition well enough to remove advertised
   output schemas?~~ **Moot.** VN1 showed the client re-materializes structured
   content into the model's view, so removing the server's text copy saves nothing
   there; the remaining ~1,020 tokens are a tool-list question, not a transport one.
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

VN2. VN0 and VN1 are both closed. VN0 built the accounting, the intent grading, the
schema breakdown, the comprehension tasks, and a repeatable multi-model baseline; VN1
spent that machinery to answer the transport question and retain variant A, because
the client re-materializes structured content and already bounds a large result.

That leaves result shape, which is where the measured wins actually are: the JIT
summary task fails because a tool's default detail costs 2,251 tokens against the 172
the question needs, and the same shape shows up in `events`. VN2's detail profiles
address that directly, and unlike transport they change what the model is asked to
read rather than how it is delivered.
