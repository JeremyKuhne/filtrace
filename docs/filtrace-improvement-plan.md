# filtrace capability initiative history

**Status:** Completed initiatives and superseded planning record

This page records the outcome of the capability plan derived from
[pvanalyze-vs-filtrace.md](pvanalyze-vs-filtrace.md). It is no longer a living
backlog. All unshipped capability and surface work is consolidated in
[vnext-improvement-plan.md](vnext-improvement-plan.md); Git history retains the
original implementation recipes.

## Initiative outcome

| ID | Initiative | Outcome |
|---|---|---|
| P1 | DATAS server-GC tuning analysis | Moved to v.next as VC1; not implemented. |
| P2 | Multi-lane timeline correlation | Shipped as `timeline` / `trace_timeline`. |
| P3 | Point-in-time snapshot | Moved to v.next as VC2, eval-gated as a timeline mode. |
| P4 | Event payload / PID / TID filtering | Shipped by extending `events` / `trace_query_events`. |
| P5 | Bidirectional caller/callee view | Shipped by extending `callers` / `trace_callers`. |
| P6 | Per-method temporal buckets | Moved to v.next as VC3; not implemented. |

The old plan expected each new capability to add a CLI verb and MCP tool. That is
no longer the default. The live MCP surface is near its permanent schema budget,
and v.next is evaluating consolidated report/source families and transport options.
New analysis must fit the selected surface rather than create a tool automatically.

## Shipped: multi-lane timeline

The timeline correlates GC, CPU, exception, allocation, and JIT activity in bounded
time buckets over `.nettrace` and `.etl` inputs. It supports:

- lane selection;
- a bounded bucket count;
- optional time-window scope;
- automatic or explicit process-tree scope for ETW;
- a hottest resolved managed method per CPU bucket;
- text sparklines and deterministic JSON;
- a next step that routes the busy window into a scoped ranking.

The implementation reads GC start times directly from TraceEvent's runtime model,
walks CPU stacks to the innermost resolved managed frame, and applies one process
scope across every lane. It deliberately rejects speedscope because aggregate
speedscope profiles do not retain the chronology required for cross-lane buckets.

## Shipped: raw-event filtering

`events` and `trace_query_events` now support:

- provider/event-name substring matching;
- case-insensitive payload-value substring matching;
- process-id and thread-id filters;
- skip/take paging;
- bounded per-event payload text;
- both EventPipe `.nettrace` and Windows ETW `.etl` event streams.

Pages and payloads are clamped so one query cannot consume unbounded memory or
response context. A next-page hint carries the exact skip value when matches remain.

## Shipped: caller/callee focus view

`callers --callees` and `trace_callers(callees=true)` return both directions around
a CPU focus frame:

- immediate callers, weighted by the target-inclusive samples they contribute;
- immediate callees, including a `<self>` pseudo-callee for the focus frame's own
  execution;
- target and scope totals, percentages, and contributing-record count;
- frame-match diagnostics and scope-preserving next steps.

The view remains CPU-only because the public call drill is defined over the CPU
stack source. Non-CPU rankings refine self/inclusive, root, process, activity, or
time scope instead of silently crossing into CPU evidence.

## Unshipped candidates

### DATAS

DATAS explains modern server-GC Dynamic Adaptation To Application Sizes decisions:
heap-count transitions, tuning samples, budgets, throughput cost, waits, and gen-2
backstop behavior. It remains the highest-value missing structured report. Its
current design, capture fixture, parity, licensing, and proposed report-family
surface are tracked as VC1 in
[vnext-improvement-plan.md](vnext-improvement-plan.md#vc1---datas-server-gc-tuning).

### Point-in-time snapshot

A snapshot would summarize all activity around one timestamp. Timeline and scoped
rank now provide the component operations, so v.next first measures whether a
single snapshot mode reduces calls or materially improves evidence. It is VC2 in
[vnext-improvement-plan.md](vnext-improvement-plan.md#vc2---point-in-time-snapshot).

### Per-frame temporal buckets

A small histogram per ranked frame could reveal bursty work without a second query,
but repeats time-series data on the hottest output path. It remains an explicit,
bounded experiment under VC3 in
[vnext-improvement-plan.md](vnext-improvement-plan.md#vc3---per-frame-temporal-buckets).

## Architecture lessons retained

1. **Add analysis in Core first.** Providers and result records belong in
   `Filtrace.Core`; the CLI and MCP heads adapt the same result.
2. **Prefer extending a compatible operation.** New stack metrics belong behind
   `rank`; report and temporal capabilities should use the v.next report/timeline
   discriminators when their schemas remain comprehensible.
3. **Bound every repeated shape.** Rows, buckets, payload values, warnings, and
   per-case output require structural limits and worst-case token tests.
4. **Keep format support distinct from capture enablement.** A file extension does
   not prove a provider was enabled or that zero events means no work occurred.
5. **Return evidence and routing together.** Contributing-record counts,
   frame/source quality, warnings, and next steps are part of the analysis contract.
6. **Eval surface changes.** Tool descriptions, defaults, consolidation, and output
   detail can change agent behavior even when numeric semantics stay constant.
7. **Carry third-party provenance.** A future DATAS parser port must retain the
   upstream MIT notice and source provenance in addition to project-level notices.

## Current roadmap

The canonical roadmap is [vnext-improvement-plan.md](vnext-improvement-plan.md).
This file remains only to explain the P1-P6 references in historical commits and
comparison documents.
