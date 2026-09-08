---
name: filtrace
description: Analyze .NET CPU, allocation, exception, GC, JIT, and wall-clock (thread-time) data in .nettrace, .etl, and speedscope files with the filtrace CLI or MCP server. Use when a user asks where time or allocation volume goes in a trace or benchmark, which method or source line is hot, why a run regressed against a baseline, what a captured .nettrace / .etl contains, or to rank / drill / diff / export a profile - including profiling .NET Framework (net481) via ETW, where an EventPipe ranking would mislead. Also covers capturing the trace first - choosing EventPipe vs ETW, elevation, and the recording tool (dotnet-trace, BenchmarkDotNet, PerfView, wpr).
license: MIT
compatibility: Pairs with the filtrace MCP server (the KlutzyNinja.Filtrace.Mcp package, run via `dnx`) for in-agent tool calls; otherwise shells out to the filtrace CLI (the KlutzyNinja.Filtrace global tool). Both heads share the analysis core; capture, cache operations, and all-process ETW widening are CLI-only.
metadata:
   portability: repo-specific
   applicability: tool-shipped
   binding: optional-overlay
   risk: local-write
   maturity: stable
   requires: none
   related: performance-testing
---

# Analyzing .NET traces with filtrace

If `overlay.md` exists beside this file, read it before acting; it contains
consumer-specific bindings. This core remains usable without it.

filtrace analyzes `.nettrace`, `.etl`, and speedscope captures through a .NET 10 CLI
or eighteen `trace_*` MCP tools. Use compact JSON and load linked detail only when
the selected question needs it.

## Route from the question

