# filtrace roadmap

**Planning authority:** This is Filtrace's one current work queue. The
[design](design.md) holds enduring product principles and investigation
lessons; operational guides and measured decision records are not additional
roadmaps. An issue records implementation detail, not a separate priority.

**Last verified:** 2026-09-30 against merged `main` at `ae608a0`, the
repository's package pin, and the four open Filtrace issues (#12, #57, #92,
and #133). The docs-only branch does not change product behavior.

**Current status:** No implementation campaign, agent comparison, CI-time
optimization, package release, or multi-operation CLI feature is selected.
PR #166 is merged. A new item becomes active only when an explicit user
scenario, ownership, and verification gate are agreed; a possible
implementation is not an authorization to start one.

## Work awaiting a concrete trigger

| Item | When to reconsider | Bounded next decision |
| --- | --- | --- |
| [#12](https://github.com/JeremyKuhne/filtrace/issues/12): Native AOT / self-contained packaging | A compatible default trace-reading dependency is available, or a separately approved distribution scenario selects the source-integrated alternative. | The current default is framework-dependent TraceEvent **3.2.6**, not AOT/trim-clean. The issue body refers to an older 3.2.3 pin. Keep the default until complete CLI/MCP, platform, native-support-file, and package-shape validation; the [source-build guide](source-build.md) covers bounded x64 evaluation, not publication permission. |
| [#57](https://github.com/JeremyKuhne/filtrace/issues/57): BenchmarkDotNet diagnoser artifact identity | Upstream exposes a stable case-to-artifact contract, or a reproduced log/filename change defeats the current parser. | Test the current pinned BenchmarkDotNet producer and a live parameterized capture. Consume structured identity where it actually identifies the raw/derived trace; keep ambiguity fail-closed until a complete replacement exists. Do not infer artifact paths from a JSON exporter that lacks them. |
| [#92](https://github.com/JeremyKuhne/filtrace/issues/92): DATAS GC tuning | A captured modern Server-GC workload asks why its heap count/budget adapts. | Extend the existing GC report and `trace_gc`, not the command/tool list. Require an exact binary-payload oracle, bounded transition evidence, and an explicit unsupported/no-events distinction; do not imply DATAS applies to Workstation GC or .NET Framework. |
| [#133](https://github.com/JeremyKuhne/filtrace/issues/133): manifest symbol input identity | Manifest-backed MCP or mediated-CLI agent evaluation with symbol directories is scheduled. | Bind the resolved, bounded symbol-directory inventory into the evaluator's input-closure hash and reject escapes, links, and missing files. The frozen EP1 strict CLI arms reject manifests and are not affected today. |
| VN5: preview-alias removal | A release/migration policy is explicitly approved. | Publish the old-to-new CLI migration map, verify help/docs/eval behavior, and remove hidden aliases only under that policy. Time since the preview release is not a trigger. The 16 canonical commands and 12 hidden aliases remain the current contract. |
| SC9: portable native-symbol evidence | A real consumer needs committed native-symbol fixtures or off-machine native resolution. | Determine a capture/merge step that preserves binary/PDB identity, then test cross-machine resolution. Current CI instead captures and validates native symbols on the same machine; do not label its local pass as portable evidence. |
| SC10: manifest-addressed lifecycle | A reproduced agent task starting from a command manifest loses exact invocation scope or wastes calls selecting PIDs. | Extend the existing `lifecycle` command and `trace_lifecycle` with an exact case reference, reject unsuitable cases rather than broaden scope, and compare answer quality/calls against manual PID selection. Nothing schedules an extra MCP tool. |

Other named capabilities and LP/TE parallelism or dependency proposals from
the closed primary plan are **not** standing backlog items. Heap retention,
net surviving heap, PMC capture, physical trim, and short-command observer
effects remain capability limits or handoff questions in the
[design](design.md#performance-evidence-and-dependency-boundaries) and
[workflow](workflow.md), not implementation commitments. A selected user
scenario may bring one back with fresh evidence and a new entry here.

## Decision and evidence index

| Completed decision | Where the lasting rule and necessary detail live |
| --- | --- |
| Keep typed MCP results and intent-bearing operations; the smaller text-only transport and nested report-tool union did not improve agent outcomes. CLI stays at 16 canonical commands and 12 hidden migration aliases. | [Agent/interface findings](design.md#agent-evidence-and-interface-decisions), [help contract](../tools/Test-CliHelp.ps1), and Git history for the VN1-VN4 experiments. |
| PP12 closed at a mixed source-integrated FastTrace x64 boundary; the default remains package-backed TraceEvent. EP1 was order-sensitive, and the EP2 answer completion showed no measured advantage. Neither establishes a lower-effort full development loop. | [Design findings](design.md#agent-evidence-and-interface-decisions), [dependency limits](design.md#performance-evidence-and-dependency-boundaries), [source-build instructions](source-build.md), and PR #138. |
| Frame-label reuse and indexed traversal improved a measured consumer path but did not establish a general parallelism or retained-memory win. No LP-1 through LP-5 or TE-P1 through TE-P5 queue follows automatically. | [Performance evidence](design.md#performance-evidence-and-dependency-boundaries), [benchmark procedures](../benchmarks/README.md), and PRs #124 and #126. |
| Repository-local CLI/MCP/skill activation replaced a broad review-era state system with fixed resource ownership and resumable restore. | [Activation boundary](design.md#performance-evidence-and-dependency-boundaries), [current recovery guide](local-testing.md), and PR #120. |
| Capture provenance, scope replay, ETLX identity, and CPU provider-specific warnings are part of accepted evidence, not optional descriptions. | [Evidence limits](design.md#capture-identity-and-evidence-limits), [workflow](workflow.md), [traps](traps.md), and merged PRs #121, #128, #163, and #166. |
| A one-process shared-store `info`+`rank` CLI prototype saved time with exact output parity. MCP already reuses a server-lifetime store; no new public CLI interface was approved. | [Measured alternatives and agent tradeoffs](multi-operation-query-reuse.md); a new CLI-only task must justify the extra discovery/grammar cost. |
| Hosted CI runner time improved in PRs #164 and #165 without dropping validation. Further CI-time optimization is paused; no speculative buffer or rundown edit is selected. | Those PRs and their measurements remain in Git history; reopen only for a renewed CI budget or fidelity-safe scenario. |

## Entry rule

For a newly selected task, record the user question, current behavior, a
representative small/common and stress case, exact source/input identities,
the correctness and quality oracle, and the absolute time, memory, and agent
cost to improve. Update this one roadmap with its trigger, gate, and
disposition. A separate investigation may retain raw profiles, but it does
not become a parallel product plan. Do not turn historical percentage
thresholds or a candidate implementation into an automatic release, PR, or
dependency change.
