# filtrace design

**Status:** Current. This page states the principles, goals, and measures of success
that govern further development.

**Last verified:** 2026-08-01 against `main`.

Anything that is a *plan* belongs in [roadmap.md](roadmap.md). Anything that is a
*comparison with other tools* belongs in
[competitive-analysis.md](competitive-analysis.md). This page is the standing
contract that both of those are judged against.

## What filtrace is

filtrace is a .NET trace analyzer with two heads over one analysis library: a
`filtrace` CLI and a stdio MCP server. It reads EventPipe (`.nettrace`),
speedscope (`.speedscope.json`), and Windows ETW (`.etl`) captures produced by
modern .NET and by .NET Framework, and answers where time, allocation, exceptions,
blocking, and wall clock went. The analyzer itself targets .NET 10.

It exists because an AI agent investigating a performance question needs three
things a screen-scraped profiler cannot give it: a typed result it can bind to, a
statement of how much that result can be trusted, and a next step that preserves
the scope it already established.

## Goals

1. **Answer a performance question with evidence, not just numbers.** Every result
   carries the scope it ran under and the quality of the data behind it.
2. **Serve an agent and a human from the same semantics.** Two renderings - dense
   text and compact deterministic JSON - over one analysis contract.
3. **Cover both capture stacks.** EventPipe for no-elevation, cross-platform
   reach; ETW for wall-clock, multi-process, native, and kernel evidence that
   EventPipe cannot express.
4. **Drill to something actionable.** From a symptom, to a metric, to a frame, to a
   source line, to a comparison against a baseline.
5. **Keep total investigation cost low.** Context paid before the first call, plus
   payload paid after each call, plus the calls wasted on misunderstanding.
6. **Stay verifiable.** Deterministic output, frozen oracles, contract scripts, and
   an eval harness that measures agent behavior rather than assuming it.

## Non-goals

- **PerfView parity.** filtrace resists breadth-for-its-own-sake; the roadmap is
  the pressure valve. See [competitive-analysis.md](competitive-analysis.md).
- **Reimplementing collectors or viewers.** Capture integrates `TraceEvent`
  sessions; rendering exports to speedscope and Chromium/Perfetto.
- **One universal `trace_query` tool.** A single polymorphic operation trades a
  smaller tool count for a large union input schema, weaker tool selection,
  runtime-only validation, and a result shape neither agents nor humans can
  predict.
- **Merging unrelated analysis families** because they share a helper.
- **Prose for agents.** Markdown or fixed-width tables never replace JSON objects
  on the machine-readable path, and property names are not abbreviated into opaque
  wire codes to save tokens.
- **Side effects behind MCP.** Capture, elevation, and ETLX cache mutation stay
  explicit CLI responsibilities.
- **Opaque server-side trace handles as the only address.** Paths and manifest
  identities stay reproducible across sessions.

## Principles

### One core, two heads

Analysis belongs in `Filtrace.Core`. The CLI and MCP projects validate requests,
map errors, and render results; they do not implement separate analysis semantics.
Both heads return the same typed `AnalysisResult<T>` envelope.

### One metric-generic stack engine, plus structured providers

Stack-producing providers normalize their observations to weighted stacks:

| Provider family | Weight |
|---|---|
| CPU | sampled milliseconds |
| Thread time | running or blocked elapsed milliseconds |
| Allocation | sampled allocated bytes |
| Exceptions | throw count |
| Contention | blocked milliseconds |
| Wait | completed wait milliseconds |
| Activity | operation elapsed milliseconds |

`FoldingAggregator` then performs self/inclusive ranking, caller drill, call tree,
source attribution, and classification wherever the public operation supports that
metric. Structured providers - GC, JIT, thread pool, disk I/O, lifecycle, timeline,
raw events - return dedicated records instead of forcing non-stack data through the
folding engine.

Public boundaries follow from this: `callers`, `lines`, `heatmap`, `tree`, `diff`,
and `export` are defined over the CPU stack source. Non-CPU metrics refine
self/inclusive, root, process, activity, or time scope rather than silently
crossing into CPU evidence.

### Scope the scenario before presenting it

A machine-wide ETW capture is auto-scoped to the busiest process tree unless the
caller names a process, names exact process ids, or widens to every process in the
CLI. Root, BenchmarkDotNet workload, activity, and time-window scopes narrow the
analysis before aggregation. Physical ETL relogging is a transport and fixture
technique, not the normal analysis path; see
[filtrace-etl-trimming.md](filtrace-etl-trimming.md).

### Trace quality is part of the result

Frame-name resolution, source and PDB identity, sequence-point coverage,
contributing-record counts, capture enablement, event counts, ambiguous frame
matches, and bounded-output warnings are evidence, not diagnostics to hide on
stderr. They travel with the result so a caller can decide whether a conclusion is
trustworthy. A frame-name resolution rate below `SymbolGate.MinimumResolutionRate`
(0.8) raises a quality warning.

