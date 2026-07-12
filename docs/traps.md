# filtrace traps (single source)

The recurring ways a .NET trace investigation goes wrong, and what filtrace does
about each. This is the **single source** for the trap catalog; the shipped skill
embeds the marked block below verbatim and
[tools/Test-Docs.ps1](../tools/Test-Docs.ps1) guards the copy.

<!-- filtrace:begin traps -->
## Trap catalog

1. **Profile .NET Framework with ETW, never extrapolate from an EventPipe trace.**
   EventPipe (`.nettrace`) is modern-.NET-only and managed-only. The net10
   EventPipe ranking actively *misleads* for `net481`: weaker Framework inlining
   relocates the hot frame, so a method that is 1.5% self-time on the EventPipe
   trace can be 56% on the ETW (`.etl`) capture of the same workload. Capture
   net481 under ETW (`threadtime` / `cpu` over an `.etl`) and rank that.

2. **Treat low symbol resolution as a quality gate, not an automatic rejection.**
   A rate below **0.8** (surfaced by `trace_info` / the load warning) means unresolved
   frames need inspection. Managed method names normally come from CLR rundown;
   `--symbols <build-output-dir>` supplies matching PDBs for source lines, not a
   replacement for missing rundown. The aggregate rate conflates managed and native
   frames, so a net481 ETW capture can read low while every *managed* leaf resolves
   correctly; in that case managed-method rankings remain usable, and
   `--native-symbols` is the relevant opt-in when the native runtime split matters.

3. **On a machine-wide `.etl`, confirm the process before scoping.** filtrace
   auto-scopes to the busiest process tree ranked by **CPU-sample count** (a
   long-lived background service wins a wall-clock race but owns few samples), and
   that default is usually right - but run `processes` first to see what is in the
   capture, then pass `--process <name>` if the auto-pick is wrong.

4. **BenchmarkDotNet captures include the harness - scope with `--benchmark` by
   default, not as an afterthought.** A raw ranking (or export) of a BDN trace is
   mixed with orchestrator and overhead scaffolding outside your `[Benchmark]`.
   In the CLI, pass `--benchmark` to every verb that offers it; in MCP, pass
   `benchmark: true` to `trace_rank`, `trace_callers`, `trace_tree`,
   `trace_classify`, and `trace_export`. The wrapper includes warmup and actual
   workload iterations; it excludes harness/overhead scaffolding, not warmup. This
   applies especially to export - a flame graph with the harness left in is not just
   noisy, its proportions are wrong. Do not substitute a benchmark method substring:
   if root/frame warnings report multiple definitions or depths, narrow the selector
   before trusting the result. `lines` / `heatmap` cannot preserve root scope; narrow
   them with their method/file filter and treat percentages as whole-trace.

5. **Native runtime frames need `--native-symbols`.** Without it, the unmanaged
   share of a trace - GC, JIT, `memset` / `memcpy`, write barriers - shows as
   unresolved `?` leaves. Opt in (CPU `.etl` only; fetches PDBs from the Microsoft
   public symbol server, cached locally) to name them, then `classify` to get the
   zeroing-vs-copying-vs-GC-vs-JIT split. It is off by default so analysis stays
   offline and deterministic.

6. **Self-time and inclusive-time answer different questions.** Self-time finds
   the leaf that burns the resource; inclusive-time finds the subtree that drives
   it. Ranking by the wrong measure hides the frame you want - start with self for
   "what is hot", switch to inclusive for "what is responsible".

7. **Reading an `.etl` through filtrace is Windows-only.** The ETW -> ETLX
   conversion needs Windows, and direct `.etlx` input is not part of the current
   CLI or MCP surface. The `.etl` paths report a clean error off Windows.

8. **The default fold list hides runtime leaves on purpose.** It folds
   `memmove`, write-barriers, and GC-poll helpers into their managed caller -
   right for "which method is hot", wrong for "what kind of work dominates". Use
   `--no-fold` (or `classify`) to let the native leaves rank on their own.

9. **Trace the built app, not `dotnet run`.** `dotnet run` builds and then forks
   your program into a separate child process, so a single-process EventPipe
   session launched with `dotnet-trace collect -- dotnet run ...` records the
   build/run host, not your code, and the hot frames never appear. Build first,
   then launch the built output directly (`dotnet-trace collect -- dotnet
   <app>.dll`, or `dotnet-trace collect -- <apphost>`); the bundled
   `Capture-ProjectTrace.ps1` resolves that run target for you.

10. **A machine-wide `.etl` can be huge - capture lean, then scope at analysis.**
   ETW kernel tracing is machine-wide, so the wrong keywords balloon the file: the
   File/Disk *name* rundowns enumerate every open file on the box (hundreds of
   thousands of events that dwarf the workload) no matter how short the window.
   `filtrace collect` avoids this by design - it enables only the CPU (and, for
   `threadtime`, context-switch) keywords and stacks just the sampled events, never the
   File/Disk rundown - so prefer it and bound open-ended runs with `--duration` or
   `--max-size-mb` (a circular buffer that keeps the last N MB). Only a `diskio` capture
   needs the File/Disk keywords, and `filtrace collect` has no switch for them: that
   capture comes from another recorder (PerfView, `wpr`, or a custom BenchmarkDotNet
   `EtwProfilerConfig` enabling `DiskIO` / `DiskFileIO`; plain `-p ETW` is CPU-only),
   so expect the system-wide rundown there and trim it down afterward. To focus a big
   capture on your code, scope at *analysis* time with `--process` (lossless - it keeps
   managed stacks); physically trimming the file by relogging is a transport-only
   optimization that currently drops JITted managed frames.
<!-- filtrace:end traps -->