| Question | First analysis |
|---|---|
| What CPU leaf is hot? | `rank --metric cpu` self; inclusive finds responsibility |
| What allocates or throws? | `rank --metric alloc|exceptions` |
| Are GC pauses or JIT compilation costly? | `report --kind gc|jit`; MCP `trace_gc` / `trace_jit` |
| Why is elapsed high but CPU low? | ETW threadtime, contention/wait, lifecycle, or thread-pool report |
| Is one captured operation slow? | `rank --metric activity`, then CPU `--activity` scope; [scope guidance](references/guide.md#scope-and-symbols) |
| Which files drive physical disk pressure? | `report --kind diskio` on ETW with disk events; MCP `trace_diskio` |
| When was the spike? | `timeline` buckets, then `rank --time <start>,<end>` |
| What happened near a known millisecond? | one `timeline` snapshot call below |
| Did CPU runs differ? | `diff`; `batch` for one bounded manifest query with any supported metric |

For non-CPU comparisons, compare equivalent scoped rankings or reports; `diff`
does not compare allocation, exception, or other non-CPU metrics.

For a point-in-time question, answer in one analysis call:

```pwsh
filtrace timeline <trace> --mode snapshot --at <center-ms> --window <half-window-ms> --format json
```

For MCP call `trace_timeline` with `path`, `mode: "snapshot"`, `at`, and `window`.
`window` requests a duration on each side; trace edges clip the resolved interval.
Report exact `fromMs`/`toMs`, top resolved CPU leaf
from `snapshot.cpu.methods[0]`, total throws and most frequent type from
`snapshot.exceptions.exceptionCount`/`types[0]`, and total raw events from
`snapshot.events.eventCount`. A missing first row means no resolved row, not zero
activity. Surface envelope `warnings` and snapshot truncation/incomplete flags.

## Work bounded and honestly

1. Use the supplied trace and narrowest answering command. Except for the self-orienting
   snapshot, start with `info` / `trace_info`: check format, analysis, capture status,
   event count, and frame-name quality.
2. Preserve scope. ETW auto-selects the busiest process tree; inspect `processes` when
   identity matters and prefer exact pid over a common name. Carry process, children,
   root, benchmark, activity, and time scope into follow-ups.
3. Request bounded JSON (`--format json`; MCP is structured). Read `warnings` first;
   `hints` are candidates. Empty can mean wrong scope, missing providers, weak symbols,
   or an open Start/Stop operation.
4. `callers`, `source`, and `tree` are CPU-only. Refine non-CPU metrics by their own
   self/inclusive, root, or time scope. Report scope, metric, measure, contributing
   count, and warnings; CPU is sampled and inclusive rows overlap.

Do not capture, elevate, use network symbols, clean caches, or export without
authorization. Never infer provider enablement from format support, call sampled CPU
latency, call allocation volume retained memory, or hide evidence limitations.

## Accept evidence before acting

1. **Establish units.** Record the analyzer version/capability, CPU record count,
   sampling semantics, known recorded interval provenance, and clock/operation
   boundary. In schema 17, inspect `context.unit` and `context.cpuSampling` on CPU
   analyses, or `result.cpuSampling` from `info`. Require `timeWeightsEstablished`
   before interpreting CPU weights as milliseconds; `weightUnit: samples` remains
   sample counts. For older fixed-unit or unknown-interval output, report
   qualified sample counts/weights; never manufacture CPU time as wall clock times
   sample share.
2. **Bind both comparison arms.** Retain canonical paths and SHA-256 identities for
   the analyzer executable, core assembly, deps file, source revision, subject, trace,
   and generated child symbols, recording embedded or absent artifacts explicitly.
   Reject or label a substituted analyzer.
   Require successful setup, nonzero cases and result rows, equivalent workload phases,
   and reconciled positive process/iteration/operation counts and units. A per-operation
   denominator must count completed workload-phase operations, not setup or harness work.
3. **Prove positive source attribution.** Embedded-PDB composition is not a hit.
   Require the relevant module among nonzero exact PDB matches and positive mapped
   sampled frames/methods before claiming source resolution. If symbols remain
   unavailable, continue with method-level evidence or an already-approved profiler
   against the same capture, state the limit, and do not recapture merely to get a
   cleaner answer.
4. **Close the expense readout.** Give one exclusive CPU/count denominator and its
   unknown/unlisted remainder. Report allocation bytes/types, collector/GC work, and
   result/intermediate/cache ownership separately. Matching and growth explain causes;
   inclusive causal rows overlap and must not be added to exclusive totals.
5. **Keep capture control distinct from the subject.** Accept structured stdout only
   when that analyzer version separates the capture result from child streams; never
   scrape a final JSON-looking line from mixed output. Current `collect --format json`
   emits one stdout document and labels child streams on stderr; preserve both and
   parse all stdout. Descendant output may be truncated after the post-exit drain grace.
   Record capture success and subject exit separately. Retain failed/partial attempts plus the terminal, process,
   and session owner before launch or handoff; do not widen ambiguous PID scope or stop
   resources that the run does not own.
6. **Make one decisive move, then stop or reroute.** Start with a read-only query and
   the smallest drill that can falsify one hypothesis. Compare equivalent work with
   uninstrumented timing kept separate and small/large/sustained controls as relevant;
   decide keep, reject, or inconclusive. Preserve a compact resume record: identities,
   accepted denominator and scope, failed attempts, next hypothesis, blocker, and
   standing authorization. Label unmet evidence as blocked and exhausted opportunity as
   diminishing returns, then move to another eligible question; do not auto-recapture or
   build a general harness. Judge the workflow by correct decisions and evidence use,
   not claimed token savings or entrypoint size.

## Load detail only when selected

- Capture, EventPipe/ETW, or short command: [capture guidance](references/guide.md#getting-a-trace-to-analyze).
- Symbols, source, scope, or quality: [scope and symbols](references/guide.md#scope-and-symbols)
   and [evidence rules](references/guide.md#interpret-and-report-the-evidence).
- Advanced comparisons, exports, cache, or full catalogs:
   [workflow and commands](references/guide.md#the-workflow-orient---rank---drill---compare).
- Empty, surprising, low-quality, platform, or capture result:
   [trap catalog](references/guide.md#trap-catalog).

The detailed guide and scripts are packaged with this entrypoint.