### Format support is not capture enablement

A supported file extension does not prove a provider was enabled, and zero events
does not prove no work occurred. Availability reporting distinguishes
enabled-with-zero-events, disabled, and unknown, and routing hints only point at
analyses the trace can actually answer.

### Deterministic, bounded output

Every machine-readable result uses compact, camel-cased, deterministically rounded
JSON, produced through source-generated serializer metadata shared by both heads.
Result producers bound rows, strings, payloads, buckets, and manifest cases under
the response ceiling, and report when they truncated.

### Separate the human surface from the agent surface

CLI and MCP share analysis semantics, not necessarily discovery shape. Humans
benefit from short commands, aliases, and grouped help. Agents benefit from
intent-bearing names, constrained schemas, and machine-readable results. Forcing
one surface to mirror the other produces avoidable CLI aliases and avoidable
permanent MCP schemas.

### Consolidate by intent, not by implementation

A good consolidation has one user intent and compatible inputs - `gcstats`,
`jitstats`, `threadpool`, and `diskio` are all bounded structured reports; `lines`
and `heatmap` are both source attribution. A bad one combines different arity or
side-effect contracts because they share a helper.

### Constrain inputs at schema time

Metric, measure, report kind, source view, detail level, and export format are
closed vocabularies and belong in schema enums, not in a free-form string validated
at runtime. Where JSON Schema cannot express a conditional (`root` versus
`benchmark`), the parameter description and the error message must use the same
wording.

### Optimize total investigation cost

The cost that matters is:

```text
permanent tool definitions
+ all tool responses
+ retries caused by misunderstanding
+ final answer context
```

A smaller schema that provokes one extra orientation or repair call is a net loss.
Surface changes are therefore judged on success, call count, response tokens, and
wall time together - never on tool count alone.

### Add analysis in Core, and extend a compatible operation before adding surface

New capability starts as a provider and result record in `Filtrace.Core`. A new
stack metric belongs behind `rank`; a new bounded report belongs in the report
family; a new temporal view belongs in `timeline`. A standalone verb or MCP tool is
the exception that must be argued for, because the MCP tool list is permanent
context paid on every conversation.

### Own the analysis; integrate capture and rendering

`collect` drives a `TraceEvent` session rather than reimplementing a collector, and
`export` writes speedscope and Chromium/Perfetto profiles rather than building a
viewer. The value filtrace adds is between capture and rendering.

### Keep dependencies published, and carry provenance

`Filtrace.Core` references `KlutzyNinja.Touki` as a published NuGet package, not a
project reference, so the repository builds standalone and validates the dependency
shape consumers receive. Ported third-party code keeps its upstream copyright
notice and source provenance in addition to project-level notices.

## Measures of success

### Enforced gates

These are checked by CI; a change that breaks one is not shippable.

| Measure | Gate | Current | Enforced by |
|---|---|---|---|
| MCP `tools/list` size | <= 7,000 estimated tokens | ~6,503 tokens / 25,770 chars over 18 tools | [tools/Test-McpServer.ps1](../tools/Test-McpServer.ps1) |
| MCP stdout purity | pure JSON-RPC, real `tools/call` round trip | envelope `schemaVersion` 13 | [tools/Test-McpServer.ps1](../tools/Test-McpServer.ps1) |
| Single analysis response | <= 25,000 tokens (`OutputBudget.DefaultCeilingTokens`) | every producer bounds its rows against `OutputBudget.DefaultRowBudgetTokens` | Core budget plus worst-case tests |
| Per-verb `--help` | <= 60 lines | 25 verbs | [tools/Test-CliHelp.ps1](../tools/Test-CliHelp.ps1) |
| Verb discoverability | every verb in top-level help, with a README example and a scope-inventory entry | 25 verbs | [tools/Test-CliHelp.ps1](../tools/Test-CliHelp.ps1) |
| Catalog completeness | every verb and every `trace_*` tool documented | 25 verbs / 18 tools | [tools/Test-Docs.ps1](../tools/Test-Docs.ps1) |
| Knowledge-layer drift | zero drift between `docs/` blocks and their embedded copies | 4 blocks | [tools/Test-Docs.ps1](../tools/Test-Docs.ps1) |
| Deterministic eval | every task keeps its answer, call count, and output budget | 23 tasks | [eval/Invoke-Eval.ps1](../eval/Invoke-Eval.ps1) |
| Numeric parity | rankings match the frozen oracle within tolerance and ordering | committed fixtures | `tests/Filtrace.Parity.Tests` |
| Capture contract | run artifacts isolated, overlap rejected, every case in the manifest | - | [tools/Test-CaptureBenchmarkTrace.ps1](../tools/Test-CaptureBenchmarkTrace.ps1) |
| Native symbol resolution | frames resolve with `--symbols` and not without | - | [tools/Test-NativeSymbolResolution.ps1](../tools/Test-NativeSymbolResolution.ps1) |
| Skill contract | commons cores match the pin; overlays, metadata, and links valid | - | [tools/Test-AgentSkills.ps1](../tools/Test-AgentSkills.ps1) |
| Build | zero warnings under `TreatWarningsAsErrors` | - | CI |

