# Filtrace benchmarks

`Filtrace.Benchmarks` is the BenchmarkDotNet harness for product performance. Pure
analysis benchmarks use synthetic in-memory inputs; trace-read and CLI benchmarks
use prepared committed fixtures so their cache state is explicit. Binary fixture
generation remains under `fixtures/`. The phased microbenchmark and
filtrace-self-profiling program is in
[the Track D plan](../docs/parallelism-opportunities.md).

Run all benchmarks in Release:

```pwsh
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks
```

Filter to one class or method while iterating:

```pwsh
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter '*FoldingAggregatorBenchmarks*' --job short
```

Every benchmark class uses `[MemoryDiagnoser]`, performs setup outside the measured
method, and returns a value derived from the work. Generated
`BenchmarkDotNet.Artifacts/` output is ignored by Git.

Run the Phase 0 trace-read, symbol-scan, and process-launch smokes:

```pwsh
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter '*ActivityReadBenchmarks*' --job dry
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter '*TimelineProviderBenchmarks*' --job dry
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter '*EmbeddedPdbBenchmarks*' --job dry
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter '*FoldingAggregatorMetricBenchmarks*' --job dry
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter '*Cli*Benchmarks*' --job dry
```

`TimelineProviderBenchmarks` compares the default five-lane timeline with a
default 200 ms point-in-time snapshot over committed allocation, exception, JIT,
and long-running thread-pool captures. The project copies each raw trace; setup
generates and verifies its ETLX cache and validates fixture-specific lane evidence
before measurement.

The CLI benchmark `Allocated` column belongs to the BenchmarkDotNet host and process
wrapper, not the child filtrace process. Capture three telemetry launches while
iterating (the default is 25) with:

```pwsh
$telemetry = 'artifacts/perf-smoke'
New-Item -ItemType Directory -Force $telemetry | Out-Null
Copy-Item tests/Filtrace.Core.Tests/Fixtures/activity.nettrace "$telemetry/activity.nettrace"
dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- `
  --cli-telemetry `
  --scenario info-warm `
  --trace "$telemetry/activity.nettrace" `
  --output "$telemetry/cli-process.json" `
  --iterations 3
```

Telemetry accepts the implemented single-trace and manifest scenario names,
including `batch-8`, `info-cold`, and `diff-cold-8`. Warm launches reuse one
prepared input tree; every cold launch records its own exact temporary paths. Cold
paths are provenance and no longer exist after each launch is validated and cleaned.
The shared runner caps each captured child stream at 10,485,760 characters; larger
output fails the run instead of exhausting the benchmark host.

Telemetry schema 2 records `launchToExitMilliseconds` from the monotonic timestamp
immediately before `Process.Start` through successful root-process exit. It excludes
post-exit output draining and hashing. CPU is the largest cumulative value observed
by polling, working set is the largest OS-reported peak observed by polling, and
private memory is the largest sampled value.

To profile a benchmark with filtrace:

```pwsh
dotnet build src/Filtrace/Filtrace.csproj -c Release
$filtraceName = if ($IsWindows) { 'filtrace.exe' } else { 'filtrace' }
$filtrace = (Resolve-Path (Join-Path 'src/Filtrace/bin/Release/net10.0' $filtraceName)).Path
./.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1 `
  -Project benchmarks/Filtrace.Benchmarks `
  -Filter '*TimelineProviderBenchmarks.Snapshot*' `
  -FiltracePath $filtrace
```

Use that same `$filtrace` apphost for every deeper `info`, `rank`, `callers`,
`source`, `diff`, and `export` command. The helper's printed `filtrace` commands
are argument templates; execute their arguments through `& $filtrace`. Do not use
an installed global tool or the MCP server to investigate this repository, because
it may run analyzer code from a different build. A/B work uses one fixed locally
built baseline CLI to analyze both arms.

`Filtrace.PerfWorkload` produces parameterized CPU and nested-activity traces for
the Track D scale corpus. Smoke both modes directly:

```pwsh
dotnet run -c Release --project benchmarks/Filtrace.PerfWorkload -- cpu --workers 2 --duration-ms 500 --depth 5
dotnet run -c Release --project benchmarks/Filtrace.PerfWorkload -- activity --workers 2 --duration-ms 500 --depth 5 --activity-rounds 10
```

