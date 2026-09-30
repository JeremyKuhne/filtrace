# filtrace design

**Status:** Current. This page states the principles, goals, and measures of success
that govern further development.

**Last verified:** 2026-09-30 against the canonical-only VN5 surface and the
repository's documentation, CLI/MCP, and test contracts.

Filtrace ordering, conditional work, and completed surface decisions belong in the
public [roadmap.md](roadmap.md). Cross-repository coordination becomes actionable in
this repository only when reflected there. This page is the standing contract
that proposed changes are judged against; dated external-tool comparisons and
completed experiment logs are not independent sources of current work.

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

- **PerfView parity.** filtrace resists breadth-for-its-own-sake; hand off
  unsupported analysis to a specialist rather than importing a profiler's
  entire surface.
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
- **Capture and destructive cache operations behind MCP.** Capture, elevation,
  and explicit cache housekeeping remain CLI responsibilities. An MCP analysis
  can still prepare or recover its adjacent ETLX as part of loading a trace;
  "read-only tool" does not mean zero disk writes.
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
| CPU | trace-reported milliseconds when established; otherwise raw samples |
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

Public boundaries follow from this: `callers`, `source`, `tree`, `diff`,
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

A good consolidation has one user intent and compatible inputs:
`report --kind` selects bounded GC, JIT, thread-pool, or disk-I/O reports,
and `source --view` selects CPU source-line ranking or heatmap attribution.
A bad one combines different arity or side-effect contracts because they share
a helper.

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

## Findings that constrain future changes

These are the durable conclusions of completed investigations, not another
implementation queue. The [roadmap](roadmap.md) carries only work with a current
trigger. Git history and linked issue/PR evidence retain the experiment detail.

### Agent evidence and interface decisions

- **A smaller wire payload need not save agent context.** A JSON-text-only MCP
  response removed the server's structured/text duplication, but the tested
  client re-materialized `structuredContent` for the model. The visible
  result was no smaller. Keep typed output and the text fallback until a
  different client demonstrates a need to change the transport.
- **Tool consolidation can harm selection.** A nested `trace_report` union
  removed four advertised tools and about 525 estimated schema tokens but
  reduced disk-I/O success from 100% to 90% and lifecycle success from 70%
  to 30% on the measured low-cost model. Lost or omitted invocation-root
  selectors mattered more than the schema saving. Keep intent-bearing tools;
  do not resurrect a universal query merely to reduce tool count.
- **Constrain detail with an existing axis before adding grammar.** An
  `info` summary default regressed a measured compatibility task from 90%
  to 40%. By contrast, allowing report `top: 0` and events `take: 0`
  provided aggregate/count-only answers without an interacting `detail`
  parameter. Preserve the bounded default information that an agent
  actually needs to choose its next operation.
- **Explicit scope matters more than a superficial skill win.** In EP1 the
  discovered skill produced 5/10 strict answers versus 3/10 for CLI-only,
  but the result was strongly order-sensitive. Failed skill chats accepted
  an automatic process choice when the question named a different tree.
  EP2's completed answer set tied 12/12 to 12/12 with zero false confidence,
  and candidate treatment delivery was verified in only 11/12. Neither
  experiment proves a general answer-quality or effort advantage. Validate
  the requested process, root, units, and scope in a new agent workflow
  rather than counting a command reduction as success.
- **Reuse a parsed model only where its ownership is clear.** MCP already
  injects one `TraceStore` for successive `trace_*` calls. In a three-query
  warm-trace comparison that saved a median paired 1,445 ms on a large ETL
  versus three fresh CLI processes, while the server used more peak sampled
  private memory. A separate two-query CLI prototype saved 1,189 ms on that
  ETL and 209 ms on a small EventPipe trace without a systematic private
  memory increase when one store was shared. The query sets differ, and
  neither test established agent efficacy or a public multi-operation CLI
  contract. See the [measurement and options](multi-operation-query-reuse.md).

### Capture, identity, and evidence limits

- **A readable trace is not necessarily usable evidence.** Check capture
  provider state, event loss, process identity, symbol and source quality,
  contributing records, and per-provider sampling units before attribution.
  SampleProfiler samples are raw counts and may include waiting; they are
  not ETW on-core CPU milliseconds. A mixed source stays in counts even
  when its ETW subset has an established interval. Warnings and qualified
  hints must follow that provenance through CLI/MCP and manifest results.
