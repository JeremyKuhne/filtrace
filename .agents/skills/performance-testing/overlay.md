---
core: performance-testing
core-pin: v0.13.0
---

# Performance testing overlay

## Project binding

- The product perf project is
  [benchmarks/Filtrace.Benchmarks](../../../benchmarks/Filtrace.Benchmarks).
  It targets net10.0, lives in `filtrace.slnx`, and benchmarks pure analysis paths
  over synthetic inputs so trace conversion and file I/O do not hide the code under
  test.
- Run it in Release with an explicit filter:

  ```pwsh
  dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter *FoldingAggregatorBenchmarks*
  ```
- The separate
  [fixtures/HotLoopBench/HotLoopBench.csproj](../../../fixtures/HotLoopBench/HotLoopBench.csproj)
  remains a manual binary-fixture generator outside `filtrace.slnx`. Run its net481
  target only for the explicit ETW fixture jobs that exercise .NET Framework capture
  behavior; do not put product microbenchmarks there.

## Capture and analysis handoff

- Use [make-fixtures.ps1](../../../fixtures/make-fixtures.ps1) for the EventPipe
  corpus; use [capture-etw.ps1](../../../fixtures/capture-etw.ps1) and
  [capture-diskio.ps1](../../../fixtures/capture-diskio.ps1) only from an elevated
  Windows session.
- Once a trace exists, hand analysis to the [filtrace skill](../filtrace/SKILL.md).
  Performance-testing owns scenario and benchmark design; filtrace owns trace
  format choice, ranking, drill-down, comparison, and export.
- Profile one product benchmark with
  [Capture-BenchmarkTrace.ps1](../filtrace/scripts/Capture-BenchmarkTrace.ps1):

  ```pwsh
  ./.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1 -Project benchmarks/Filtrace.Benchmarks -Filter '*FoldingAggregatorBenchmarks.SelfTime*'
  ```
- Use [il-copy-inspection](../il-copy-inspection/SKILL.md) only when the question is
  whether the C# compiler emitted a struct copy. BenchmarkDotNet and filtrace answer
  whether that copy has measurable runtime cost.
- Binary fixtures are frozen test contracts. Regenerate only the intended pair or
  family, review every baseline change, and run the full test and eval gates.