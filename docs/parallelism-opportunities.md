# Track D performance investigation plan

**Status:** Measurement plan. No Track D optimization has shipped.

This plan turns the performance and parallelism hypotheses in
[roadmap.md](roadmap.md#track-d---performance-and-parallelism) into repeatable
experiments. It deliberately uses two complementary forms of evidence:

1. **BenchmarkDotNet** measures an isolated Core operation with stable synthetic or
   prepared inputs. It answers whether the implementation itself became faster and
   what managed allocation it added.
2. **Filtrace profiles the filtrace CLI** while it runs the corresponding real
   command. It answers whether the isolated win survives process startup, ETLX
   loading, symbol work, rendering, and the rest of the analysis pipeline, and
   whether the expected method or source line actually moved.

Neither substitutes for the other. A microbenchmark win that does not reduce a CLI
scenario is not a product win; a faster CLI run whose targeted frame is unchanged is
noise or an unrelated effect.

## Outcomes

Track D is complete when LP-1 through LP-4 have each produced a retained or rejected
experiment with reconstructable evidence, and LP-5 has either obtained the upstream
thread-safety contract it needs or remains explicitly blocked. Each retained change
must satisfy all of these:

- deterministic output and numeric parity are unchanged;
- the target BenchmarkDotNet row improves outside normal run-to-run noise;
- allocations and peak process memory stay within the item-specific budget;
- a real CLI scenario improves without regressing the small or common case;
- a filtrace profile shows the targeted method or phase shrank;
- the experiment ledger records rejected variants and the chosen threshold or degree
  of parallelism.

## Measurement model

### Layer A: Core microbenchmarks

The product harness is
[`benchmarks/Filtrace.Benchmarks`](../benchmarks/Filtrace.Benchmarks). Benchmark
classes use `[MemoryDiagnoser]`, put input construction in setup, and return a result
derived from the work. Retained measurements use the default BenchmarkDotNet job;
`--job short` is only a smoke check.

Run one class into a unique artifact directory:

```pwsh
$run = "artifacts/perf/LP-2/baseline-$(Get-Date -Format yyyyMMdd-HHmmss)"
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- `
  --filter '*FoldingAggregatorBenchmarks*' `
  --artifacts "$run/bdn" `
  --exporters json github
```

The analyzer targets net10.0 only. Do not add a net481 job because filtrace can read
net481 traces; the net481 HotLoopBench project generates input fixtures and is not a
product runtime target.

All benchmark classes and orchestration used by an LP experiment must already exist
in one merged **harness commit** before its baseline is selected. Record that commit
as `harnessCommit`; build the baseline from that commit and the candidate from a
branch based on it. Fail the run when `benchmarks/` or the orchestration script differs
between arms. If a later investigation needs a new scenario, merge that measurement-
only change first and choose a new baseline. Never compare a candidate-only benchmark
with an older checkout that does not contain the same harness.

### Layer B: end-to-end CLI benchmarks

Three BenchmarkDotNet classes own this layer:

- `CliWarmBenchmarks` starts the checkout's built `filtrace.exe` against prepared
  inputs whose ETLX caches already exist. This measures process startup plus trace
  load, analysis, JSON rendering, and exit.
- `CliColdConversionBenchmarks` gives each iteration a fresh trace copy with no ETLX,
  invokes the command once, and deletes the trace and generated ETLX in cleanup. One
  invocation per iteration is intentional because cold state cannot be reused.
- `CliColdManifestBenchmarks` does the same for independent 8/24-case batch and
  paired diff trees, verifying every distinct trace produced an ETLX before cleanup.

All launch the executable, consume bounded stdout and stderr asynchronously, require
exit code zero with nonempty stdout and empty stderr, and return exit/output metadata
so the work cannot be elided. `[MemoryDiagnoser]` remains on the classes, but its
`Allocated` column belongs to the benchmark host and process-launch wrapper, not the
child filtrace process; do not report it as CLI allocation. Core allocation evidence
comes from Layer A. Child CPU and lifecycle evidence come from Layer C.

After each retained timing run, run a separate untimed telemetry pass of 25 launches
through the same process runner. `--cli-telemetry` supports the implemented warm and
cold single-trace/batch/diff scenarios. It samples cumulative `TotalProcessorTime`,
`PeakWorkingSet64`, and `PrivateMemorySize64` while each child is alive, then records
the largest observed values, exact per-launch arguments, exit code, output lengths,
and SHA-256 output digest in `cli-process.json`. Schema 2 also records monotonic
launch-to-exit time from immediately before `Process.Start` through successful root
process exit, before post-exit output draining and hashing. Warm launches reuse one
prepared tree; each cold launch gets new trace/manifest paths and records those paths
before cleanup.
`PeakWorkingSet64` is resident memory, not total committed memory; report both memory
fields with that distinction. Keep telemetry outside the timed BenchmarkDotNet method
so querying process counters cannot become the measured workload. Capture child
managed allocations with an explicit EventPipe allocation trace only when allocation
inside the CLI process is the question, and call it sampled allocation volume rather
than exact retained memory. The startup ETW profile used below does not carry
allocation data.

Keep these scenario names stable so baseline and candidate reports pair cleanly:

| Scenario | Command shape | Cache state | Primary use |
|---|---|---|---|
| `info-warm` | `info <trace> --format json` | warm ETLX | trace-load floor |
| `rank-self-warm` | `rank <trace> --metric cpu --format json` | warm ETLX | LP-2 |
| `rank-inclusive-warm` | `rank <trace> --metric cpu --measure inclusive --format json` | warm ETLX | LP-2 |
| `rank-activity-warm` | `rank <activity> --metric cpu --activity <task> --format json` | warm ETLX | LP-3 |
| `batch-8` / `batch-24` | `batch <manifest> --format json` | independent warm traces | LP-1 |
| `diff-8` / `diff-24` | `diff <before> <after> --format json` | independent warm traces | LP-1 |
| `batch-cold-8` / `batch-cold-24` | `batch <fresh-copy-manifest> --format json` | no ETLX | LP-1 memory guardrail |
| `diff-cold-8` / `diff-cold-24` | `diff <fresh-before> <fresh-after> --format json` | no ETLX | LP-1 memory guardrail |
| `symbols-1` / `symbols-32` | `info <trace> --symbols <dir> --format json` | warm ETLX | LP-4 |
| `info-cold` | `info <fresh-copy> --format json` | no ETLX | conversion guardrail |

The warm CLI case is warm only at the ETLX layer. Every invocation starts a fresh
process and therefore gets a fresh `TraceStore`; do not call it an in-process cache
measurement. The cold case means "no ETLX"; it does not flush the operating system's
filesystem page cache. MCP server cache performance and physical cold-disk latency are
separate investigations.

### Layer C: profile filtrace with filtrace

Use one fixed collector/analyzer binary for both arms so capture and interpretation
do not change with the implementation under test. The subjects are the baseline and
candidate `filtrace.exe` binaries. Build them from separate checkouts and give each
arm identical trace bytes at distinct paths.

The collector/analyzer must be a locally built Release CLI from the investigation's
recorded checkout. Never substitute an installed global `filtrace` or the MCP server:
either can silently change analysis behavior. For a single-checkout drill, use that
checkout's local CLI. For A/B work, use the fixed local baseline CLI for both arms.

```pwsh
$harnessCommit = '<merged commit containing the complete measurement harness>'
git worktree add --detach ../filtrace-perf-base $harnessCommit
dotnet build ../filtrace-perf-base/filtrace.slnx -c Release
dotnet build filtrace.slnx -c Release

$collector = (Resolve-Path ../filtrace-perf-base/src/Filtrace/bin/Release/net10.0/filtrace.exe).Path
$baseline = $collector
$candidate = (Resolve-Path src/Filtrace/bin/Release/net10.0/filtrace.exe).Path
```

Capture the same scenario names in separate baseline and candidate manifests. Use
`startup` to minimize observer effect and the same machine-honored sub-millisecond
interval for both arms. The current ETW reader weights every periodic record as 1.0;
it does not consume the effective interval recorded in the command manifest. Treat
self-profile weights as records and percentages, not absolute CPU milliseconds, and
fail the comparison if the manifests report different effective intervals.

Start with 25 iterations, then raise the count until the **target query** is thick:
`callers.contributingRecordCount` must be at least 200, and a source-line claim needs
`lines.attributedRecordCount` of at least 1,000. Whole-trace `info.sampleCount` does
not establish confidence for a narrow target frame.

```pwsh
$baselineScenarios = @(
  @{ Name = 'rank-self-warm'; Command = $baseline; Arguments = "rank <baseline-trace> --metric cpu --format json" }
)

./.agents/skills/filtrace/scripts/Capture-CommandTrace.ps1 `
  -Scenario $baselineScenarios `
  -Iterations 25 `
  -CaptureProfile startup `
  -CpuSampleMSec 0.125 `
  -FiltracePath $collector `
  -OutputDirectory artifacts/perf/LP-2/baseline-cli
```

Repeat with the candidate binary and matching scenario names. Then use the fixed
analyzer to orient, rank, and compare its own captures:

```pwsh
& $collector batch artifacts/perf/LP-2/baseline-cli/manifest.json --children exclude --format json
& $collector batch artifacts/perf/LP-2/candidate-cli/manifest.json --children exclude --format json
& $collector diff `
  artifacts/perf/LP-2/baseline-cli/manifest.json `
  artifacts/perf/LP-2/candidate-cli/manifest.json `
  --children exclude --format json
```

For each manifest case, read `trace` and `invocations[].processId`, then run:

```pwsh
& $collector info <case.etl> --format json
& $collector lifecycle <case.etl> --pid <comma-separated-root-pids> --format json
& $collector cpu <case.etl> --pid <comma-separated-root-pids> --children exclude --format json
& $collector callers <case.etl> <target-frame> --pid <comma-separated-root-pids> --children exclude --format json
& $collector lines <case.etl> --method <target-frame> --pid <comma-separated-root-pids> --children exclude --symbols <subject-output> --format json
```

Use each subject's own build output for source lines because the PDB must match that
binary. Keep the analyzer binary fixed for both arms. SC10 will eventually let
`lifecycle` consume the manifest case address directly; until then the comparison
script reads the recorded IDs rather than falling back to a process-name selector.

## Reproducibility contract

Every LP investigation writes beneath an ignored unique directory:

```text
artifacts/perf/<LP>/<timestamp>/
  run.json
  commands.txt
  ledger.md
  input-corpus.zip
  input-corpus.manifest.json
  dirty-source.patch
  dirty-source.zip
  dirty-source.manifest.json
  baseline/
    bdn/
    cli-benchmark/
    cli-capture/
    analysis/
  candidate/
    bdn/
    cli-benchmark/
    cli-capture/
    analysis/
```

`run.json` records:

- base and candidate commit IDs and clean/dirty state;
- full binary-capable patch, reviewed source archive, and manifest when the candidate
  is dirty;
- SHA-256 hashes of the actual input/source archives, every trace and manifest inside
  them, the symbol-directory inventory, and each subject binary;
- exact commands and non-secret environment settings;
- SDK, runtime/JIT, OS, architecture, processor count, CPU model, and power plan;
- BenchmarkDotNet version, job, filters, and artifact paths;
- filtrace collector/analyzer version and schema version;
- requested and effective ETW CPU sample interval.

Run baseline and candidate on the same machine, power plan, processor affinity, and
background-load conditions. Alternate arm order across three independent retained
runs (`B-A`, `A-B`, `B-A`) to reduce thermal/order bias. Compare the median of the
three arm-level BenchmarkDotNet means and the median of the three CLI lifecycle p50s.
Compute the 5% coefficient-of-variation guard across those three independent
arm-level values, not across BenchmarkDotNet's within-run iteration samples. Retain
the BenchmarkDotNet `Error` and `StdDev` columns as a separate within-run quality
signal. If either guard is noisy, mark the result inconclusive and rerun; do not tune
thresholds against it.

`ledger.md` has one row per candidate:

| Hypothesis | One-variable change | Benchmark | CLI scenario | Allocation / memory | Target frame | Decision |
|---|---|---|---|---|---|---|

Record rejected variants. Do not overwrite baseline artifacts with later runs.

## Input corpus

Committed fixtures are smoke checks, not the scale corpus:

| Input | Approximate current CPU samples | Use |
|---|---:|---|
| `folding.speedscope.json` | 4 | deterministic aggregator smoke only |
| `activity.nettrace` | 298 | activity correctness and setup smoke |
| `etw.etl` | 381 | ETW/process-scope smoke |
| `threadpool.nettrace` | 11,587 | medium aggregation and warm-load check |

Generate the retained scale corpus under `artifacts/perf-inputs/<corpus-id>`, then
archive the **actual bytes** as `input-corpus.zip` with repository-relative names and
an allowlisted manifest. Hashes and generation commands do not reconstruct traces:
timestamps, PIDs, sampling, and event order are nondeterministic. A durable claim is
not complete until the archive is copied to approved durable storage and restored in
a clean checkout; an ignored local directory alone is not durable evidence.

- CPU traces with roughly 10,000, 100,000, and 1,000,000 periodic samples, with
  average stack depths near 5 and 20;
- activity traces with roughly 10,000 and 100,000 CPU samples inside repeated named
  start/stop activities;
- manifest sets of 1, 2, 4, 8, 16, and 24 distinct trace paths. Copies may have
  identical bytes, but paths must be distinct so `TraceStore` does not deduplicate
  the load the experiment intends to parallelize;
- paired before/after manifests at the same case counts for diff;
- symbol directories containing 1, 8, 32, and 64 DLLs, with separate 0%, 25%, and
  100% embedded-PDB hit-rate sets.

Keep a tiny committed fixture in each new benchmark for setup/correctness, but do not
commit large generated traces merely to preserve performance evidence. Retain their
reviewed archive outside Git and record its durable location, hash, privacy review,
and exact restore command. Never archive credentials, private symbol caches, or
unrelated machine data.

## Common acceptance gates

These gates are fixed before testing a candidate:

1. **Correctness:** all unit, parity, CLI/MCP/docs/eval, capture, and agent-skill
   gates pass. Parallel results preserve deterministic ordering and produce the same
   JSON after normalizing only explicitly nondeterministic path/cache diagnostics.
2. **Small-case guardrail:** no primary small/common microbenchmark or CLI scenario
   regresses by more than 5%; a delta inside combined error bars is neutral.
3. **Target win:** the LP-specific large scenario improves by its stated threshold
   in at least two of three independent runs and in the median result.
4. **Allocation:** no managed allocation regression on unaffected Core paths. A
   parallel path may allocate thread-local state only within its LP-specific budget.
5. **Memory:** record peak child working set for CLI scenarios. Concurrency is bounded
   and must not create an unbounded live set as case/sample/module count grows.
6. **Attribution:** the self-profile shows the intended frame or phase shrinking; a
  wall-clock-only improvement with no matching attribution is not enough. When the
  claim names a retained method, require at least 200 target method records and, for
  a source-line claim, 1,000 attributed line records. For a phase deliberately
  eliminated (LP-3's pre-pass), use the baseline frame when thick and pair it with a
  structural/counter assertion that the phase executes zero times in the candidate.
7. **Scope:** compare identical trace bytes, symbol sets, options, output format, row
   limits, and process/activity scopes.

## Phase 0 - build the measurement harness

Complete this before any production parallelism edit.

**Phase 0 implementation through 2026-08-04:**

- feasible location-bearing synthetic stacks from 100 to 1,000,000 samples, with
  explicit cold and warm self/inclusive benchmarks;
- preloaded 1/8/24-case batch/diff benchmarks over degree values 1/2/4/8;
- compatibility-preserving degree-aware batch/diff overloads that remain sequential,
  with ordered-callback and range tests;
- `Filtrace.PerfWorkload` CPU and nested-activity modes, validated in real EventPipe
  captures with activity ranking and 498 `Order`-scoped CPU records in a short run;
- `Capture-TrackDCorpus.ps1`, validated for success, missing recorder, invalid
  workload options, native recorder failure, nonempty output, and paths containing
  spaces. It publishes through same-volume staging, fails closed on derived ETLX,
  and validates archive hashes against a portable no-BOM manifest before publication.
- committed-fixture `ActivityReadBenchmarks` for unscoped and `Order`-scoped CPU
  loads, plus a public-path embedded-PDB matrix at 1/8/32/64 DLLs and exact feasible
  0/25/100% hit rates;
- `CliWarmBenchmarks` for `info`, self, inclusive, activity, batch 8/24, and diff
  8/24, plus symbols 1/32; `CliColdConversionBenchmarks` for `info-cold`;
  `CliColdManifestBenchmarks` for cold batch/diff 8/24; and warm/cold child telemetry
  with exact per-launch arguments and validated BOM-free JSON.
- warm and cold CPU aggregation methods for self, inclusive, callers, hot lines,
  source heatmap, call tree, and classification over all 20 synthetic scenarios.
- focused thread-time, allocation-byte, and count metric parity over all seven
  aggregation families.
- adaptive `Capture-TrackDCorpus.ps1 -Scale` capture for CPU 10k/100k/1m at depths
  5/20 and activity-scoped CPU 10k/100k at depth 20. A local 2026-08-04 run produced
  9,995-1,000,403 target records across eight traces; its 8,923,614-byte archive hash
  is `A504CDFEC912F38390A68BE3B5DAA0823AE3A4FB869589550A0A63AF113B7635`.
- `Invoke-TrackDInvestigation.ps1` for benchmark-tree equality, corpus restoration,
  filtered BDN and CLI telemetry A/B, exact commands, run/comparison JSON, and a
  starter ledger. A same-checkout dry smoke paired three rows with zero allocation
  deltas and a largest absolute timing delta of 9.61%.
- `Test-TrackDInvestigation.ps1` fake-driven contracts for neutral comparison,
  injected adapter failure with retained commands/status, and test-adapter gating.

Remaining Phase 0 work is copying/restoring the reviewed corpus archive in approved
durable storage, optional Layer C capture wiring, and a post-merge exact-worktree
no-op run using the default job and 25-launch telemetry. The local ignored archive
and dirty-checkout dry smoke are not durable acceptance evidence.

### Benchmark additions

Complete the following benchmark set in `Filtrace.Benchmarks`:

- `FoldingAggregatorBenchmarks`: the initial CPU self/inclusive matrix now covers
  sample counts `100`, `1_000`, `5_000`, `10_000`, `100_000`, and `1_000_000`;
  stack depths 5 and 20; feasible low/high frame cardinality; source locations; and
  fractional, zero, and large exactly-representable weights. Separate benchmark
  methods and nonempty setup assertions now cover `CallersOf`, `HotLines`,
  `SourceHeatmap`, `CallTree`, and `Classify`.
  `FoldingAggregatorMetricBenchmarks` applies all seven families to one representative
  10k/depth-20/high-cardinality source for thread-time, allocation bytes, and count
  without multiplying the full CPU matrix.
- `FoldingAggregatorColdBenchmarks` now constructs a fresh aggregator over a prepared
  immutable source inside each operation, measuring the first query with an empty
  short-name cache. The warm class reuses and explicitly primes its aggregator. Keep
  both: a reused global aggregator alone hides first-query cost behind warmup.
- `ManifestAnalyzerBenchmarks`: preloaded batch/diff at 1/8/24 cases is implemented;
  add independent warm-trace loading and cold one-shot classes.
- Compatibility-preserving `Analyze(..., maxDegreeOfParallelism, load)` overloads
  on the batch and diff analyzers are implemented. Phase 0 validates a degree in
  `[1, 24]` and forwards to the existing sequential implementation for every value.
  Its XML contract warns that the loader may be called concurrently in a future
  implementation, while the legacy overload remains sequential. This gives both arms
  the same API and benchmark shape without shipping parallel behavior; LP-1 changes
  only the new overload's implementation and then opts the audited heads into it.
- `ActivityReadBenchmarks` is implemented over the same preconverted committed
  activity trace with no activity scope and with the named `Order` scope. Add the
  generated 10k/100k traces after the retained scale corpus is calibrated.
- `EmbeddedPdbBenchmarks` is implemented through public
  `TraceLoader.Load(trace, symbolsDirectory)` at 1/8/32/64 DLLs. Its embedded and
  non-embedded source PEs are verified at setup and padded to equal length so hit
  rate is the changed axis. Exact extractor tests cover embedded, no-PDB, corrupt,
  locked, duplicate-copy, and mixed directories without widening accessibility.
- `CliWarmBenchmarks` covers `info-warm`, `rank-self-warm`,
  `rank-inclusive-warm`, `rank-activity-warm`, `batch-8/24`, and `diff-8/24`;
  `CliColdConversionBenchmarks` covers `info-cold`; and
  `CliColdManifestBenchmarks` covers cold batch/diff 8/24. Their manifest corpus owns
  distinct paths, exact pairing, warm/cold ETLX validation, and cleanup.
  `symbols-1/32` use the same byte-normalized 100%-embedded corpus as the public PDB
  benchmark. Every implemented timing scenario is also accepted by child telemetry.
- `Filtrace.PerfWorkload` is implemented as a small net10.0 console project with
  `cpu` and `activity` modes. It accepts `--workers`, `--duration-ms`, `--depth`, and
  `--activity-rounds`; the CPU mode runs a non-inlined call chain at the requested
  depth, and the activity mode emits nested `Order`/`Query`/`Render` EventSource
  start/stop pairs around CPU work rather than sleeps. A returned checksum prevents
  elimination.

Capture the scale corpus from that workload, calibrating duration/workers until
`filtrace info` lands within 10% of the requested sample-count tier:

```pwsh
dotnet build benchmarks/Filtrace.PerfWorkload -c Release

dotnet-trace collect --profile dotnet-common,dotnet-sampled-thread-time -- `
  benchmarks/Filtrace.PerfWorkload/bin/Release/net10.0/Filtrace.PerfWorkload.exe `
  cpu --workers 8 --duration-ms 15000 --depth 20

dotnet-trace collect --profile dotnet-common,dotnet-sampled-thread-time `
  --providers Filtrace-TrackD:0xFFFFFFFFFFFFFFFF:5 -- `
  benchmarks/Filtrace.PerfWorkload/bin/Release/net10.0/Filtrace.PerfWorkload.exe `
  activity --workers 8 --duration-ms 15000 --depth 20 --activity-rounds 1000
```

Record the calibrated arguments beside each archived trace. The committed
sleep-based `ActivityLoop` remains a correctness fixture; it is not the scale
producer.

### Orchestration

`benchmarks/Invoke-TrackDInvestigation.ps1` now:

- create unique result directories;
- build an exact detached baseline and the candidate;
- verify both arms use the same `harnessCommit` and benchmark tree hash;
- copies/restores the scale corpus and records its archive hash;
- run the filtered BenchmarkDotNet baseline and candidate commands;
- write `run.json`, `commands.txt`, and a starter ledger without deciding whether a
  candidate passed.

Still add optional Layer C invocation of `Capture-CommandTrace.ps1`, extraction of
exact invocation IDs, and fixed-analyzer `info`, `lifecycle`, `cpu`, `callers`, and
`lines` output after arbitrary launch arguments have a reviewed encoding path.

[`Capture-TrackDCorpus.ps1`](../benchmarks/Capture-TrackDCorpus.ps1) is the first
implemented subset of that orchestrator: it builds the workload, captures one
CPU/activity pair, verifies both with filtrace, removes derived ETLX files, archives
the raw trace bytes under their portable `inputs/` paths, validates a SHA-256
manifest, and publishes the complete corpus from a same-volume staging directory.
Its `-Scale` mode owns the retained matrix and adapts duration from observed target
records until every trace lands within tolerance. The resulting local archive still
needs an approved durable location and a clean-checkout restore before supporting a
kept claim.

The A/B wrapper's explicit-checkout mode is test-only. Exact mode creates detached
worktrees; both modes refuse benchmark-tree drift, restore identical corpus bytes,
record raw BDN mean/allocation and child telemetry deltas, and retain failed partial
runs with `status: failed`.

Test the script with fake baseline/candidate executables before relying on an
expensive ETW run. The script must distinguish absent, nonzero, malformed, and valid
empty tool output; keep its output deterministic and UTF-8 without BOM. Exercise
absolute paths containing spaces and quotes, since SC11 still tracks the broader
command-capture provenance contract.

**Phase 0 exit:** one no-op baseline-versus-baseline run reconstructs successfully,
produces equivalent outputs, and reports neutral deltas at both measurement layers.

## LP-2 calibration - partition and reduce in `FoldingAggregator`

Run this first even though LP-1 is listed first in the roadmap: the existing
`FoldingAggregatorBenchmarks` provides the shortest end-to-end proof of the new
measurement loop.

### Hypotheses

- Parallel partition/reduce loses below a threshold because scheduling and merge
  costs dominate.
- It wins at high sample count and stack depth, where regex matching and frame walks
  dominate.
- Frame cardinality controls merge cost and may require a higher threshold than
  sample count alone.

### Benchmark matrix

Measure `SelfTime`, `InclusiveTime`, `CallersOf`, `HotLines`, `SourceHeatmap`,
`CallTree`, and `Classify` across:

- sample count: 100, 1k, 5k, 10k, 100k, 1m;
- average stack depth: 5 and 20;
- distinct shortened frames: 64 and 4,096;
- fold behavior: no helper match, common helper match, custom regex list;
- policy candidates: sequential, and separately built one-variable candidates using
  degree caps 2, 4, and 8 (each capped by logical processor count).

Do not combine all axes in one class/run. Start with self/inclusive to choose a
candidate threshold, then validate the other families against that policy. Do not
add a public or benchmark-only degree switch to `FoldingAggregator`: each policy is a
separate build from the same harness commit, and the ledger records its commit and
one changed policy variable.

Only run feasible sample/depth/cardinality combinations. The exact high-cardinality
tiers are:

| Samples | Depth 5 | Depth 20 |
|---:|---:|---:|
| 100 | 256 | 1,024 |
| 1,000 | 2,048 | 4,096 |
| 5,000+ | 4,096 | 4,096 |

The low-cardinality tier remains 64. Setup asserts that the requested distinct-frame
count does not exceed generated frame occurrences and that every requested frame
appears; never label an impossible requested count as observed cardinality.

### CLI confirmation

Use `rank-self-warm` and `rank-inclusive-warm` on the 11,587-sample committed trace
as a medium guardrail and on generated 100k/1m traces as target cases. Self-profile
`FoldingAggregator.SelfTime` / `InclusiveTime`, `FrameNames.IsFolded`, regex
matching, and dictionary merge frames.

### Keep/reject gate

- 100/1k cases: no more than 3% slower;
- 5k/10k cases: no more than 5% slower;
- 100k case: at least 15% faster at depth 20;
- 1m case: at least 25% faster at depth 20;
- managed allocation on the parallel path no more than 2x sequential and bounded
  by partition/frame cardinality, not sample count;
- large CLI rank median at least 10% faster; the 11,587-sample CLI case must not
  regress more than 5%;
- byte-equivalent rankings, totals, contributing-record counts, and deterministic
  tie ordering.

The parity corpus includes location-bearing samples and adversarial fractional
weights. Require byte-identical serialized output and stable row ordering; compare
unrounded Core totals within the existing numeric parity tolerances. Reject a merge
order that changes a rounded value, tie order, or frozen parity result.

Evaluate at least three threshold policies. Record and reject one-dimensional
sample thresholds if frame cardinality causes a repeatable regression.

## LP-1 - parallel case loading in batch and diff

### Hypotheses

- Independent trace paths scale across cases once ETLX caches are warm.
- One or two cases should stay sequential.
- Unbounded parallel loads can trade latency for an unacceptable peak live set, so
  degree of parallelism must be capped and measured.

### Benchmark matrix

`ManifestAnalyzerBenchmarks` covers:

- case count: 1, 2, 4, 8, 16, 24;
- operation: batch self, batch inclusive, manifest diff self/inclusive;
- load state: preloaded traces, warm ETLX/fresh `TraceLoader`, and cold ETLX as a
  separate one-shot class;
- trace size: smoke, ~10k samples, ~100k samples;
- max degree of parallelism: 1, 2, 4, 8.

The existing public `Analyze` overloads invoke arbitrary caller-supplied load
delegates sequentially; keep that behavior. Implement bounded parallelism in the
Phase 0 `maxDegreeOfParallelism` overload, with `1` preserving the sequential path.
The CLI and MCP heads opt into that overload only after their delegates are
thread-safe. In particular, replace the current non-atomic
`belowThreshold |= ...` updates with an atomic flag or per-case values reduced after
the loop. Audit `TraceStore`, warning collection, and every callback capture before
enabling parallelism.

Use preallocated result slots by case/pair index so output order stays manifest order.
Benchmark distinct paths, and add a separate duplicate-path case to pin the documented
`LruCache` double-factory race as a bounded performance cost rather than a correctness
failure. Add contract tests proving the legacy overload never overlaps callbacks, the
new overload never exceeds its degree, failures remain case-local, and `--strict`
trips when any parallel case falls below the symbol threshold. The overload has no
cancellation token; do not claim or test cancellation semantics in LP-1.

### CLI confirmation

Run `batch-8`, `batch-24`, `diff-8`, and `diff-24` against separate baseline and
candidate manifest trees. Capture each command with `Capture-CommandTrace.ps1`, then
use `batch` and `diff` over those command-capture manifests to compare the CLI's own
CPU. Inspect loader, conversion-gate, and aggregation callers.

Run `batch-cold-8/24` and `diff-cold-8/24` in the separate untimed telemetry pass to
measure peak working/private memory while independent ETLX conversions overlap. Cold
latency remains descriptive because TraceEvent conversion dominates. If the selected
degree breaches the memory gate, lower the degree or keep no-ETLX cases sequential;
do not accept a warm-only win that makes first use unsafe.

### Keep/reject gate

- 1/2 cases: no more than 5% slower;
- 8 warm cases: at least 15% faster;
- 24 warm cases: at least 25% faster;
- cold conversion is reported separately and must not be described as a loading win;
- peak CLI working set no more than 1.75x sequential at the selected default degree;
- in-flight load operations never exceed the selected degree; retained parsed traces
  still follow `TraceStore`'s independent capacity-16 cache contract;
- exact case order, warnings, failures, per-operation values, and diff rows are
  preserved.

Try degrees 2 and 4 before tying the default to processor count. Reject a policy that
wins on a high-core workstation but regresses a four-core runner.

## LP-3 - merge the activity pre-pass into the main read

### Hypotheses

- Activity-scoped reads pay almost two event-stream scans today.
- A single `TraceLogEventSource.Process()` can maintain activity state and build CPU
  samples without changing async activity ancestry or event counts.
- Unscoped reads should be byte-for-byte and performance neutral.

### Benchmark matrix

`ActivityReadBenchmarks` uses preconverted traces and fresh loaders with:

- committed 298-sample activity trace for correctness/smoke;
- generated 10k and 100k activity traces;
- no scope, matching outer activity, matching nested activity, and unmatched activity;
- shallow and async/thread-hopping activity trees.

Each invocation returns a digest of sample count, total weight, and selected frame
rows. Compare allocations as well as time; replacing a `HashSet<EventIndex>` with a
single-pass filter should not create a larger retained structure.

### CLI confirmation

Measure `rank-activity-warm` against the same command without `--activity`. Profile
`ComputeActivitySampleFilter`, `TraceLogEventSource.Process`, activity-computer
callbacks, and `ReadCore`. The baseline should visibly contain the pre-pass; the
candidate should show one processing pass and no standalone filter phase.

Use `TraceLogEventSource.Process` inclusive CPU as the retained target-frame measure
and require at least 200 contributing records there. `ComputeActivitySampleFilter`
may legitimately disappear and cannot satisfy a candidate density gate; verify its
zero candidate invocations with a test-only processing-pass counter or an identical
instrumentation patch applied to both measurement arms, not by requiring samples in
a method that no longer executes.

### Keep/reject gate

- exact matching/unmatched/nested activity results and applied context;
- unscoped load no more than 3% slower and no allocation regression;
- 10k matching activity at least 10% faster;
- 100k matching activity at least 20% faster;
- activity-scoped CLI median at least 10% faster;
- profile confirms the pre-pass is removed rather than merely shifted into another
  full scan.

If TraceEvent callback ordering prevents one-pass equivalence, reject the change and
record the upstream API needed rather than weakening activity correctness.

## LP-4 - parallel embedded-PDB scanning

### Hypotheses

- Parallel file read/PE parse/deflate helps only at larger DLL counts.
- Creating the temp directory lazily and writing distinct PDB names can remain
  deterministic under bounded concurrency.
- If public end-to-end measurements show less than 5 ms or 5% of CLI latency, TE-P1
  is a better investment than local parallel complexity.

### Benchmark matrix

`EmbeddedPdbBenchmarks` uses the public trace-loading path with:

- DLL count: 1, 8, 32, 64;
- embedded-PDB hit rate: 0%, 25%, 100%;
- total DLL bytes: small and representative build-output distributions;
- separately built one-variable candidates using degree caps 2, 4, and 8;
- valid assemblies plus unreadable/corrupt/non-managed neighbors.

Every iteration gets a fresh symbol directory identity. The public benchmark verifies
the sampled modules visible through the resulting source-resolution report, but that
report cannot prove that every decoy DLL was scanned. Add white-box correctness tests
in the existing `Filtrace.Core.Tests` friend assembly that call the internal extractor
and verify the exact PDB set for valid, no-PDB, corrupt, unreadable, duplicate-name,
and mixed directories. Do not benchmark the private extractor directly, add the
benchmark assembly as a friend, or widen production accessibility.

Degree comparisons are separate builds from the same harness commit; do not add a
production tuning option solely for BenchmarkDotNet.

### CLI confirmation

Measure `symbols-1` and `symbols-32`, then profile `info --symbols` and `lines
--symbols`. Attribute time to file reads, `PEReader`, `DeflateStream.CopyTo`, temp
file creation, source resolution, and cleanup. Check `%TEMP%` before/after repeated
failure runs for leaked `filtrace-pdb-*` directories.

### Keep/reject gate

- 1/8 DLL cases: no more than 5% slower;
- 32/64 DLL, 100%-hit cases: at least 15% faster and at least 5 ms absolute saved;
- actual `info --symbols` or `lines --symbols` median at least 5% faster;
- peak working set no more than 1.5x sequential at selected degree;
- identical matching/mismatch/missing-module diagnostics and source-line output;
- no temp-directory/file leaks after success, corrupt DLLs, or failed extraction.

Reject LP-4 when the absolute or end-to-end gate is not met, even if the isolated
ratio looks large.

## LP-5 - parallel native-symbol module lookups

**State:** blocked on TE-P3. Do not parallelize production lookup until TraceEvent
provides a documented thread-safety contract or a per-module async API.

### Measurement while blocked

Establish a baseline only:

- commands: `cpu --native-symbols`, `classify --native-symbols`, and `info` on ETW;
- module count: 1, 4, 8 relevant runtime/OS modules;
- symbol cache: populated warm cache for implementation comparisons; empty cache as
  descriptive network evidence only;
- record total elapsed time, resolved target frames, and cache file count/bytes from
  the current public surface.

The current runtime-symbol pass returns no per-module status object. If module-level
timing/status is needed, use a disposable instrumentation-only build that records
module name, elapsed time, and lookup outcome around each call. Apply the identical
instrumentation patch to baseline and prototype arms, keep it out of the production
PR, and never infer remote lookup status from the local-native `NativeSymbolInfo`.

Network-first-download results are not an acceptance metric. They vary with service,
DNS, and geography. Warm-cache lookup isolates local symbol processing and is the
only stable candidate benchmark.

### Unblock gate

Before a production candidate:

1. TE-P3 is answered in writing or an upstream API lands.
2. A disposable prototype uses one `SymbolReader` per worker and stress-runs at least
   100 repetitions under 1/2/4/8 relevant modules without access violations, corrupt
   names, or partial resolution.
3. Warm-cache 4+ module scenarios improve at least 20%, with no regression at one
   module and identical resolution.
4. Peak memory, open handles, and symbol-cache writes remain bounded.

If those conditions are not met, retain sequential lookup and keep LP-5 blocked.

## Execution order and PR boundaries

Measurement order differs slightly from roadmap numbering:

1. **Phase 0:** build and prove the harness with baseline-versus-baseline.
2. **LP-2 calibration:** exercise the existing benchmark and self-profile loop.
3. **LP-1:** highest expected product value after the harness is trusted.
4. **LP-3:** one-pass activity read, with async correctness as the hard gate.
5. **LP-4:** proceed only if measurement proves material absolute cost.
6. **LP-5:** baseline now; implementation only after TE-P3.

Phase 0 lands as a measurement-enabling PR containing the complete harness required
by the planned LP work. It may add a compatibility-preserving public seam such as the
sequential `maxDegreeOfParallelism` overload above, but ships no parallel behavior.
If a later item discovers a missing scenario, land another measurement-only extension
and select a new baseline before writing production code. The benchmark tree must not
change in an LP implementation PR.

Use one implementation PR per LP item. It contains:

- the baseline and candidate ledger summary (not large traces or generated BDN
  output);
- the one-variable production change;
- exact correctness and performance commands;
- before/after tables for the retained scenarios;
- filtrace self-profile evidence naming the target frame/phase;
- roadmap status changed to kept, rejected, or blocked with the measured reason.

Never combine two parallelization mechanisms in one performance PR. If a candidate
misses its gate, record the rejection in the roadmap/ledger and do not submit the
losing production code. Any reusable harness improvement must already have landed in
its own measurement-only PR.

## Final Track D exit

Track D exits when:

- the Phase 0 no-op experiment proves reconstruction and neutral comparison;
- LP-1 through LP-4 each have a benchmark-backed kept/rejected decision;
- LP-5 is shipped after TE-P3 or remains explicitly blocked with current baseline
  evidence;
- retained changes pass all normal filtrace gates on Windows and Linux ARM64;
- the roadmap links each decision to its benchmark class, CLI scenario, and durable
  experiment summary.
