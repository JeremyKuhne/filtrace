# filtrace

A small, agent-shaped CLI and MCP server for analyzing .NET CPU/memory/wall-clock
traces - the productized successor to `touki.mcp`. Built on the
`Microsoft.Diagnostics.Tracing.TraceEvent` library; reads EventPipe
(`.nettrace` / `.speedscope.json`) and ETW (`.etl`) captures from both .NET and
.NET Framework runs.

> **Status.** filtrace was extracted from the
> [`touki`](https://github.com/JeremyKuhne/touki) repository, where it incubated,
> into this standalone repository with its history preserved. It is pre-1.0; the
> surface may still shift.

## Using filtrace

Every verb takes a trace path and prints a dense text report (or compact JSON
with `--format json`). The canonical investigation is **rank -> drill -> compare**:
find the hot frames, drill into one, then diff against a baseline.

```pwsh
# Workflow: rank the hottest frames, drill into one, then diff two runs.
filtrace cpu app.nettrace                      # 1. what's hot (self-time)
filtrace callers app.nettrace MyApp.Parse      # 2. who calls the hot frame
filtrace lines app.nettrace --symbols bin/Release/net10.0   # 3. hot source lines
filtrace diff before.nettrace after.nettrace   # 4. what changed between runs
```

Install the CLI as a .NET global tool (`dotnet tool` ships with the .NET 10 SDK):

```pwsh
dotnet tool install --global KlutzyNinja.Filtrace
filtrace cpu app.nettrace
```

Or build and install it from source:

```pwsh
dotnet pack src/Filtrace/Filtrace.csproj -c Release
dotnet tool install --global --add-source ./artifacts/packages KlutzyNinja.Filtrace
```

### MCP server

The same analysis is also exposed as a stdio MCP server (thirteen `trace_*`
tools). See [Using filtrace from an AI agent](#using-filtrace-from-an-ai-agent)
below for the client config and the tool workflow.

### Verbs

**Ranking** - rank stacks by a metric (`--metric` on `rank`, or a shortcut verb):

| Verb | What it ranks | Example |
|---|---|---|
| `rank` | Any metric (`--metric cpu\|alloc\|exceptions\|threadtime`) | `filtrace rank app.nettrace --metric alloc` |
| `cpu` | CPU self-/inclusive-time | `filtrace cpu app.nettrace --measure inclusive` |
| `alloc` | Bytes allocated, by site | `filtrace alloc app.nettrace --top 10` |
| `exceptions` | Throw sites, by count | `filtrace exceptions app.nettrace` |
| `threadtime` | Wall-clock (running + blocked), Windows `.etl` | `filtrace threadtime app.etl` |

Every ranking verb accepts `--root` (scope to a frame subtree) and `--benchmark`
(scope a BenchmarkDotNet capture to the measured workload, past the harness). The
verbs that can read a multi-process ETW `.etl` - `cpu`, `threadtime`, and `rank`,
plus the drill-down `callers`, `lines`, and `heatmap` - also accept `--process` /
`--all-processes` (the busiest process tree, ranked by CPU sample count, is
auto-scoped by default); `alloc` and `exceptions` read single-process
`.nettrace` only, so they have no process options. To see what is in a
multi-process capture before scoping, run `filtrace processes` (below).

```pwsh
filtrace cpu bdn.nettrace --benchmark          # just the [Benchmark] code
filtrace alloc bdn.nettrace --benchmark        # allocations under the workload
filtrace processes machinewide.etl             # list every process by weight
filtrace cpu machinewide.etl --process MyApp   # one process tree
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

**Drill-down** - follow a ranking into detail:

| Verb | Purpose | Example |
|---|---|---|
| `callers` | Immediate callers of a frame | `filtrace callers app.nettrace MyApp.Parse` |
| `lines` | Hottest source lines of scoped methods | `filtrace lines app.nettrace --symbols bin/Release/net10.0` |
| `heatmap` | Per-line heat for one source file | `filtrace heatmap app.nettrace Parser.cs` |
| `tree` | Top-down call tree from the root | `filtrace tree app.nettrace --max-depth 5` |

**Inventory** - see what a (possibly machine-wide) capture contains:

| Verb | Purpose | Example |
|---|---|---|
| `processes` | List processes by CPU-sample weight, to pick a `--process` target | `filtrace processes machinewide.etl` |
| `classify` | Summarize CPU time by runtime work category (zeroing / copying / GC / ...) | `filtrace classify app.etl --native-symbols` |

**Compare and export:**

| Verb | Purpose | Example |
|---|---|---|
| `diff` | What got slower/faster between two traces | `filtrace diff before.nettrace after.nettrace` |
| `export` | Write a flame graph (speedscope / chromium) | `filtrace export app.nettrace --format speedscope -o app.json` |

**Structured reports** (EventPipe `.nettrace`):

| Verb | Purpose | Example |
|---|---|---|
| `gcstats` | GC counts, pauses, heap summary | `filtrace gcstats app.nettrace` |
| `jitstats` | JIT method count, compile time, sizes | `filtrace jitstats app.nettrace` |
| `events` | Query raw events by name, paged | `filtrace events app.nettrace --name GC/AllocationTick` |

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
`trace_info` first and trust the rankings only when the symbol-resolution rate is
at or above 0.8; rank by the metric that matches the question (cpu, alloc,
exceptions, threadtime); drill the hot frame with callers / lines / tree; diff
against a baseline to see what changed.
<!-- filtrace:end agents-snippet -->

## Layout

| Path | Purpose |
|---|---|
| `src/Filtrace.Core/` | Analysis core: trace readers, stack-source providers, the provider-agnostic question-service engine. The only place logic lives. |
| `src/Filtrace/` | CLI host (`filtrace`); the `filtrace mcp` verb hosts the server. |
| `src/Filtrace.Mcp/` | Thin shim package over the same core assembly. |
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
