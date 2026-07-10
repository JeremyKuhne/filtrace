# filtrace

[![CI](https://github.com/JeremyKuhne/filtrace/actions/workflows/ci.yml/badge.svg)](https://github.com/JeremyKuhne/filtrace/actions/workflows/ci.yml)
[![KlutzyNinja.Filtrace](https://img.shields.io/nuget/v/KlutzyNinja.Filtrace?logo=nuget&label=KlutzyNinja.Filtrace)](https://www.nuget.org/packages/KlutzyNinja.Filtrace)
[![KlutzyNinja.Filtrace.Mcp](https://img.shields.io/nuget/v/KlutzyNinja.Filtrace.Mcp?logo=nuget&label=KlutzyNinja.Filtrace.Mcp)](https://www.nuget.org/packages/KlutzyNinja.Filtrace.Mcp)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A small, agent-shaped CLI and MCP server for analyzing .NET CPU, allocation,
blocking, and wall-clock traces. Built on the
`Microsoft.Diagnostics.Tracing.TraceEvent` library; reads EventPipe
(`.nettrace` / `.speedscope.json`) and ETW (`.etl`) captures from both .NET and
.NET Framework runs.

## Install

filtrace targets **.NET 10**. Both heads are published on NuGet.org:
[`KlutzyNinja.Filtrace`](https://www.nuget.org/packages/KlutzyNinja.Filtrace)
(the `filtrace` CLI) and
[`KlutzyNinja.Filtrace.Mcp`](https://www.nuget.org/packages/KlutzyNinja.Filtrace.Mcp)
(the MCP server).

### CLI (global tool)

Installing the CLI as a .NET global tool needs the .NET 10 SDK (`dotnet tool`
ships with it):

```pwsh
dotnet tool install --global KlutzyNinja.Filtrace
filtrace cpu app.nettrace
```

Update or remove it later with `dotnet tool update --global KlutzyNinja.Filtrace`
or `dotnet tool uninstall --global KlutzyNinja.Filtrace`.

### MCP server

The MCP server runs on demand - there is no install step. Add the stdio server to
your agent's MCP config and `dnx` (bundled with the .NET 10 SDK) fetches
`KlutzyNinja.Filtrace.Mcp` and launches it. See
[Using filtrace from an AI agent](#using-filtrace-from-an-ai-agent) for the exact
config block and the tool workflow.

### From source

```pwsh
dotnet pack src/Filtrace/Filtrace.csproj -c Release
dotnet tool install --global --add-source ./artifacts/packages KlutzyNinja.Filtrace
```

## Using filtrace

Every analysis verb takes a trace path and prints a dense text report (or compact
JSON with `--format json`); `collect` instead launches the executable it records.
The canonical investigation is **orient -> rank -> drill ->
compare**: inspect the capture, rank the matching metric, drill an unwindowed CPU
ranking when needed, then diff comparable CPU traces against a baseline.

```pwsh
# Workflow: orient, rank the hottest frames, drill into one, then diff two runs.
filtrace info app.nettrace                     # 0. orient: format, symbol rate, what it can answer
filtrace cpu app.nettrace                      # 1. what's hot (self-time)
filtrace callers app.nettrace MyApp.Parse      # 2. who calls the hot frame
filtrace lines app.nettrace --symbols bin/Release/net10.0   # 3. hot source lines
filtrace diff before.nettrace after.nettrace   # 4. what changed between runs
```

The same analysis core is exposed as a stdio MCP server: every analysis verb has a
matching `trace_*` tool (sixteen in all - `info` -> `trace_info`, `rank` ->
`trace_rank`, `callers` -> `trace_callers`, and so on), returning the same envelope
shape and results. The capture and housekeeping verbs (`collect`, `convert`,
`clean`) are CLI-only, as is widening an ETW analysis with `--all-processes`; MCP
supports automatic or named-process scope. See
[Using filtrace from an AI agent](#using-filtrace-from-an-ai-agent) for the client
config and tool workflow.

### Verbs

**Orient** - see what a capture holds before ranking (the CLI counterpart of the
`trace_info` tool):

| Verb | Purpose | Example |
|---|---|---|
| `info` | Format, sample count, symbol-resolution rate, the analyses the trace can answer, and warnings | `filtrace info app.nettrace` |

**Ranking** - rank stacks by a metric (`--metric` on `rank`, or a shortcut verb):

| Verb | What it ranks | Example |
|---|---|---|
| `rank` | Any metric (`cpu`, `alloc`, `exceptions`, `threadtime`, `contention`, `wait`, `activity`) | `filtrace rank app.nettrace --metric contention` |
| `cpu` | CPU self-/inclusive-time | `filtrace cpu app.nettrace --measure inclusive` |
| `alloc` | Bytes allocated, by site | `filtrace alloc app.nettrace --top 10` |
| `exceptions` | Exception types by count; inclusive view reveals throw paths | `filtrace exceptions app.nettrace` |
| `threadtime` | Wall-clock (running + blocked), Windows `.etl` | `filtrace threadtime app.etl` |

Every ranking verb accepts `--root` (scope to a frame subtree) and `--benchmark`
(scope a BenchmarkDotNet capture to the measured workload, past the harness). The
verbs that can read a multi-process ETW `.etl` - `cpu`, `threadtime`, `rank`,
`callers`, `lines`, `heatmap`, `tree`, `classify`, and the `timeline` overview - also
accept `--process` / `--all-processes` (the busiest process tree, ranked by CPU sample count, is
auto-scoped by default); `alloc` and `exceptions` read single-process
`.nettrace` only, so they have no process options. To see what is in a
multi-process capture before scoping, run `filtrace processes` (below). The `rank`
verb adds two more scopes: `--activity <name>` (the CPU samples taken inside one
start-stop request/job) and `--time <start>,<end>` (milliseconds from the trace
start, either bound optional; any metric on `.nettrace` / `.etl`), to zoom in on one
request or the slice around a latency spike. Speedscope input is aggregate-only for
`--time` and warns that the window was ignored.

```pwsh
filtrace cpu bdn.nettrace --benchmark          # just the [Benchmark] code
filtrace alloc bdn.nettrace --benchmark        # allocations under the workload
filtrace processes machinewide.etl             # list every process by weight
filtrace cpu machinewide.etl --process MyApp   # one process tree
filtrace rank app.nettrace --time 1000,5000    # just the spike window
```

**Native runtime symbols.** Managed frames (including NGEN and ReadyToRun
framework methods) resolve for free from the trace's CLR rundown. The *unmanaged*
runtime frames - the GC, the JIT, `memset` / `memcpy`, write barriers - need PDBs
from the Microsoft public symbol server, which `cpu` / `rank` fetch only when you
opt in with `--native-symbols` (cached under `--symbol-cache`, default in the temp
path). It is off by default so analysis stays offline and deterministic; the first
run downloads, later runs hit the cache.

```pwsh
filtrace cpu app.etl --process MyApp --native-symbols   # name the GC/JIT/memcpy frames
```

**CPU drill-down** - follow an unwindowed CPU ranking into detail:

| Verb | Purpose | Example |
|---|---|---|
| `callers` | Immediate CPU callers of a frame, or a caller/callee view with `--callees` | `filtrace callers app.nettrace MyApp.Parse --callees` |
| `lines` | Hottest CPU source lines of scoped methods | `filtrace lines app.nettrace --symbols bin/Release/net10.0` |
| `heatmap` | Per-line CPU heat for one source file | `filtrace heatmap app.nettrace Parser.cs` |
| `tree` | Top-down CPU call tree from the root | `filtrace tree app.nettrace --max-depth 5` |

**Inventory** - see what a (possibly machine-wide) capture contains:

| Verb | Purpose | Example |
|---|---|---|
| `processes` | List processes by CPU-sample weight, to pick a `--process` target | `filtrace processes machinewide.etl` |
| `classify` | Summarize CPU time by runtime work category (zeroing / copying / GC / ...) | `filtrace classify app.etl --native-symbols` |

**Temporal** - see what happened when, then scope a ranking to the busy window:

| Verb | Purpose | Example |
|---|---|---|
| `timeline` | Per-bucket GC / CPU / exception / allocation / JIT activity over time | `filtrace timeline app.nettrace --lanes gc,cpu` |

**Compare and export:**

| Verb | Purpose | Example |
|---|---|---|
| `diff` | Absolute CPU sampled-time changes between comparable traces | `filtrace diff before.nettrace after.nettrace` |
| `export` | Write a flame graph (speedscope / chromium) | `filtrace export app.nettrace --format speedscope -o app.json` |

**Structured reports:**

| Verb | Purpose | Example |
|---|---|---|
| `gcstats` | GC counts, pauses, % time in GC, induced, heap | `filtrace gcstats app.nettrace` |
| `jitstats` | JIT method count, compile time, sizes | `filtrace jitstats app.nettrace` |
| `threadpool` | Worker-thread adjustments and starvation (slow under load, CPU idle) | `filtrace threadpool app.nettrace` |
| `diskio` | Physical disk I/O by file: bytes and disk service time (ETW) | `filtrace diskio app.etl` |
| `events` | Query raw events, filtered by name / payload / pid / tid, paged | `filtrace events app.etl --payload ConnectionReset` |

**Capture** (Windows, elevated) - record an ETW `.etl` yourself, no external recorder:

| Verb | Purpose | Example |
|---|---|---|
| `collect` | Launch an executable and record a CPU / thread-time `.etl` | `filtrace collect --launch bin/Release/net10.0/MyApp.exe --output myapp.etl --metric threadtime` |

```pwsh
filtrace collect --launch bin/Release/net10.0/MyApp.exe --output myapp.etl              # CPU
filtrace collect --launch dotnet --launch-args MyApp.dll --output tt.etl --metric threadtime
filtrace collect --launch MyApp.exe --output ring.etl --max-size-mb 512                 # bounded ring buffer
```

For an EventPipe (`.nettrace`) capture - cross-platform, no elevation - use the
first-party `dotnet-trace` (`dotnet tool install -g dotnet-trace`, then
`dotnet-trace collect -- <app>`); `collect` is ETW-only.

**File ops** - manage the ETLX conversion cache TraceEvent keeps beside a trace:

| Verb | Purpose | Example |
|---|---|---|
| `convert` | Build the ETLX cache up front | `filtrace convert app.nettrace` |
| `clean` | Remove the ETLX cache to force a rebuild | `filtrace clean app.nettrace` |

Run `filtrace <verb> --help` for the full option set of any verb.

<!-- filtrace:begin agents-snippet -->
## Using filtrace from an AI agent

filtrace is built for an agent mid-investigation. Two ways to wire it in:

- **MCP server** - add the stdio server so the agent calls the `trace_*` tools
  directly:

  ```json
  {
    "servers": {
      "filtrace": {
        "type": "stdio",
        "command": "dnx",
        "args": ["KlutzyNinja.Filtrace.Mcp", "--yes"]
      }
    }
  }
  ```

- **CLI** - install the global tool (`dotnet tool install -g KlutzyNinja.Filtrace`)
  and let the agent shell out to `filtrace <verb>`.

Either way, the canonical loop is **orient -> rank -> drill -> compare**: read
`trace_info` (CLI: `filtrace info`) first; when symbol resolution is below 0.8,
inspect its warning and unresolved rows; use local PDBs for source lines or native
symbols for CPU ETW runtime frames as applicable. Rank by the metric that matches the question (cpu, alloc, exceptions,
threadtime, contention, wait, activity); for an unwindowed CPU ranking, drill the
hot frame with callers / lines / tree; diff comparable CPU traces against a baseline.
<!-- filtrace:end agents-snippet -->

## Layout

| Path | Purpose |
|---|---|
| `src/Filtrace.Core/` | Analysis core: trace readers, stack-source providers, the provider-agnostic question-service engine. The only place logic lives. |
| `src/Filtrace/` | CLI host, packaged as the `filtrace` .NET global tool. |
| `src/Filtrace.Mcp/` | Stdio MCP host, packaged separately for `dnx KlutzyNinja.Filtrace.Mcp`. |
| `tests/Filtrace.Core.Tests/` | Unit + golden-file contract tests. |
| `tests/Filtrace.Parity.Tests/` | Numeric parity against the frozen legacy oracles. |
| `eval/` | Headless-agent eval harness, tasks, baselines (M5). |
| `docs/` | Single-source workflow text for the skill / README / help (M4). |
| `.agents/skills/filtrace/` | The shipped agent skill. |

## Self-containment

filtrace carries its own `Directory.Build.props`, `Directory.Build.targets`,
`Directory.Packages.props`, `global.json`, and `.editorconfig` (`root = true`),
so the build is fully self-contained. Its only external dependency is the
published `KlutzyNinja.Touki` NuGet package; it references no other project.

## Build and test (standalone)

```pwsh
cd filtrace
dotnet build filtrace.slnx
dotnet test filtrace.slnx
```
