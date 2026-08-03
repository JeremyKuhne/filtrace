# filtrace competitive analysis

**Status:** Current. Reviewed 2026-08-01.

**Basis:** filtrace at `main` on 2026-08-01 - 25 CLI verbs, 18 `trace_*` MCP tools,
three projects. pvanalyze at commit `208d2b8` (12 commands, single project). Other
tools are described at the level of documented, stable capability rather than a
pinned version; verify any single row before acting on it.

This page exists to answer two questions: **how is filtrace different**, and **what
should we take from everyone else**. It does not track our own work - actionable
items are carried into [roadmap.md](roadmap.md) with an ID, and the scope guard that
keeps this page from turning into a feature-parity checklist is in
[design.md](design.md#non-goals).

## The landscape

.NET performance tooling divides into four jobs. Most tools do one or two well.

| Job | Representative tools |
|---|---|
| **Capture** | `dotnet-trace`, `dotnet-counters`, `dotnet-gcdump`, `dotnet-dump`, `dotnet-monitor`, PerfView, `wpr`, BenchmarkDotNet diagnosers, filtrace `collect` |
| **Analyze** | PerfView, pvanalyze, filtrace, Visual Studio Performance Profiler, JetBrains dotTrace / dotMemory |
| **View** | speedscope, Firefox Profiler, Perfetto / Chrome trace viewer, PerfView's stack viewer, IDE profiler UIs |
| **Observe continuously** | Datadog / Sentry / Grafana Pyroscope-class continuous profilers, `dotnet-monitor` |

filtrace is an **analyzer**. It captures only where nothing else conveniently does
(a Windows ETW session for a child process), and it renders only by exporting to
viewers that already exist. Its differentiator is not the analysis primitives -
several tools compute the same rankings - it is that the result is a typed,
bounded, self-describing contract with a trust signal attached, addressable by an
agent over MCP.

## Tool by tool

### PerfView

The reference implementation, and the origin of the `TraceEvent` library filtrace
depends on. MIT, Windows, GUI-first with a scripting/command-line mode.

**Strongest at:** breadth and depth nothing else matches - CPU stacks with
folding/grouping/regex rewriting, thread time and blocked-time analysis, GC heap
snapshots with path-to-root retention, deep GC and JIT statistics reports, an event
viewer over any ETW or EventPipe provider, ETW capture with symbol merge, and stack
viewer diffing.

**Weak for our goals:** Windows-only and GUI-centered; its outputs are HTML, CSV,
and an interactive viewer rather than a machine contract; the learning curve is
famously steep; and there is no agent-addressable surface.

**Take from it:**

- **Retention and path-to-root** is the analysis we most visibly lack (VC5) - and
  PerfView is also the reason it is dependency-gated, since the heap-graph types
  live on its side rather than in the packaged `TraceEvent`.
- **Symbol merge on capture.** PerfView's merge step injects symbol identity into
  the `.etl` so a trace resolves on a machine other than the one that took it. That
  is exactly what blocks a committed native-symbol fixture for us (SC9).
- **Grouping and folding vocabulary.** Its group-by-module and group-by-namespace
  patterns are more expressive than our fold patterns; worth mining when the drill
  family is revisited (VN3).

### pvanalyze

The closest peer: a lean, cross-platform, AOT-friendly "companion to PerfView" -
one project, `System.CommandLine`, one static analysis engine, per-command
source-generated JSON. It deliberately ships no agent scaffolding, betting that
`--help` plus a README and `--format json` are enough for a frontier model.

**Strongest at:** breadth per line of code and cross-platform reach, plus three
capabilities filtrace does not have - **DATAS** server-GC heap-count tuning
analysis, a **point-in-time snapshot** around a chosen millisecond, and **per-method
temporal sample buckets**. It also groups CPU stacks by module or namespace,
auto-follows a hot path when a child holds >= 80% of its parent, and breaks out
large-object-heap allocation.

**Weak for our goals:** EventPipe-centered, so no wall-clock/blocked split, no
multi-process scoping, no native or kernel evidence; no source-line drill; no
trust signal, so an agent cannot tell a well-symbolized ranking from a
symbol-starved one; no two-trace comparison; no output bound.

**Take from it:** DATAS (VC1), snapshot (VC2), per-frame buckets (VC3), and the
open questions of group-by and hot-path auto-follow. Both projects are MIT and share
a runtime substrate, so these are realistic ports rather than rewrites - with the
provenance obligation stated in [design.md](design.md#keep-dependencies-published-and-carry-provenance).

### The `dotnet-*` diagnostics family

`dotnet-trace` (EventPipe capture, with speedscope/Chromium conversion),
`dotnet-counters` (live counters), `dotnet-gcdump` (heap snapshot), `dotnet-dump`
(process dump plus SOS), `dotnet-stack` (managed stacks), `dotnet-monitor`
(production diagnostics over HTTP, with triggers and egress).

**Strongest at:** being the standard, cross-platform, production-acceptable way to
*get* data. `dotnet-monitor` in particular solves capture in environments where
attaching a profiler is not an option.

**Weak for our goals:** with the partial exception of `dotnet-counters`, they do not
answer questions - they produce artifacts for something else to analyze.

**Take from it:** these are integration points, not competitors. Every one of them
produces an input filtrace should read or hand off to: `dotnet-gcdump` is the
capture half of VC5, and a `dotnet-monitor`-collected `.nettrace` is already an
input. Their argument conventions are also the vocabulary users arrive with.

### Visual Studio Performance Profiler

Integrated CPU, allocation, and async tooling in the IDE, able to open captured
`.nettrace` and `.diagsession` files and present them beside the source.

**Strongest at:** the last mile for a human - clicking from a hot method into the
code, with allocation and async views that need no vocabulary to read.

**Weak for our goals:** interactive by construction; not scriptable, not
composable into an automated investigation, and not available in a CI or agent
loop.

**Take from it:** the ambition of its source-line view. Our `lines` and `heatmap`
are the terminal equivalent, and the standard to hold them to is "would this send
someone to the right line as reliably as the IDE would".

### JetBrains dotTrace / dotMemory

Commercial profilers with timeline profiling, several sampling and instrumentation
modes, memory snapshots with retention paths, and remote or CI attach.

**Strongest at:** the timeline and memory-retention user experience, and
presenting wall clock in a way non-experts read correctly.

**Weak for our goals:** licensed, GUI-first, proprietary snapshot formats, so
nothing composes into an open pipeline.

**Take from it:** their framing of wall clock - a timeline where blocked time is as
visible as CPU time - is the model our `threadtime`, `timeline`, and `lifecycle`
outputs are trying to reach in text.

### Viewers: speedscope, Firefox Profiler, Perfetto

Not competitors; the rendering half of the pipeline. filtrace exports speedscope
and Chromium/Perfetto profiles precisely so it never has to build a viewer.

**Take from it:** keep export fidelity honest. Our CPU export uses a synthetic
weight axis rather than original chronology, and that has to stay documented at the
point of use, because a viewer will happily draw a timeline that is not one.

### Continuous production profilers

Always-on sampling aggregated across hosts and deploys, with regression alerting.

**Weak for our goals:** they answer "which service regressed last week", not "which
line of this benchmark got slower" - and they cannot see a 55 ms command or a
BenchmarkDotNet workload at all.

**Take from it:** their normalization discipline. Comparing two runs of different
lengths is only meaningful normalized, which is what our `diff` already does and
what any future comparison must keep doing.

### BenchmarkDotNet

Adjacent rather than competing: it measures, and its ETW/EventPipe diagnosers
produce traces. filtrace's benchmark scoping and capture manifests exist because
that is where a large share of real questions start.

**Take from it:** its statistical rigor is the bar for any claim we make about a
difference between two runs.

## Capability matrix

**Y** present, **~** partial or indirect, **-** absent. Rows are chosen to
discriminate, not to be exhaustive.

| Capability | filtrace | pvanalyze | PerfView | `dotnet-*` | IDE profilers |
|---|:---:|:---:|:---:|:---:|:---:|
| `.nettrace` input | Y | Y | Y | Y | Y |
| `.etl` (ETW) input | Y | ~ | Y | - | ~ |
| speedscope input | Y | - | - | - | - |
| .NET Framework traces | Y | ~ | Y | - | ~ |
| Cross-platform analysis | ~ | Y | - | Y | ~ |
| CPU self / inclusive ranking | Y | Y | Y | - | Y |
| Call tree / caller-callee | Y | Y | Y | - | Y |
| Group by module / namespace | - | Y | Y | - | Y |
| Hot-path auto-follow | - | Y | Y | - | Y |
| Source-line attribution | Y | - | ~ | - | Y |
| Wall-clock thread time (running vs blocked) | Y | - | Y | - | Y |
| Lock contention / wait metrics | Y | - | Y | - | ~ |
| Allocation ranking | Y | Y | Y | - | Y |
| LOH breakout | - | Y | Y | - | Y |
| GC report | Y | Y | Y | - | Y |
| DATAS server-GC tuning | - | Y | - | - | - |
| Retention / path-to-root | - | - | Y | ~ | Y |
| JIT report | Y | Y | Y | - | ~ |
| Thread-pool starvation report | Y | - | ~ | ~ | - |
| Physical disk I/O by file | Y | - | Y | - | ~ |
| Process lifecycle / wall-clock phases | Y | - | ~ | - | ~ |
| Multi-lane timeline | Y | Y | ~ | - | Y |
| Point-in-time snapshot | - | Y | ~ | - | Y |
| Per-method temporal buckets | ~ | Y | ~ | - | Y |
| Raw event query with payload/PID/TID filters | Y | Y | Y | - | - |
| Multi-process scoping (auto, name, exact pid, descendants) | Y | ~ | Y | - | ~ |
| Root / benchmark / activity / time scoping | Y | ~ | ~ | - | ~ |
| Two-run normalized diff | Y | - | ~ | - | ~ |
| Manifest case pairing and batch | Y | - | - | - | - |
| Symbol-resolution trust gate | Y | - | ~ | - | - |
| Output token budget | Y | - | - | - | - |
| MCP server | Y | - | - | - | - |
| Structured envelope with warnings and next steps | Y | - | - | - | - |
| Shipped agent skill | Y | - (by design) | - | - | - |
| Built-in capture | Y | - | Y | Y | Y |
| Symbol merge for portable traces | - | - | Y | ~ | Y |
| Native AOT single-file distribution | - | Y | - | - | - |
| Parity / oracle tests, eval harness | Y | - | - | - | - |

## What differentiates filtrace

1. **An agent binds to a contract, not to stdout.** 18 `trace_*` tools returning a
   typed `AnalysisResult<T>` envelope with `schemaVersion`, warnings, and next
   steps, under a CI-enforced schema and response budget.
2. **A trust signal travels with the result.** A frame-name resolution rate with a
   0.8 gate, plus source/PDB identity, capture-enablement state, and contributing
   record counts. No other tool in the matrix tells a caller when not to believe it.
3. **Both capture stacks, and both runtimes.** EventPipe and ETW, modern .NET and
   .NET Framework - which is what makes wall clock, multi-process scope, native
   frames, kernel disk I/O, and process lifecycle expressible at all.
4. **The last mile to a source line.** `lines` and `heatmap` over extracted PDBs.
5. **Comparison as a first-class operation.** Normalized `diff` for direct traces or
   exact benchmark/parameter manifest pairs, and `batch` across every case.
6. **A scoping vocabulary.** Auto-scope to the busiest tree, exact `--pid` sets with
   descendant control, `--root`, `--benchmark`, `--activity`, `--time`.
7. **Engineering rigor aimed at agents.** Deterministic rounding, bounded output,
   frozen-oracle parity, an eval harness, and a shipped skill with drift checks.

## What to learn, and where it goes

| Learning | From | Roadmap item |
|---|---|---|
| DATAS server-GC tuning analysis | pvanalyze | VC1 |
| Point-in-time snapshot around a spike | pvanalyze, IDE profilers | VC2 |
| Per-method temporal buckets | pvanalyze | VC3 |
| Retention / path-to-root | PerfView, dotMemory, `dotnet-gcdump` | VC5 |
| Symbol merge so a trace resolves off-machine | PerfView | SC9 |
| Group-by module/namespace, hot-path auto-follow, LOH breakout | pvanalyze, PerfView | VC8 / VN3 drill-family review |
| Wall-clock presentation a non-expert reads correctly | dotTrace, IDE profilers | VN2 output contract |
| Statistical honesty when comparing two runs | BenchmarkDotNet, continuous profilers | existing `diff`; keep normalized |

## What we deliberately do not copy

- **PerfView's breadth.** Parity is an explicit non-goal; every addition has to
  clear the surface and budget gates in [design.md](design.md).
- **A GUI, or a viewer of our own.** Export to speedscope and Perfetto instead.
- **Continuous production profiling.** A different data model, a different
  deployment story, and not what an offline single-trace analyzer is for.
- **pvanalyze's "no agent scaffolding" bet.** It is a reasonable bet for frontier
  models; filtrace's is that a machine-checkable contract - envelope, schema,
  next steps, budget - pays off across model generations and lets cheaper models
  succeed. Both bets can be right for different audiences.

## Ecosystem opportunities

- **A shared DATAS parser.** DATAS payload parsing is fiddly and versioned; one
  small MIT component, or a clean port in either direction, avoids two drifting
  implementations.
- **A common JSON envelope.** If two analyzers converged on a minimal
  `{ schemaVersion, warnings, hints, context, result }` shape, an agent could route by
  capability rather than by output format.
- **Complementary routing rather than duplication.** A DATAS or snapshot question
  to pvanalyze; a wall-clock, multi-process, source-line, capture, or diff question
  to filtrace. Aligning JSON and hint conventions would make that routing seamless.
- **Cross-pollinated fixtures.** Our oracle and parity fixtures and a DATAS-enabled
  capture are exactly the traces each project needs to test what it borrows.

## Keeping this page honest

Update it when filtrace's surface changes, when a compared tool ships something in
the matrix, or when a claim here is contradicted by measurement. The filtrace column
is verifiable: verb count from `tools/Test-CliHelp.ps1`, tool count and schema size
from `tools/Test-McpServer.ps1`. Other columns are not, so keep those claims at the
level of documented capability and date the page when you touch it.
