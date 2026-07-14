# filtrace ETL trimming

An investigation note on physically shrinking an ETW `.etl` by relogging it down
to one process tree, why filtrace still wants that even though analysis-time
`--process` scoping is lossless, and the one limitation that keeps the trim in the
fixture tool rather than shipped as a verb.

The trim itself lives in the `Trim` method of the fixture generator
([../fixtures/HotLoopBench/Program.cs](../fixtures/HotLoopBench/Program.cs)); it is
invoked by [../fixtures/capture-diskio.ps1](../fixtures/capture-diskio.ps1) to turn a
raw machine-wide disk capture into the committed `diskio.etl` smoke fixture.

## Why physically trim at all

filtrace already scopes a machine-wide capture to one process tree at *analysis*
time with `--process` (see [trap 3](traps.md)), and that path is **lossless** - it
reads the full trace and keeps every managed stack. So trimming is never needed for
analysis fidelity. It is still wanted for two other reasons:

- **Transport.** A machine-wide `.etl` is routinely hundreds of megabytes to
  gigabytes (see below); a per-scenario trace of a few hundred kilobytes is what you
  can commit as a test fixture, attach to an issue, or hand to another machine.
- **Repeated work.** Scoping at analysis time re-filters the whole trace on every
  command. A file trimmed once is cheap to read many times.

The concrete case that motivated the current trim: a disk-I/O capture of the
`HotLoopBench` workload was **1.16 GB / 13.7M events**; trimmed to the process tree it
became **about 365 KB / ~11,000 events** - small enough to commit as `diskio.etl`.

## Where the size comes from

ETW kernel tracing is machine-wide, so trace size is driven far more by *what
keywords are enabled* than by how long the capture runs:

- The **File/Disk name rundowns** (the `DiskFileIO` / `FileIO` keywords) enumerate
  every open file object on the machine at session start and stop - hundreds of
  thousands of `FileIo/Name` events (650K+ in the case above) - independent of the
  capture window. This rundown, not the workload's own I/O, is the bulk of a disk
  capture.
- Machine-wide `DiskIO`, context switches, and CPU samples from every other process
  on the box add the rest.

The workload's own events are a rounding error against that. Trimming to the process
tree is therefore mostly about *dropping the rundown and the other processes*.

## What the trim keeps

The relog (`ETWReloggerTraceEventSource`) copies only the events belonging to the
target process tree - the process whose name matches, plus its descendants (the
default, because BenchmarkDotNet and "profile my app" both run the real work in child
processes). Getting a *usable* trimmed trace took three non-obvious steps:

1. **CPU samples, context switches, and their stacks** are attributed by thread, so
   they are kept by thread id. A kernel stack-walk event carries no reliable owning
   thread, so it is matched to its target event by timestamp - exactly how `TraceLog`
   folds stacks onto events. Compressed (stack-key) stacks additionally need their
   `StackWalkKeyRundown` definitions kept for the referenced keys.
2. **DiskIO completions** are misattributed by the kernel to whichever process was
   running when the I/O completed (frequently Idle or System), *not* the issuer. A
   plain by-process filter would drop the target's own disk activity. They are
   correlated back to the issuing thread by **IRP**: keep each `DiskIOInit` logged in
   a kept thread's context, record its IRP, and keep the completion whose IRP matches.
3. **File names** only resolve if the name-rundown entry for each touched file
   survives. The kept DiskIO events' `FileKey`s are collected in a first `TraceLog`
   pass, and only the matching `FileIONameTraceData` entries are kept - so the trimmed
   disk events still resolve to file names while the rest of the system-wide rundown
   is dropped.

## Known limitation: managed frames do not survive the relog

The raw `ETWReloggerTraceEventSource` re-injects events but does **not** rebuild the
managed-method address map that a full `TraceLog` conversion builds. Even with the
complete CLR method/module rundown preserved, a trimmed `.etl` resolves native
modules but shows JITted managed methods as an unresolved `?` frame (a `threadtime`
of the trimmed disk fixture credited ~804 ms to `?`). So the current trim is a
**native-only file shrink**, fine for a by-file `diskio` report or a native-frame
view, but not a substitute for analyzing the full trace when managed stacks matter.

This is why the trim is a fixture-generation tool, not a shipped verb: the
lossless path (`--process` at analysis time) already covers the analysis case, and a
shipped physical trim would ship this limitation with it.

## Future shipping decision

Physical trim is tracked only as VC7 in the canonical
[v.next roadmap](vnext-improvement-plan.md#vc7---physical-etl-trim). The earlier
TraceEvent assessment called the process-tree relog TE-14 and the time-window axis
TE-15. Analysis-time `--time` scope has since shipped as the lossless way to inspect
a spike; only a physical `[t0, t1]` relog remains part of the potential transport
feature.

A user-facing trim should combine process-tree and optional time-window selection,
state that it is for transport/fixtures rather than analysis fidelity, and either:

1. rebuild enough managed method/module mapping for JITted frames to resolve; or
2. explicitly limit the output contract to native/event scenarios such as disk I/O.

Resolving the managed-method rebuild is what would raise physical trim from a
specialized transport convenience to a general lossless shrink. Until then, keep
the relogger in fixture generation and use analysis-time process/time scope for
normal investigations.
