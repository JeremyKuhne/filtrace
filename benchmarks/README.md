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

Retained runs omit the explicit checkouts and dirty override, use exact commit
arguments, the default BenchmarkDotNet job, and 25 telemetry launches. A completed
run carries `run-status.json` with `status: completed`; failed partial runs remain
available with `failure.txt` and `status: failed`.
