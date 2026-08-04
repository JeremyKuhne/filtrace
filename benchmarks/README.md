# Filtrace benchmarks

`Filtrace.Benchmarks` is the BenchmarkDotNet harness for product performance. It
uses synthetic in-memory inputs so trace conversion and file I/O do not hide the
analysis code being measured. Binary fixture generation remains under `fixtures/`.
The phased microbenchmark and filtrace-self-profiling program is in
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

To profile a benchmark with filtrace:

```pwsh
./.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1 -Project benchmarks/Filtrace.Benchmarks -Filter '*FoldingAggregatorBenchmarks.SelfTime*'
```

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
