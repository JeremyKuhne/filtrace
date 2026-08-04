# Filtrace benchmarks

`Filtrace.Benchmarks` is the BenchmarkDotNet harness for product performance. It
uses synthetic in-memory inputs so trace conversion and file I/O do not hide the
analysis code being measured. Binary fixture generation remains under `fixtures/`.

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
