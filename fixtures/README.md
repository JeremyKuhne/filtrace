# filtrace fixtures

The committed trace corpus the tests read and the manual tools that regenerate it.
Binary captures are test contracts: regenerate them only with the matching script,
then run the full test/eval suite and review every changed assertion or baseline.

## Layout

- `HotLoopBench/` - a dedicated BenchmarkDotNet and capture-utility project. It is
  intentionally outside `filtrace.slnx`; fixture regeneration is manual. Its bounded
  workloads cover CPU (`HotLoop`), allocation/GC (`AllocLoop`), exceptions
  (`ExceptionLoop`), JIT (`JitLoop`), lock contention (`ContentionLoop`), .NET 9+
  wait handles (`WaitLoop`), start-stop activities (`ActivityLoop`), thread-pool
  starvation (`ThreadPoolStarveLoop`), and net481 ETW CPU/thread time (`EtwLoop`).
  Utility commands count or inspect events, convert ETL to ETLX, capture disk I/O,
  and relog a capture to one process tree.
- `oracles/Get-TraceHotspots.ps1` - the frozen legacy CPU-ranking oracle. The
  parity pipeline executes it as a process and compares output; production code
  never references it.
- `make-fixtures.ps1` - cross-platform EventPipe generator.
- `capture-etw.ps1` - elevated Windows generator for the net481 CPU/thread-time ETW
  fixture.
- `capture-diskio.ps1` - elevated Windows generator for the trimmed disk-I/O ETW
  fixture.

## Regenerating

From the repository root with the .NET 10 SDK:

```pwsh
# EventPipe corpus; no elevation.
./fixtures/make-fixtures.ps1

# ETW corpus; Windows Administrator terminal.
./fixtures/capture-etw.ps1
./fixtures/capture-diskio.ps1
```

`make-fixtures.ps1` refreshes the parity
`hotloop.speedscope.json` / `hotloop.oracle.json` pair together and writes the
allocation, JIT, exception, contention, wait, activity, and thread-pool `.nettrace`
smokes under `tests/Filtrace.Core.Tests/Fixtures/`. Keep the parity pair together:
absolute sample counts vary by capture, while the oracle and filtrace must read the
same committed file.

`capture-etw.ps1` writes `etw.etl` using a custom BenchmarkDotNet ETW profile with
CPU and context-switch stacks. `capture-diskio.ps1` writes `diskio.etl`: it captures
the required DiskIO/DiskFileIO keywords, then relogs to the workload process tree so
the machine-wide file-name rundown is small enough to commit. See
[../docs/filtrace-etl-trimming.md](../docs/filtrace-etl-trimming.md) for the relog
limitation.

## Tracked files and caches

Git tracks the raw `.nettrace` / `.etl` smokes and the speedscope/oracle pair. ETLX
files beside them are generated TraceEvent caches and are ignored; `capture-etw.ps1`
also writes `etw.etlx` for manual conversion checks, but it is not part of the
committed public input corpus. The full HotLoop CPU `.nettrace` and other large
BenchmarkDotNet captures remain under the ignored
`HotLoopBench/BenchmarkDotNet.Artifacts/` directory.

Do not rename the `TraceQ.Fixtures.HotLoopBench` namespace: it is embedded in the
committed stacks, and regenerating the ETW goldens requires an elevated Windows
capture.