### Efficacy measures

These decide whether a surface change is an improvement. They are measured by the
eval harness across more than one model family, with repeated runs and medians -
one-shot success is too noisy to decide a surface question.

| Measure | Target |
|---|---|
| Task success | no regression on any model or task |
| Expected-operation selection | the agent picks the operation the question implies, without inventing a nonexistent one |
| Median tool calls | does not increase |
| p95 tool calls | stays within the current six-call ceiling |
| Total investigation tokens | falls for a change justified by token cost; a rename or removal justified as simplification must show at least 20% |
| Repair calls | incompatible-parameter and ambiguous-default retries trend to zero |
| Wall time | no material regression |

A semantic improvement - a structured diagnostic, a clearer next step - may proceed
on accuracy or removed repair calls alone, without a token win. A breaking
simplification may not.

### What the gates deliberately do not measure

Tool count, verb count, and description length are not goals. Descriptions are a
small share of the permanent schema cost, so tightening prose cannot buy headroom;
and a lower tool count that costs an extra call is a loss under
[total investigation cost](#optimize-total-investigation-cost).

## Frozen contracts

Changing one of these is a deliberate, announced decision, not a refactor.

- **`trace_*` MCP tool names.** Clients bind to them. Tools may be added; renaming
  or removing one requires the breaking-change decision described in
  [AGENTS.md](../AGENTS.md) and a versioned surface in [roadmap.md](roadmap.md).
- **The result envelope.** `schemaVersion` (currently 13), structured `warnings` and `hints`,
  effective query `context`, and the typed result. A shape change bumps the version and updates both renderers,
  the goldens, and the budgets together.
- **CLI exit codes.** Success, usage error, input error, and the `--strict`
  symbol-gate code.
- **`TraceQ.Fixtures.HotLoopBench`.** Baked into committed binary captures that
  cannot be regenerated without elevated ETW.
- **Deterministic JSON.** Field names, ordering, and rounding are part of the
  contract because goldens and agent parsing both depend on them.

## Validation strategy

Binary trace semantics are protected at several levels, weakest to strongest:

1. unit tests pin pure transforms, bounds, parsing, and object contracts;
2. committed trace fixtures exercise real `TraceEvent` paths;
3. parity tests compare against frozen oracle output where regenerating a binary
   capture is impractical;
4. CLI and MCP tests prove both heads preserve the core result;
5. contract scripts gate help, docs, MCP wire behavior, capture helpers, native
   symbol resolution, and skills;
6. deterministic eval tasks pin answers, call counts, and output-token baselines;
7. live-agent runs compare surface changes across models before acceptance.

A golden or baseline is updated only after reviewing the semantic change behind it.
It is never regenerated merely to make a gate pass.

Some checks cannot be committed fixtures. A filtrace capture records no PDB identity
of its own, so `TraceEvent` resolves a native module by reading the binary back from
the absolute path recorded in the trace - a committed capture therefore resolves
only on the machine that took it. Those checks capture during the CI run instead,
which hosted Windows runners permit because they run elevated.

## Known constraints

- **Native AOT is blocked by `TraceEvent`.** It relies on reflection, dynamically
  built event parsers, and ETW native interop, and is not annotated as trim- or
  AOT-safe. Do not set `IsAotCompatible` or `PublishAot` on filtrace projects until
  a real native publish of the whole analysis graph succeeds. Source-generated JSON
  and trim-safe filtrace-owned code remove avoidable blockers but do not make the
  dependency AOT-safe.
- **ETW is Windows plus Administrator.** Everything that depends on it - thread
  time, disk I/O, lifecycle phases, native frames, machine-wide multi-process
  scope, `collect` - inherits that. Extending the default EventPipe loop is worth
  more than an equivalent ETW-only addition.
- **Some analysis is dependency-gated, not merely unwritten.** The pinned
  `TraceEvent` package does not ship the heap-graph or heap-simulator types that
  retention and net-surviving-heap analysis need; see
  [traceevent-surface-assessment.md](traceevent-surface-assessment.md).
- **The permanent MCP schema is a shared budget.** Every new tool spends context on
  every conversation with every client, whether or not it is called.
