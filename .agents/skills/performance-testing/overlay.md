---
core: performance-testing
core-pin: v0.10.0
---

# Performance testing overlay

## Project binding

- The benchmark/capture project is
  [fixtures/HotLoopBench/HotLoopBench.csproj](../../../fixtures/HotLoopBench/HotLoopBench.csproj).
  It is intentionally outside `filtrace.slnx` and is a manual fixture generator,
  not a general production perf project.
- The analyzer product targets net10.0 only. Run net481 only for the explicit ETW
  fixture jobs that exercise .NET Framework capture behavior.
- Build or run the fixture project with an explicit framework and Release
  configuration, for example:

  ```pwsh
  dotnet run -c Release -f net10.0 --project fixtures/HotLoopBench -- --filter *HotLoop*
  ```

## Capture and analysis handoff

- Use [make-fixtures.ps1](../../../fixtures/make-fixtures.ps1) for the EventPipe
  corpus; use [capture-etw.ps1](../../../fixtures/capture-etw.ps1) and
  [capture-diskio.ps1](../../../fixtures/capture-diskio.ps1) only from an elevated
  Windows session.
- Once a trace exists, hand analysis to the [filtrace skill](../filtrace/SKILL.md).
  Performance-testing owns scenario and benchmark design; filtrace owns trace
  format choice, ranking, drill-down, comparison, and export.
- Binary fixtures are frozen test contracts. Regenerate only the intended pair or
  family, review every baseline change, and run the full test and eval gates.