Capture and archive one CPU/activity corpus pair with exact hashes and filtrace
verification:

```pwsh
./benchmarks/Capture-TrackDCorpus.ps1 -Workers 8 -CpuDurationMilliseconds 15000 -ActivityDurationMilliseconds 15000 -Depth 20
```

Capture the adaptive retained matrix (CPU 10k/100k/1m at depths 5/20 and
activity-scoped CPU 10k/100k at depth 20):

```pwsh
./benchmarks/Capture-TrackDCorpus.ps1 -Scale -Workers 8
```

Run a dry no-op reconstruction while iterating on the harness:

```pwsh
./benchmarks/Invoke-TrackDInvestigation.ps1 `
  -InputCorpusDirectory artifacts/perf-inputs/<corpus-id> `
  -BaselineCheckout . `
  -CandidateCheckout . `
  -AllowDirtyCheckouts `
  -NoBuild
```

This dry smoke checks reconstruction plumbing once; it is not performance
evidence. Merge the complete measurement harness before choosing a baseline,
then run fresh baseline and candidate measurements from the same benchmark tree.
Add fixed-analyzer profiles to a retained A/B run with:

```pwsh
$inputCorpusDirectory = 'artifacts/perf-inputs/<corpus-id>'
$harnessCommit = '<merged-harness-commit>'
$baselineCommit = '<baseline-product-commit>'
$candidateCommit = '<candidate-product-commit>'
$analyzerPath = (Resolve-Path 'artifacts/tools/frozen-analyzer/filtrace.exe').Path

./benchmarks/Invoke-TrackDInvestigation.ps1 `
  -InputCorpusDirectory $inputCorpusDirectory `
  -HarnessCommit $harnessCommit `
  -BaselineCommit $baselineCommit `
  -CandidateCommit $candidateCommit `
  -BenchmarkJob default `
  -CliScenario rank-self-warm `
  -TelemetryIterations 25 `
  -CaptureProfiles `
  -AnalyzerPath $analyzerPath
```

`-CaptureProfiles` is opt-in and runs only after both arms finish their timed
BenchmarkDotNet measurements and untimed child telemetry. It supports
`info-warm`, `rank-self-warm`, `rank-inclusive-warm`, and
`rank-activity-warm`; these are persistent single-trace warm scenarios. The
current capture input is one trace and one warm command per arm. Retained-heap,
concurrency, and eviction captures are not implemented.

`-AnalyzerPath` must name a locally built Release apphost with its adjacent DLL,
deps file, and runtimeconfig file. Do not use a global tool or modify that build
during the run. The wrapper inventories the bounded analyzer output directory,
copies the whole directory into `profile-artifacts/analyzer`, and verifies the
source and snapshot identities before and after profiling.

`-DotnetTracePath` defaults to `dotnet-trace` on `PATH`; an explicit executable
path is also accepted. The executable is resolved once and its path, hash,
version, and effective profiles are recorded. The shared recorder negotiation
used by project capture requires `gc-verbose` for allocation/GC and either
`dotnet-common,dotnet-sampled-thread-time` or `cpu-sampling` for CPU. Unsupported
recorders or scenarios fail during preflight, before either measured arm runs.

Each arm retains bounded CPU and allocation traces, recorder logs, structured
analysis plans, query outputs, and analysis snapshots under `profiles/`.
`profiles.json` reports each profile as `observed`, `insufficientQuality`, or
`empty`. CPU evidence requires a positive contributing-record count. Allocation
may validly report that count as unavailable, and enabled GC with zero
collections is retained as `empty`, not treated as absent. CPU and allocation
weights are sampled evidence; allocation scope weight is not an exact allocation
total. Use these captures to attribute a concrete cost before proposing an
optimization, and do not make an attribution claim from `insufficientQuality`.

Retained runs omit the explicit checkouts and dirty override, use exact commit
arguments, the default BenchmarkDotNet job, and 25 telemetry launches. A completed
run carries `run-status.json` with `status: completed`; failed partial runs remain
available with `failure.txt` and `status: failed`.