- **An automatic process choice can select the wrong work.** An EP1 task
  and a later machine-wide `csc` capture both exposed the risk of name
  matches across unrelated instances. Use process inventory and exact
  invocation/root ids when the question identifies a specific workload.
  Do not silently replace missing recorded ids with a name match.
- **An ETLX is a disk cache, not proof of a valid parsed view.** Cache
  provenance must be checked against its raw input and sidecar before
  reuse; a changed trace at the same path cannot inherit an old answer.
  A warm ETLX still leaves parse/model work for a fresh CLI process.
  Capture and replay must bind inputs, symbols, effective scope, and the
  actual analyzer build to avoid a success-shaped but substituted result.
- **A short-command capture can measure its own observer effect.** ETW
  session startup and CLR naming can dominate a 30-100 ms subject.
  Keep an uninstrumented timing baseline, use the leanest provider
  profile that can answer the question, and verify actual recorded
  sampling rather than the requested interval alone. The `startup`
  profile is lower perturbation, not proof of a kernel-only capture.
  Do not schedule another capture mode without an equivalent-work
  experiment showing that it fixes a real scenario.
- **External profiler artifacts need exact case identity.** The
  BenchmarkDotNet adapter currently reconciles console text and
  profiler filenames because its JSON exporter does not expose a
  case-to-artifact path. Ambiguous pairing fails closed; an exporter
  with benchmark identity but no artifact path is not a substitute.
  Revisit this boundary when a stable upstream mapping exists or a
  reproduced producer change breaks it ([issue #57](https://github.com/JeremyKuhne/filtrace/issues/57)).
- **Physical relogging trades fidelity for transport size.** A fixture
  trim reduced a machine-wide disk capture enough to commit, but the
  relogger did not rebuild the managed-method address map. Native/file
  evidence survived; JITted managed frames did not. Use analysis-time
  process/time scoping for lossless investigation. The necessary fixture
  procedure and limitation remain in
  [filtrace-etl-trimming.md](filtrace-etl-trimming.md).
- **Native symbols are not automatically portable.** The current ETW
  capture does not embed the PDB identity needed for off-machine native
  resolution. Tests capture and resolve on the same machine instead of
  promising that a committed native-symbol trace resolves everywhere.

### Performance evidence and dependency boundaries

- **Test the whole analysis path, not only a microbenchmark.** Frame-label
  reuse moved a warm 1M ranking from roughly six seconds to 3.3-3.5
  seconds. An indexed stack walk then improved the 1M row another 9.5%
  with TraceEvent and 12% with source-integrated FastTrace, using the
  existing public APIs. Sampled allocations fell, but process memory
  rose on that row. Those are workload-specific consumer wins, not proof
  that parallelization, a replacement engine, or lower retained memory
  follows. Use an isolated benchmark, the matching end-to-end CLI
  scenario, a fixed-analyzer attribution profile, exact output/scope
  parity, and explicit CPU/GC/memory evidence. There is no universal
  wall-time or memory percentage floor for future experiments; report
  absolute effects and uncertainty. Reusable commands live in
  [the benchmark guide](../benchmarks/README.md).
- **The default engine remains TraceEvent 3.2.6.** Source-integrated
  FastTrace produced useful Windows/Linux x64 JIT and Native AOT results,
  but cold large-input throughput and working set could lose. It is a
  namespace-adjusted source build, not a published, binary-compatible
  replacement or a Native AOT claim for the default product. Use the
  [source-build guide](source-build.md) only for an explicitly selected
  comparison.
- **Some missing answers need a different data model.** The pinned
  TraceEvent package does not contain `MemoryGraph`, `GCHeapDump`, or
  `GCHeapSimulator`; sampled allocation cannot answer path-to-root
  retention or net surviving heap. Route those questions to a heap
  snapshot/PerfView or another specialist until a compatible graph or
  simulator is selected. PMC sampling has an analysis-side event surface
  but still needs a proven ETW capture and fixture. Re-audit these facts
  when the pin in [Directory.Packages.props](../Directory.Packages.props)
  changes rather than carrying an old package inventory forward.
- **Prefer fixed ownership for repository-local activation.** An earlier
  general local-testing implementation accumulated path, lock, schema,
  and rollback branches. Its replacement uses fixed per-worktree managed
  paths, an immutable baseline, one lock, and explicit resumable states.
  Do not add arbitrary managed paths or automatic migration without a
  concrete contributor scenario. The current
  [activation and recovery guide](local-testing.md) preserves the
  operator contract.

## Measures of success

### Enforced gates

These are checked by CI; a change that breaks one is not shippable.

| Measure | Gate | Current | Enforced by |
|---|---|---|---|
| MCP `tools/list` size | <= 7,000 estimated tokens | ~6,928 tokens / 27,389 chars over 18 tools | [tools/Test-McpServer.ps1](../tools/Test-McpServer.ps1) |
| MCP stdout purity | pure JSON-RPC, real `tools/call` round trip | envelope `schemaVersion` 18 | [tools/Test-McpServer.ps1](../tools/Test-McpServer.ps1) |
| Single analysis response | <= 25,000 tokens (`OutputBudget.DefaultCeilingTokens`) | every producer bounds its rows against `OutputBudget.DefaultRowBudgetTokens` | Core budget plus worst-case tests |
| Per-command `--help` | <= 60 lines | 16 canonical commands | [tools/Test-CliHelp.ps1](../tools/Test-CliHelp.ps1) |
| Command discoverability | every registered command in top-level help, README examples, and scope inventory | 16 canonical commands; top-level help within 27 lines / 2,171 chars | [tools/Test-CliHelp.ps1](../tools/Test-CliHelp.ps1) |
| Catalog completeness | every canonical command and every `trace_*` tool documented | 16 commands / 18 tools | [tools/Test-Docs.ps1](../tools/Test-Docs.ps1) |
| Knowledge-layer drift | zero drift between `docs/` blocks and their embedded copies | 4 blocks | [tools/Test-Docs.ps1](../tools/Test-Docs.ps1) |
| Deterministic eval | every task keeps its answer, call count, and output budget | 31 tasks | [eval/Invoke-Eval.ps1](../eval/Invoke-Eval.ps1) |
| Numeric parity | rankings match the frozen oracle within tolerance and ordering | committed fixtures | `tests/Filtrace.Parity.Tests` |
| Capture contract | run artifacts isolated, profiles preflighted, every case in the manifest | - | [tools/Test-CaptureBenchmarkTrace.ps1](../tools/Test-CaptureBenchmarkTrace.ps1), [tools/Test-CaptureProjectTrace.ps1](../tools/Test-CaptureProjectTrace.ps1) |
| Analysis-record contract | exact argv and hashes retained; changed inputs rejected before replay | - | [tools/Test-FiltraceAnalysis.ps1](../tools/Test-FiltraceAnalysis.ps1) |
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

Tool count, command count, and description length are not goals. Descriptions are a
small share of the permanent schema cost, so tightening prose cannot buy headroom;
and a lower tool count that costs an extra call is a loss under
[total investigation cost](#optimize-total-investigation-cost).

## Frozen contracts

Changing one of these is a deliberate, announced decision, not a refactor.

- **`trace_*` MCP tool names.** Clients bind to them. Tools may be added; renaming
  or removing one requires the breaking-change decision described in
  [AGENTS.md](../AGENTS.md) and a versioned surface in [roadmap.md](roadmap.md).
- **The result envelope.** `schemaVersion` (currently 18), structured `warnings` and `hints`,
  effective query `context`, and the typed result. A shape change bumps the version and updates both renderers,
  the goldens, and the budgets together.
- **CLI exit codes.** Success, usage error, input error, and the quality-gate code.
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

- **The default TraceEvent-backed product is blocked from Native AOT.** TraceEvent
  relies on reflection, dynamically built event parsers, and ETW native interop and
  is not annotated as trim- or AOT-safe. Explicit FastTrace source builds have
  produced working Windows and Linux x64 native CLIs, but the published default
  engine has not changed; see [source-build.md](source-build.md). Do not mark the
  default projects AOT-compatible until the dependency decision and required native
  platform matrix are complete.
- **ETW is Windows plus Administrator.** Everything that depends on it - thread
  time, disk I/O, lifecycle phases, native frames, machine-wide multi-process
  scope, `collect` - inherits that. Extending the default EventPipe loop is worth
  more than an equivalent ETW-only addition.
- **Some analysis is dependency-gated, not merely unwritten.** The pinned
  `TraceEvent` package does not ship the heap-graph or heap-simulator types that
  retention and net-surviving-heap analysis need; route those questions to
  a specialist and recheck the [package pin](../Directory.Packages.props)
  before planning an in-process implementation.
- **The permanent MCP schema is a shared budget.** Every new tool spends context on
  every conversation with every client, whether or not it is called.
