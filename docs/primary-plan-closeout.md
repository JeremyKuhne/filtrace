# Primary Plan Closeout

**Status:** Final for PP12. **Date:** 2026-09-21.

This record closes the coordinated FastTrace and Filtrace primary plan at its
measured source-integrated x64 boundary. It preserves negative and inconclusive
results rather than turning them into another evaluation or implementation queue.
No measured execution is active.

## Decision Summary

Filtrace has a repeatable local capture-to-verification workflow and two shipped,
profile-guided consumer improvements. FastTrace is a credible source-integrated
TraceEvent replacement for the tested Windows x64 and Linux x64 scenarios, including
explicit Native AOT deployments. It is not the published Filtrace default: the
package-backed product remains on TraceEvent 3.2.6.

The replacement is mixed rather than universal. FastTrace wins several JIT replay
rows and gives large Native AOT startup gains on small inputs, but some cold rows are
near parity or slower, large-input Native AOT throughput can lose, and resident
memory can be higher. Windows native symbols require an explicitly supplied DIA
runtime. Integrated ARM64 and macOS execution, package-only FastTrace adoption, and
default DIA distribution are outside the demonstrated boundary.

The agent evidence also closes with a bounded result. EP1 identified explicit
process scope as the stable failure mechanism, but was strongly order-sensitive.
EP2 v1 stopped inconclusive after a post-exposure treatment-integrity failure. A
separately authorized completion filled only the five unrun answer slots: both skill
variants passed 12/12 answers with zero false confidence and all 12 pairs tied.
Candidate treatment delivery was verified in 11/12 answers versus 12/12 for the
baseline. Keep the compact scope guidance because it is small and caused no observed
regression, not because EP2 proved an answer-quality advantage.

## Measured Development Path

| Stage | Retained evidence | Boundary |
| --- | --- | --- |
| Capture | Windows ETW and cross-platform EventPipe paths are operational. Seven short-command pairs verified the effective 1 ms interval, invocation roots, cleanup, and a bounded perturbation observation. | ETW collection is Windows-only and elevated. Thin captures do not guarantee stable method rankings. |
| Accept | Filtrace records provider state, event counts, exact process scope, symbol/source quality, and evidence-density warnings before attribution. EP1 exposed automatic-scope acceptance as a real failure. | Passing format checks do not establish adequate symbols, samples, or requested scope. |
| Attribute | Fixed-analyzer profiles placed repeated frame-label work at 43.52% and 48.42% of recorded CPU samples in the two 1M arms, with roughly 3.68 GB of sampled allocation before the first consumer change. | CPU records are samples, sampled allocation is not exact allocation, and neither proves retained-memory ownership. |
| Change | Filtrace PR #124 reused repeated frame labels. PR #126 replaced per-caller wrappers with indexed stack traversal through existing APIs. FastTrace PR #298 retained GC reconstruction and classic replay improvements while preserving rejected probes. | Shared consumer wins are not credited as FastTrace-only library wins. No additive FastTrace API was required. |
| Verify | Frame-label reuse reduced 1M warm ranking from roughly six seconds to 3.3-3.5 seconds in both engine builds and reduced sampled allocation about 76%. Indexed traversal then improved that row by another 9.5% with TraceEvent and 12.0% with FastTrace in three-pair comparisons. Ordered outputs remained unchanged. | The measurements are descriptive and workload-specific. Memory losses and neutral/regressive rows remain part of the decision. |

