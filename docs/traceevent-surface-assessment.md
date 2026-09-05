# TraceEvent surface assessment

**Status:** Dependency reference for the pinned package. It records what
`Microsoft.Diagnostics.Tracing.TraceEvent` **3.2.6** (`lib/netstandard2.0`) does and
does not provide, and which [roadmap.md](roadmap.md) items that gates.

**Last verified:** 2026-09-05. The missing-type inventory and unused exception, GC,
and PMC members were rechecked from the four restored assembly metadata tables.
Capture measurements remain historical observations, not new measurements of this
package version.

This is not a second roadmap. Schedule and priority for every unshipped item belong
only in [roadmap.md](roadmap.md); the design constraints they are judged against are
in [design.md](design.md).

## What filtrace builds on today

| filtrace family | TraceEvent surface |
|---|---|
| CPU | `SampledProfileTraceData`, `ClrThreadSampleTraceData`, `TraceLog.CodeAddresses` |
| Thread time | `ThreadTimeStackComputer` (emits `CPU_TIME`, `DISK_TIME`, `HARD_FAULT`, `NETWORK_TIME`, `READIED` leaves) |
| Contention / wait | `ContentionLatencyComputer`, `WaitHandleWaitLatencyComputer` - both `StartStopLatencyComputer` derivatives driven through `GenerateStacks()` |
| Activity | `StartStopActivityComputer`, `GetCurrentStartStopActivity` |
| Allocation | `GC/AllocationTick` |
| Exceptions | `ExceptionTraceData` |
| GC | `TraceGC` on the runtime model |
| JIT / thread pool | CLR method and `ThreadPoolWorkerThreadAdjustment` events |
| Disk I/O | `KernelTraceEventParser` `DiskIO/Read`, `DiskIO/Write`, `FileIO` name rundown |
| Lifecycle | kernel `Process/Start`, `Process/Stop`, image-load events, `ProcessIndex` |
| Raw events | `TraceLog.Events`, `TraceLog.OpenOrConvert` (identical loop for `.nettrace` and `.etl`) |
| Capture | `TraceEventSession`, `KernelTraceEventParser.Keywords`, `TraceEventProfileSources` |
| Fixture trim | `ETWReloggerTraceEventSource` |

## Verified absent from 3.2.6

Each of these was checked across all four DLLs in the package
(`TraceEvent`, `FastSerialization`, `Dia2Lib`, `TraceReloggerLib`). They are the
reason several roadmap items are dependency-gated rather than merely unwritten.

| Missing | Consequence |
|---|---|
| `MemoryGraph`, `GCHeapDump`, `Graph`, `RefGraph`, `MemoryGraphStackSource`, any `Graphs` namespace | Retention / leak analysis (VC5) needs a separate PerfView-side source set. `dotnet-gcdump` vendors roughly 173 KB of it as read-only MIT source copied from PerfView, because factoring it into TraceEvent "proved to be too disruptive". Path-to-root analysis is *not* in that vendored set and would be filtrace-authored on the `RefGraph` primitive. It also needs `AllowUnsafeBlocks`, and its trimming/AOT posture is unverified. |
| `GCHeapSimulator` | Net surviving heap (VC6) stays a PerfView lift, not a provider addition. |
| `XmlStackSourceWriter` | `export --format perfview` would be PerfView-side work. Do not promise it cheaply. |

`FastSerialization` *is* bundled in the package, so that layer of a future graph
port is free. `ZippedETLWriter` *is* present, which is relevant to a symbols-bundled
`.etl` handoff and to the parked physical trim (VC7).

## Present but unused

Payload filtrace already reads past, and could surface without new capture:

- `ExceptionTraceData.ExceptionMessage` - the type is surfaced as a synthetic leaf;
  the message is discarded.
- `TraceGC.GlobalCondemnedReasons`, `.PauseTimePercentageSinceLastGC`, and
  `.SuspendDurationMSec` - the GC report surfaces reason, type, generation,
  promoted MB, and percent time in GC, but not these.
- `ProfileSourceInfo` and `PMCCounterProfTraceData` - fully supported analysis-side;
  PMC ranking (VC4) is gated on the ETW capture path and a fixture, not on the
  library.

These are VC8-class enrichments in [roadmap.md](roadmap.md).

## Capture-side facts worth keeping

- **`KernelTraceEventParser.Keywords.Default` is `0x0101270F`** - `Process | Thread
  | ImageLoad | ProcessCounters | DiskIO | DiskFileIO | DiskIOInit |
  MemoryHardFaults | NetworkTCPIP | Profile`. It therefore enables the machine-wide
  file-name rundown even for a capture that reads none of it. That rundown is why
  `collect --profile startup` exists.
- **The `DiskFileIO` / `FileIO` name rundown enumerates every open file object on
  the machine at session start and stop** - measured at over 650,000 events - and its
  size is independent of the capture window. It, not the workload's own I/O,
  dominates a disk capture.
- **Windows reports the honored CPU sample interval range per profile source**
  through `TraceEventProfileSources`, as `MinInterval` / `MaxInterval` in
  100-nanosecond units, readable without elevation. `TraceEventSession`'s
  `CpuSampleIntervalMSec` getter and the `TraceQueryInformation` echo both report
  whatever was last *set*, so neither can detect a clamp; only the reported range
  or sample density can.

## Cache migration from 3.2.3

TraceEvent 3.2.6 requires ETLX format 78 and rejects format-77 caches from 3.2.3.
Filtrace rebuilds an unreadable adjacent cache from its raw `.nettrace` or `.etl`
when opening it, and reports `recovered`. Explicit cache conversion also verifies
that the returned cache can be opened. Failed regeneration preserves the previous
cache, and the raw trace is not replaced.

Keep separate trace/cache paths when comparing old and new binaries. A timestamp
alone does not make one version's ETLX readable by the other, and this recovery
does not establish standalone ETLX interoperability.

## Re-verifying

Reflect over the referenced assembly in a disposable PowerShell session - no
project needed. Loaded assemblies remain locked until that session exits:

```pwsh
$dll = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.diagnostics.tracing.traceevent\3.2.6" `
    -Recurse -Filter 'Microsoft.Diagnostics.Tracing.TraceEvent.dll' |
    Where-Object FullName -match 'netstandard2.0' | Select-Object -First 1
$asm = [System.Reflection.Assembly]::LoadFrom($dll.FullName)
try { $types = $asm.GetTypes() } catch { $types = $_.Exception.Types | Where-Object { $_ } }
$types | Where-Object { $_.IsPublic -and $_.Namespace -eq 'Microsoft.Diagnostics.Tracing.Computers' } |
    Sort-Object Name | ForEach-Object Name
```

When the pin in [../Directory.Packages.props](../Directory.Packages.props) moves,
bump the version above, re-audit the `Computers` namespace and the event surface,
and re-check the absent list. A new finding enters [roadmap.md](roadmap.md) only
after it is judged against agent value, capture feasibility, dependency cost, and
response bounds.
