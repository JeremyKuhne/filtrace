# filtrace implementation history

**Status:** Completed historical record

**Completed:** 2026-06 through 2026-07

filtrace began as the `traceq` analyzer inside
[touki](https://github.com/JeremyKuhne/touki) and was promoted into this standalone
repository. The original implementation plan described that extraction in detail:
scaffolding, provider and engine separation, CLI and MCP heads, output contracts,
fixtures and parity, documentation single-sourcing, packaging, and the eval harness.
Those milestones are complete. Git history retains the original long-form plan;
this page preserves the decisions that still explain the repository.

For current work, use [vnext-improvement-plan.md](vnext-improvement-plan.md). The
current TraceEvent capability inventory is
[traceevent-surface-assessment.md](traceevent-surface-assessment.md).

## Outcome

The implementation produced:

- `src/Filtrace.Core` - the public analysis library, providers, readers, scoping,
  aggregation, typed results, deterministic JSON, and token budgets;
- `src/Filtrace` - the ConsoleAppFramework CLI;
- `src/Filtrace.Mcp` - the stdio MCP server over the same core;
- `tests/Filtrace.*.Tests` - unit, CLI, MCP, and frozen-oracle parity tests;
- `fixtures` - committed EventPipe, ETW, and speedscope captures;
- `eval` - deterministic and live-agent evaluation harnesses;
- `docs`, `.agents/skills/filtrace`, and contract scripts - one knowledge layer
  with drift checks;
- public NuGet packages for the CLI and MCP server.

The analyzer itself targets .NET 10. It reads traces produced by modern .NET and
.NET Framework. `TraceQ.Fixtures.HotLoopBench` remains the fixture namespace because
it is embedded in committed binary captures.

## Architecture decisions that remain current

### One core, two heads

Analysis belongs in `Filtrace.Core`. The CLI and MCP projects validate requests,
map errors, and render results; they do not implement separate analysis semantics.
Both heads use the same typed `AnalysisResult<T>` contract.

### One metric-generic stack engine

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

`FoldingAggregator` then performs self/inclusive ranking, caller drill, call-tree,
source attribution, classification, and related transforms where the public
operation supports that metric. Structured providers such as GC, JIT, thread-pool,
disk I/O, timelines, and raw events return dedicated records rather than forcing
non-stack data into that engine.

### Scenario scope before presentation

A machine-wide ETW capture is auto-scoped to the busiest process tree unless the
caller selects another process or widens to every process in the CLI. Root,
BenchmarkDotNet workload, activity, and time-window scopes narrow the analysis
before aggregation. Physical ETL relogging remains a transport/fixture technique,
not the normal analysis path; see
[filtrace-etl-trimming.md](filtrace-etl-trimming.md).

### Trace quality is part of the result

Frame-name resolution, source/PDB identity and sequence-point coverage,
contributing-record counts, capture enablement, event counts, ambiguous frame
matches, and bounded-output warnings are evidence, not diagnostics to hide on
stderr. They travel with the result so an agent can decide whether a conclusion is
trustworthy.

### Deterministic, bounded output

Every machine-readable result uses compact, camel-cased, deterministically rounded
JSON. The CLI and MCP heads share source-generated serializer metadata. CI gates the
MCP `tools/list` size, and result producers bound rows, strings, payloads, buckets,
and manifest cases under the response ceiling.

### ETLX conversion is coordinated

Same-trace conversion is coordinated across threads and processes by canonical
path. Conversion writes a unique temporary sibling and atomically publishes the
completed cache. Cache state is observable; different traces remain independent.

### Public dependencies stay published

`Filtrace.Core` references `KlutzyNinja.Touki` as a published NuGet package rather
than a project reference. This keeps the repository independently buildable and
validates the same dependency shape consumers receive.

## Delivery milestones

| Milestone | Delivered |
|---|---|
| M0 | Standalone scaffold, independent SDK/build configuration, and extraction rehearsal. |
| M1 | Core readers, providers, stack engine, object model, deterministic envelope, quality gates, cache, and fixtures. |
| M2 | CLI verbs, text/JSON renderers, exit-code contract, help lint, capture/export/cache operations. |
| M3 | Curated MCP facade, typed structured results, stdio purity, schema budget, and round-trip contract. |
| M3.5 | Promotion from Touki into the standalone filtrace repository. |
| M4 | Public packages, workflow documentation, shipped skill, generated MCP metadata, and docs drift checks. |
| M5 | Deterministic eval gate, live CLI/MCP agent arms, multi-model labels, and baseline comparison tooling. |
| Issue #42 roadmap | Capture-state correctness, source/PDB diagnostics, isolated manifests, normalized paired diff, and manifest batch analysis. |

The old plan described M6 as a combined Touki migration, v1.0 cadence change, MCP
registry entry, and badges. Those are no longer treated as one implementation
milestone. Remaining stabilization and distribution decisions now follow the
measured v.next surface selection.

## Fixture and validation strategy

Binary trace semantics are protected at several levels:

1. unit tests pin pure transforms, bounds, parsing, and object contracts;
2. committed trace fixtures exercise real TraceEvent paths;
3. parity tests compare against frozen oracle outputs where binary captures make
   regeneration difficult;
4. CLI and MCP tests ensure both heads preserve the core result;
5. contract scripts gate help, docs, MCP wire behavior, capture helpers, and skills;
6. deterministic eval tasks pin answers, call counts, and output-token baselines;
7. live-agent runs compare surface changes across models before acceptance.

A golden or baseline is updated only after reviewing the semantic change; it is not
blindly regenerated to make a gate pass.

## Publishing decisions

The incubation-era proposal to use a private GitHub Packages feed until v1.0 was
superseded. filtrace published directly to public NuGet.org through Trusted
Publishing. Package IDs and the two-head architecture were established before the
surface reached stability.

The MCP tool names are frozen in the current contract. Removing or renaming them
requires the explicit v.next breaking-change process described in
[vnext-improvement-plan.md](vnext-improvement-plan.md).

## Native AOT status

Full Native AOT remains a goal, not a current compatibility claim. TraceEvent is
mandatory throughout the analysis graph and relies on reflection, dynamically built
event parsers, and ETW native interop without trim/AOT annotations. Do not set
`IsAotCompatible` or `PublishAot` on filtrace projects until a real native publish
of the complete graph succeeds. Source-generated JSON and trim-safe filtrace-owned
code still reduce avoidable blockers, but they do not make the TraceEvent dependency
AOT-safe.

## Current roadmap

All unshipped capability, surface, transport, output-contract, evaluation, AOT, and
release work is tracked in [vnext-improvement-plan.md](vnext-improvement-plan.md).
This file is intentionally no longer an active checklist.