The final scenario-by-scenario replacement decision is in the
[FastTrace replacement assessment](https://github.com/JeremyKuhne/fasttrace/blob/main/docs/filtrace-replacement-assessment.md).
Source-build and Native AOT reproduction commands are in
[source-build.md](source-build.md); the indexed consumer measurements are in
[stack-traversal-experiment.md](stack-traversal-experiment.md).

## Agent Results

### EP1 Natural Adoption

| Measure | CLI plus discovered skill | CLI only |
| --- | ---: | ---: |
| Strict answers | 5/10 | 3/10 |
| False-confidence scope errors | 5/10 | 6/10 |
| Successful analysis calls | 13 | 12 |
| Observed result tokens | 42,848 | 10,505 |
| Counted host credits | 10 | 10 |

All 20 counted records validated. Three infrastructure-invalid attempts consumed
three additional credits outside the behavioral denominator. First-position chats
passed 2/10 versus 6/10 in second position, and the skill moved from 0/5 in the first
half to 5/5 in the second. That prevents a winner claim. The public retained summary
does not promote aggregate EP1 elapsed time, so it remains unavailable here rather
than being reconstructed. The one preflight pair took 32.704 seconds with the skill
and 29.957 seconds with CLI only; it proved protocol readiness, not efficacy.

### EP2 Complete Answers

| Measure | Exact EP1 baseline | Compact scope contract |
| --- | ---: | ---: |
| Strict answers | 12/12 | 12/12 |
| False-confidence cases | 0 | 0 |
| Verified treatment delivery | 12/12 | 11/12 |
| Analysis / help calls | 22 / 12 | 19 / 12 |
| Observed result tokens | 73,444 | 80,094 |
| Host output tokens | 41,368 | 36,723 |
| Evaluator wall time | 799.300 s | 767.720 s |
| Host credits | 12 | 12 |

All 12 matched pairs were tie-passes. These execution and cost totals combine the
original stopped run with the separately authorized continuation, so they are
descriptive rather than causal. EP2 v1 remains formally inconclusive, and the
unverified candidate treatment cannot be credited with its passing answer. The
result shows no observed answer-quality advantage on these two tasks; it does not
establish general equivalence.

## Choosing An Interface

| Need | Preferred path | Reason and limit |
| --- | --- | --- |
| Repository-local agent investigation, capture orchestration, or command-line reproducibility | CLI plus the shipped skill | This is the primary workflow and carries the full capture/orient/rank/drill/compare guidance. Verify named-process scope explicitly. The skill has no demonstrated general quality or token advantage. |
| Structured analysis inside an MCP-capable client | MCP `trace_*` tools | Use the typed operations when direct tool calls fit the client and capture already exists. Client handling of duplicated or large structured results is host-specific. ETW capture, cache housekeeping, and all-process widening remain CLI-only. |
| Cross-platform EventPipe capture | `dotnet-trace`, then Filtrace analysis | Filtrace's collector is ETW-only. The handoff is explicit rather than pretending ETW collection works cross-platform. |
| Package-only deployment, untested integrated platforms, or broader TraceEvent compatibility | Current TraceEvent-backed Filtrace | FastTrace adoption is source-only in this campaign and is namespace-adjusted, not binary assembly-identity compatible. |
| Exact heap retention, PMC, DATAS, unsupported provider breadth, or a profiler cross-check | An established specialist tool | Filtrace should hand off honestly when it lacks the required evidence. A handoff is not a claim that Filtrace performed the analysis. |

## Closed Scope

The final S00-S11 disposition is:

- S00-S10 are accepted or narrowed only at the measured boundaries in the
  replacement assessment.
- S11, the general claim of reaching improvements with less agent effort, is
  declined. The evidence demonstrates a useful workflow and shipped improvements,
  not lower total effort.
- A matched skill-mediated full-development-loop pair is not required for the
  library replacement decision and is not scheduled merely to improve the label.
- No EP2 rerun, CLI/MCP scope-salience change, speculative FastTrace API work, or
  broader evaluator campaign follows automatically.
- No package, release tag, visibility, credential, or publishing action is part of
  this closeout.

The EP2 completion record has SHA-256
`ff6ef14db28d6898d1f6f03e020682c566718220cb0df520f406b4a43ea14c68`;
its summary is
`479fba15499e3c79b0675d15317dc3dc492dfaa448bd0a2c27ccf93ff50572a3`,
and its report is
`08e5b4d1b9fa2a9713d635423cc09784d2b51f8af11139d42ac7e171f80a870c`.
The raw private evidence remains outside Git.

Future work starts from a new reproduced product question and current revisions. It
does not resume this plan at the next numbered item.
