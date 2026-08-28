---
core: performance-testing
core-pin: v0.14.0
---

# Performance testing overlay

## Project binding

- **Repository override:** the product perf project is single-target `net10.0`.
  Do not apply the core's multi-target `-f` or both-TFM rules to this project. The
  product perf project is
  [benchmarks/Filtrace.Benchmarks](../../../benchmarks/Filtrace.Benchmarks).
  It targets net10.0 and lives in `filtrace.slnx`. Pure analysis paths use synthetic
  inputs. Trace-provider benchmarks use committed raw fixtures and prime their
  generated ETLX cache during global setup when conversion must stay outside the
  measured operation.
- Run it in Release with an explicit filter:

  ```pwsh
  dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter '*TimelineProviderBenchmarks*'
  ```
- The separate
  [fixtures/HotLoopBench/HotLoopBench.csproj](../../../fixtures/HotLoopBench/HotLoopBench.csproj)
  remains a manual binary-fixture generator outside `filtrace.slnx`. Run its net481
  target only for the explicit ETW fixture jobs that exercise .NET Framework capture
  behavior; do not put product microbenchmarks there.
- The generic `framework-jit-optimization` and `scratch-buffer-strategy` branches
  are not vendored here because the analyzer product is net10.0-only. Continue with
  existing local C# patterns, then validate through this benchmark/profile loop.

## Capture and analysis handoff

- Use [make-fixtures.ps1](../../../fixtures/make-fixtures.ps1) for the EventPipe
  corpus; use [capture-etw.ps1](../../../fixtures/capture-etw.ps1) and
  [capture-diskio.ps1](../../../fixtures/capture-diskio.ps1) only from an elevated
  Windows session.
- Once a trace exists, hand analysis to the [filtrace skill](../filtrace/SKILL.md).
  Performance-testing owns scenario and benchmark design; filtrace owns trace
  format choice, ranking, drill-down, comparison, and export.
- Build this checkout's Release CLI before profiling. Pass its apphost to capture
  helpers and use the same binary for every orientation, ranking, drill, diff, and
  export command. Never use an installed global `filtrace` or the MCP server for a
  deeper investigation of filtrace itself; either may run different analyzer code.

  ```pwsh
  dotnet build src/Filtrace/Filtrace.csproj -c Release
  $filtraceName = if ($IsWindows) { 'filtrace.exe' } else { 'filtrace' }
  $filtrace = (Resolve-Path (Join-Path 'src/Filtrace/bin/Release/net10.0' $filtraceName)).Path
  ```
- Profile one product benchmark with
  [Capture-BenchmarkTrace.ps1](../filtrace/scripts/Capture-BenchmarkTrace.ps1):

  ```pwsh
  ./.agents/skills/filtrace/scripts/Capture-BenchmarkTrace.ps1 `
    -Project benchmarks/Filtrace.Benchmarks `
    -Filter '*TimelineProviderBenchmarks.Snapshot*' `
    -FiltracePath $filtrace
  ```
- The helper's printed `filtrace` commands are argument templates. Execute those
  arguments through `& $filtrace`, preserving the same locally built analyzer. For
  an A/B investigation, build one baseline checkout and use that fixed local CLI to
  capture metadata and analyze both arms.
- Use [il-copy-inspection](../il-copy-inspection/SKILL.md) only when the question is
  whether the C# compiler emitted a struct copy. BenchmarkDotNet and filtrace answer
  whether that copy has measurable runtime cost.
- Binary fixtures are frozen test contracts. Regenerate only the intended pair or
  family, review every baseline change, and run the full test and eval gates.

## Investigation budget

Use four explicit stages for optimization work in this repository. State the current
stage in progress updates and do not silently escalate it.

| Stage | Default budget | Filtrace execution |
| --- | ---: | --- |
| Screen | 15 minutes | Focus one small/common row and one or two target rows; use `--job short`; try at most three one-variable candidates. |
| Product pilot | 15 minutes | Run the common and largest real CLI scenarios 3-5 times per arm with exact output comparison and child memory counters. |
| Confirm | 30 minutes | Broaden only the surviving candidate to affected methods/axes, cold/warm forms, and full correctness checks. |
| Retained | remainder of a 60-90 minute investigation | Use exact worktrees, the default BenchmarkDotNet job, alternating three-run evidence, 25-launch telemetry, and filtrace attribution. |

For Track D, use `Invoke-TrackDInvestigation.ps1` with a narrow
`-BenchmarkFilter`, `-BenchmarkJob dry` or `short`, and 3-5
`-TelemetryIterations` for screening/pilot work. The default job and 25 launches
are retained-evidence settings, not candidate-discovery defaults.

Before a timed run, preflight once:

- remove or reject generated worktrees beneath the repository that would make
  BenchmarkDotNet discover duplicate project names;
- verify baseline/candidate benchmark-tree hashes and exact row identities;
- record power plan, process priority, and processor affinity without inferring
  physical-core topology from logical ids;
- make native commands noninteractive, retain failed status/output, and clean only
  resources owned by the current run.

Do not spend the candidate budget repairing measurement contamination. Mark that run
invalid, fix the harness/preflight, and restart the same stage with a fresh output
directory.

The canonical hard stops are in
[investigation-workflow.md](investigation-workflow.md). Filtrace uses its defaults
unchanged: 60-90 minutes total, three candidates, one repair each, one repeated
product pilot, and at most two retained noise reruns. For a 10% retained gate, the
pilot cutoff is 8% with equivalent output, successful launches, arm CVs no higher
than 5%, and consistent direction across all three or at least four of five
paired/alternated repetitions.
