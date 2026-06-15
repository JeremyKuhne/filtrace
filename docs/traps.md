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

2. **Trust the symbol-resolution rate before the rankings.** A rate below **0.8**
   (surfaced by `trace_info` / the load warning) means managed frames are missing
   and the names are unreliable - pass `--symbols <build-output-dir>`. Caveat: the
   aggregate rate conflates managed and native frames, so a net481 ETW capture can
   read low while every *managed* leaf resolves correctly; the warning hedges this
   ("managed-method rankings remain usable").

3. **On a machine-wide `.etl`, confirm the process before scoping.** filtrace
   auto-scopes to the busiest process tree ranked by **CPU-sample count** (a
   long-lived background service wins a wall-clock race but owns few samples), and
   that default is usually right - but run `processes` first to see what is in the
   capture, then pass `--process <name>` if the auto-pick is wrong.

4. **BenchmarkDotNet captures include the harness.** A raw ranking of a BDN trace
   is dominated by the orchestrator and warmup iterations, not your `[Benchmark]`.
   Pass `--benchmark` to preset the root to the measured-workload wrapper so only
   the measured code is ranked.

5. **Native runtime frames need `--native-symbols`.** Without it, the unmanaged
   ~10% of a trace - GC, JIT, `memset` / `memcpy`, write barriers - shows as an
   unresolved `?` leaf. Opt in (CPU `.etl` only; fetches PDBs from the Microsoft
   public symbol server, cached locally) to name it, then `classify` to get the
   zeroing-vs-copying-vs-GC-vs-JIT split. It is off by default so analysis stays
   offline and deterministic.

6. **Self-time and inclusive-time answer different questions.** Self-time finds
   the leaf that burns the resource; inclusive-time finds the subtree that drives
   it. Ranking by the wrong measure hides the frame you want - start with self for
   "what is hot", switch to inclusive for "what is responsible".

7. **Reading an `.etl` is Windows-only.** The ETW -> ETLX conversion needs
   Windows; once converted, the `.etlx` resolves managed frames and analyzes
   identically on any OS ("convert on Windows, analyze anywhere"). The CLI/MCP
   `.etl` paths are guarded and report a clean error off Windows.

8. **The default fold list hides runtime leaves on purpose.** It folds
   `memmove`, write-barriers, and GC-poll helpers into their managed caller -
   right for "which method is hot", wrong for "what kind of work dominates". Use
   `--no-fold` (or `classify`) to let the native leaves rank on their own.
<!-- filtrace:end traps -->
