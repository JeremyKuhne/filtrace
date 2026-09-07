# Indexed Stack Traversal Experiment

**Status:** Retained consumer change, pending publication. **Measured:** 2026-09-06/07.

This comparison-first iteration follows the
[primary-plan results](https://github.com/JeremyKuhne/fasttrace/blob/main/docs/filtrace-comparison-2026-09-06.md).
It changes the same Filtrace reader loop in both TraceEvent 3.2.6 and the local
FastTrace source integration. No new API or library-specific branch is needed.

## Evidence And Change

The cold 24-case batch profile did not establish that conversion caused the observed
wall-time loss. Scoped CPU samples were 2,600 for TraceEvent and 2,598 for FastTrace.
ETLX open/conversion accounted for 1,015 versus 593 samples, while `ReadCore`
accounted for 835 versus 1,334. Frame-name resolution was only 49%/45%, so native
leaf attribution remains limited. FastTrace allocated less overall, but its
`TraceCallStacks.get_Item` bucket alone accounted for 95.7 MB of sampled allocation.

The reader now walks `CallStackIndex` values with existing `Caller` and
`CodeAddressIndex` APIs instead of constructing a `TraceCallStack` for every caller.
The initial event stack lookup/null check remains. Every frame still reads its current
method/module name, reuses equal formatted labels, resolves source, and contributes
to quality observations. Frame order, scopes, sample weights, and output are unchanged.

## Before And After

Windows x64, .NET 10.0.11, SDK 10.0.400, Release/JIT. Each row has three alternating
before/after pairs, one child launch per arm. Medians are descriptive; the paired
ratio is the median of per-pair ratios, not the ratio of the two medians. Warm ranking
excludes conversion but includes process startup and output. Cold batch uses 24
distinct file copies of one trace; the OS file cache is not flushed.

| Library | Scenario | Before ms | After ms | Paired After/Before | Pair Range |
| --- | --- | ---: | ---: | ---: | --- |
| TraceEvent | Warm rank, 10K | 309.92 | 312.37 | 0.997 | 0.990-1.038 |
| TraceEvent | Warm rank, 1M | 3,351.38 | 3,040.34 | 0.905 | 0.904-0.907 |
| TraceEvent | Cold batch, 24 x 10K | 2,579.05 | 2,277.51 | 0.912 | 0.770-1.075 |
| FastTrace | Warm rank, 10K | 262.04 | 256.60 | 1.008 | 0.962-2.374 |
| FastTrace | Warm rank, 1M | 3,281.11 | 2,881.15 | 0.880 | 0.865-0.880 |
| FastTrace | Cold batch, 24 x 10K | 2,591.54 | 2,250.37 | 0.868 | 0.837-0.916 |

The 10K candidate row contains a large outlier and is inconclusive, not a small-input
speedup. The entire JSON result is identical before/after for 10K and 1M rank and
all 24 batch cases in both libraries. All cases have nonzero samples.

Separate allocation/GC captures use the same fixed analyzer for both arms. Sampled
allocation totals fell from 423,479,704 to 324,227,856 bytes with TraceEvent and from
292,684,216 to 193,516,064 with FastTrace. The formerly second-ranked FastTrace
wrapper bucket is absent from the after top-200 allocation ranking. This is not an
exact zero-allocation proof, and allocation ticks do not measure retained heap.

Process memory did not fall on the 1M row: median peak working set rose from
495.10 to 519.82 MiB with TraceEvent and 629.83 to 652.39 MiB with FastTrace; sampled
private memory rose from 460.52 to 487.20 MiB and 459.61 to 484.24 MiB respectively.
No cause is inferred from those counters alone. Earlier model-lifecycle checks
established collection of released models, not immediate page return to the OS.

## Fair Updated Library Comparison

With the indexed walk applied to both arms, a separate three-pair comparison gives:

| Scenario | TraceEvent ms | FastTrace ms | Paired FastTrace/TraceEvent |
| --- | ---: | ---: | ---: |
| Warm rank, 10K | 301.53 | 249.74 | 0.826 |
| Warm rank, 1M | 3,037.04 | 2,899.52 | 0.958 |
| Cold batch, 24 x 10K | 2,221.60 | 2,271.59 | 1.017 |

The optimization helps each consumer but does not eliminate every library tradeoff.
FastTrace's cold batch remains slightly slower in this sample. Its 1M working set
is 652.40 versus 520.02 MiB, while sampled private memory is similar at 484.22 versus
486.87 MiB. These rows are not a general replacement recommendation.

## Validation And Reproduction

- 58 focused reader/source/scope tests passed on each library.
- Full TraceEvent consumer Debug/Release suites: 1,683 passed plus two expected
  environment skips each; all 27 canonical tasks, CLI help, and MCP contracts pass.
- After integrating the compact-skill parent, 37 reader/source tests pass.
- Independent review confirmed root normalization, ordering, address lookup, and
  log ownership are equivalent on the existing public APIs.

Evidence root: `N:\perf-corpora\fasttrace-filtrace-primary-plan\validation\evidence`.
The retained experiment is `pp08-index-stack-walk-aa26d90aec7d4faabe8d7819434830a0`;
its `experiment.json` contains source/binary hashes and every launch. `summary.json`
contains paired ranges and complete semantic hashes. Local reproduction recipes,
before/after outputs, and separate allocation captures are retained there.
The first incorrectly selected TraceEvent fallback setup is archived and excluded.

Before reader SHA-256: `BE3134405DD0AE329C0B2427ED5A8E8B12A1B2095140BED5BCCEF500E96ABEC2`.
After reader: `CB63EEEE782C0C316B7D9C3EF994B88EAD723FCADDAB1BBF75FBD83B26414A3E`.
FastTrace source is `355617a9b12c67810ea6897a6465a24ca2c60ab0`, whose tree equals
merged `73b32fe690491bc3d3ba05080ae2d5eb59ba01cb`; its engine binary is unchanged
between arms. TraceEvent stays at 3.2.6. The cold-profile baseline is
`cold-manifest-profiles-992f68fb39ca4f61ac3c5ab2a73b28d1`.

No captured trace or local integration dependency is published with this change.