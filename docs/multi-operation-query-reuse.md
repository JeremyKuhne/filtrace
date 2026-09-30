# Multi-operation query reuse: evidence and CLI choices

**Status:** Measured design record, 2026-09-30. No multi-operation CLI command
has been approved or shipped. The private probe is not a public interface.

## Executive summary

- Two fresh CLI processes repeat work even when they use the same warm ETLX.
  In an isolated `info` then CPU `rank` prototype, one process with one
  `TraceStore` saved a **median paired 1,189 ms** on a 445 MB ETL (10 pairs)
  and **209 ms** on a small EventPipe trace (5 pairs). Both complete JSON
  envelopes matched the two-process outputs on every pair. Median peak
  sampled private memory was effectively unchanged: **276.4 to 275.9 MiB**
  and **22.2 to 22.3 MiB**, respectively.
- **MCP already realizes this kind of reuse.** The stdio server registers one
  `TraceStore` for its lifetime, and `trace_info` and `trace_rank` receive that
  same instance. A separate three-query measurement found median paired
  savings of **1,445 ms** on the ETL and **129 ms** on the small trace versus
  three fresh CLI processes, *including* MCP startup and initialization.
  Those three-query totals are not estimates of the two-query CLI feature.
- One process alone is not the whole win. A no-store control saved only
  **333 ms** on the large trace and peaked at **360.8 MiB** of sampled private
  memory, versus **275.9 MiB** for the shared-store process. On the small
  trace, single-process effects (including avoiding a second launch)
  explain most of the saving.
- **Recommendation:** For an MCP-capable agent, use the existing typed
  `trace_*` tools rather than add another tool or a CLI daemon. Keep the
  existing single-purpose CLI commands. Consider a bounded, read-only CLI
  multi-operation interface only if a *CLI-only* agent or automation scenario
  demonstrates a better end-to-end outcome without degrading complete answers
  at acceptable command-discovery, repair, token, wall-time, and memory cost.
  Do not ship the probe's static store or hidden switches.

This is a measurement and choice record, not a new implementation queue; the
[roadmap](roadmap.md) remains the source of truth for active work.

## What is reused, and what MCP already does

The CLI's [TraceExecution.TryLoad](../src/Filtrace/Cli/TraceExecution.cs) creates
a `TraceStore` for a load, and ordinary separate `filtrace info` and
`filtrace rank` commands start separate processes. An adjacent ETLX avoids
reconversion but does not retain the parsed analysis model across those
processes.

The MCP [host](../src/Filtrace.Mcp/Program.cs) instead registers
`TraceStore` as a singleton. Its [tools](../src/Filtrace.Mcp/TraceTools.cs)
take that instance as an injected argument and use it for successive loads.
The [store](../src/Filtrace.Core/Server/TraceStore.cs) caches parsed views by
source identity (including file facts), metric, effective scope, symbols, and
resolution options. Matching `trace_info` and `trace_rank` requests can reuse
the CPU view without another parse. Other metrics, scopes, or symbols can
require another view; sharing a process is not a promise of a hit. The store
has a bounded entry count, not a byte-based memory limit.

Before the prototype, an alternating comparison ran `info`, CPU `rank`, and
`timeline --mode buckets --buckets 50` as three fresh CLI processes versus
three calls to one MCP stdio server. The MCP total **includes process start,
initialize, and tools/list** (median setup about 333 ms on the large trace
and 340 ms on the small trace):

| Input | Pairs | Three CLI calls, median | One MCP server, median | Median paired saving | Median peak sampled private, CLI / MCP |
| --- | ---: | ---: | ---: | ---: | ---: |
| Large warm ETL | 10 | 4,673 ms | 3,205 ms | 1,445 ms | 283.7 / 395.0 MiB |
| Small EventPipe | 5 | 717 ms | 596 ms | 129 ms | 23.5 / 39.7 MiB |

On the large trace, `rank` alone took a median 1,304 ms as a fresh CLI
process but about 42 ms as the second MCP call after `info`. MCP `timeline`
still did substantial additional work; not every later tool call is just a
cache lookup. Numeric results and context matched in all 15 pairs. Some CLI
and MCP advisory hints differ, so this is **not** a claim of byte-identical
three-operation envelopes. The MCP memory figures include its server and all
three operations; do not compare them directly with a two-query CLI run.

This mechanism already gives agents with a suitable MCP client the key
parsed-model benefit, without introducing a new `trace_*` tool. The published
MCP tool names are frozen, and `tools/list` is already about 6,928 estimated
tokens against a 7,000-token gate ([design gates](design.md#enforced-gates)).

## Private one-process prototype

The experiment used a detached worktree from `ae608a01a2ac469ba22cc4e9f130848b076cf0f2`.
It changed only the scratch CLI's [entry point](../src/Filtrace/Program.cs)
and load seam; it did not change production `main`. A hidden probe called
the existing `info <trace> --format json` and
`rank <trace> --metric cpu --measure self --top 25 --format json` executors
sequentially and emitted their two original envelopes. The shared variant
supplied one store for both calls. A control used the **same built CLI** and
the same one-process sequence but a new store for each call. Neither hidden
switch appeared in normal CLI help.

The 445 MB ETL and the small EventPipe trace both had ready, isolated ETLX
caches. Each arm used the same local Release binary, with one excluded warmup
and alternating first-arm order. The following medians exclude warmups.
Savings are medians of **paired differences**, so they need not equal the
difference between displayed arm medians.

| Input and one-process variant | Pairs | Two fresh / one process wall | Median paired saving (range) | `rank` fresh / one process | Child CPU fresh / one process |
| --- | ---: | ---: | ---: | ---: | ---: |
| Large ETL, **shared store** | 10 | 2,402 / 1,226 ms | **1,189 ms** (925-1,393) | 1,229 / 42 ms | 2,422 / 1,297 ms |
| Large ETL, no store | 10 | 2,335 / 2,020 ms | 333 ms (106-580) | 1,252 / 796 ms | 2,375 / 2,164 ms |
| Small EventPipe, **shared store** | 5 | 447 / 246 ms | **209 ms** (197-214) | 226 / 19 ms | 406 / 219 ms |
| Small EventPipe, no store | 5 | 452 / 263 ms | 186 ms (182-207) | 227 / 37 ms | 422 / 234 ms |

| Input and one-process variant | Peak sampled private, fresh / one | OS peak working set, fresh / one |
| --- | ---: | ---: |
| Large ETL, **shared store** | 276.4 / 275.9 MiB | 264.1 / 264.8 MiB |
| Large ETL, no store | 276.5 / 360.8 MiB | 264.2 / 350.7 MiB |
| Small EventPipe, **shared store** | 22.2 / 22.3 MiB | 53.4 / 54.3 MiB |
| Small EventPipe, no store | 22.2 / 24.3 MiB | 53.3 / 56.2 MiB |

Wall time runs from launch to the last response; the first compound JSON
line marks the `info`/`rank` split. CPU sums the owned children for the
two-process arm. `PrivateMemorySize64` was sampled every 10 ms, so its peak
is a **lower bound**, not retained managed heap. The two-process memory
number is the **maximum of the sequential child peaks**, not their sum;
`PeakWorkingSet64` is the OS peak, not the GC heap. All four final batches
used one unchanged managed CLI DLL and checked both whole JSON envelopes
(including warnings, hints, context, counts, and units) in each of their
30 pairs. A previous, independently built shared-store-only prototype
repeated the positive outcome: 1,196 ms median paired saving for 10 large
pairs and 200 ms for 5 small pairs.

The large input selected 112,741 ETW CPU samples with established
millisecond weights. Its automatic `csc` scope grouped three unrelated
process trees and only 38% of frame names resolved: it tests query
performance and parity, **not** a defensible build hotspot conclusion.
The small input had 298 EventPipe SampleProfiler samples in the unit
`samples`, not milliseconds. Their CPU weights cannot be compared across
the two formats.

The difference between the large shared-store and no-store **one-process
medians** is about 795 ms. These variants ran in separate batches, not
directly paired against each other; that difference is indicative, not a
confidence interval for model reuse. The no-store memory peak is consistent
with duplicate in-process model work, but is not a heap-ownership profile.
The no-store saving over two launches can also include JIT and other
process-local effects; it is not an isolated startup-time measurement.
The test does not cover cold ETLX conversion, other metrics, scoped or
symbol-resolved queries, concurrency, or failure after the first result.

The scratch CLI passed 399 CLI tests with zero failures and two skips
(an elevated-host negative case and an unbuilt native workload). Both
hidden modes returned exit code 2 and no JSON envelope for a missing
trace. This verifies a probe, **not** the lifetime, partial-failure, and
discovery contracts of a public multi-operation interface.

### Evidence and reproduction boundary

Private evidence root: `N:\perf-corpora\filtrace-query-reuse-20260929`.
The source ETL and its private copy have SHA-256
`3F44F6549F352518F37971052848DAB96D63CC6DFF4159F3F39025A6006222FF`;
the small input has SHA-256
`C4DBDE2BB928851C7AD6ACF76FFF5EEFE7D294F7466032198ADA8A710D5048EB`.
The final candidate's **managed** `filtrace.dll` SHA-256 is
`FF1E96AB0BD73B917E48062D22FF871F88AFA9548A4EAFC7149B32122EB9F81B`;
hashing only its apphost would not identify the changed managed code.

- `results/summary.json` is the preceding three-query CLI/MCP comparison,
  with per-pair paths, argv, input identities, timing, and memory.
- `results/compound-prototype-decision.json` indexes the four final batches
  (`shared-v2-large-10pairs`, `no-store-v2-large-10pairs`,
  `shared-v2-activity-5pairs`, `no-store-v2-activity-5pairs`), hashes, method,
  exact-output checks, pilots, and limitations. Each batch's `result.json`
  contains all pairs and their exact argv.
- `prototype-info-rank.patch` and
  `prototype-with-no-store-ablation.patch` retain the scratch-only source
  variants; `harness-compound/Program.cs` and `MeasureCompound.csproj` retain
  the runner. Rebuild against the recorded revision and use the *same build*
  for both arms of a comparison.

The archived machine-wide trace, converted ETLX, and raw outputs remain
private, outside this repository. Do not add them, install a different
global Filtrace to replay them, or treat this local path as a public fixture.

## Multi-operation interface choices

These are **options, not shipped syntax**. The existing `batch` /
`trace_batch` means *one ranking across capture-manifest cases*; reusing
that name for several operations on one trace would mislead agents and
break its contract ([workflow](workflow.md)).

| Choice | Benefit | Cost or limitation for an agent | Assessment |
| --- | --- | --- | --- |
| **Use existing MCP `trace_*` calls** | Typed, discoverable operations; one server and one cached store; later calls can follow earlier results. No CLI grammar or new permanent tool schema. | Needs an MCP-capable client, retains a server and its cache; CLI-only capture, cache operations, and some scope widening remain separate. | **Preferred for repeated agent analysis where available.** Do not add a universal MCP `trace_query`. |
| **Narrow CLI preset**, for example a fixed `info` plus CPU `rank` | Simple invocation for a proven, predictable two-query need; can preserve each existing envelope. | Pays for both outputs even when `info` shows CPU is unavailable, another scope is required, or only one query was needed. Each new preset competes for CLI discovery and help space. | Consider only if a real CLI-only task frequently requires exactly that fixed pair. Do not multiply presets for combinations. |
| **Typed, bounded CLI query plan** (conceptually a distinct plan command accepting a file or stdin) | Shares one per-invocation store across a small, declared sequence; scripts can choose pre-known read-only operations without shell-specific argument chaining. | An agent must construct, validate, quote, and possibly persist a plan; a plan can add tokens and repair calls. A static plan cannot choose a caller drill from a frame discovered by an earlier rank. New schema, stdout, failure, and help contracts are substantial. | Most flexible **CLI-only** candidate, conditional on an observed end-to-end agent or automation win. Start with one trace and a small allowlist, not an arbitrary command runner. |
| **Chained verb/flag syntax** such as a hypothetical `--then` | Short for a human typing a fixed chain. | Duplicates command parsing, blurs per-step options and scopes, worsens quoting and help, and invites agents to invent unsupported combinations. | Do not pursue without evidence it beats a typed plan or preset. |
| **Persistent CLI session / daemon / REPL** | Reuses memory across unrelated shell invocations. | Duplicates MCP's lifetime, protocol, cleanup, concurrency, and cache-invalidating responsibilities; adds a third agent discovery path. | Not justified by this experiment. |

If a CLI plan is chosen, its first design decision is **not its name** but
its read-only and failure boundaries: one trace; explicit allowed canonical
operations and typed options; bounded step count and aggregate response;
an explicit policy for shared versus per-step process/scope/symbol axes;
no arbitrary argv, shell, `collect`, `clean`, `trim`, or `export` execution.
Existing analysis can still prepare an ETLX cache; "read-only" here excludes
new mutation commands, not all disk writes.
Preserve the original `schemaVersion`, result, context, warnings, and hints
of each step. Define how step identity, ordered results, stdout framing
(one bounded document or JSON lines), exit status, and a partially successful
sequence compose: a failing sequence needs a nonzero aggregate exit and an
explicit failure record, never a success-shaped aggregate. Decide whether
a failed first `info` prevents `rank` before implementing it.
Lifetime should be owned by the invocation with a bounded `TraceStore`,
**not** the probe's global static property. Keep source/cache identity and
existing CLI exit codes intact. These are unresolved design choices, not
implicit defaults.

## Agent effectiveness is a separate gate

A saved process launch is not automatically an easier investigation.
[The design](design.md#efficacy-measures) judges task correctness, expected
operation selection, median and p95 tool calls, total context tokens,
repair calls, and wall time together. The deterministic
[eval](../eval/README.md) has 31 fixture-backed tasks, most answered in one
call, with a six-call goal and bounded output; live-agent runs catch
misunderstood commands, missing skill use, and false confidence. Past agent
comparisons did **not** establish a general reduction in effort from another
interface ([durable findings](design.md#agent-evidence-and-interface-decisions)).

In particular:

- The normal `info`-first workflow is adaptive. A process or provider
  warning can change the next operation; a premature fixed plan could
  repeat the wrong automatic scope or bury its warning in a larger output.
  An interactive rank-to-caller drill needs the returned frame name first.
- Fewer CLI processes need not mean fewer *agent* actions: discovering a
  new command, constructing a JSON plan, repairing a rejected option, and
  reading extra results all consume calls or context. An MCP client already
  gets reuse without teaching an agent another surface.
- A new canonical CLI command must remain discoverable in top-level help
  and the README, while preserving the top-level baseline of at most 27 lines
  and 2,171 characters, and the 60-line per-verb budget
  enforced by [Test-CliHelp.ps1](../tools/Test-CliHelp.ps1). Hiding a
  production command to pass the help gate is not a solution. Adding an
  MCP tool instead consumes permanent `tools/list` context for every agent.
- Keep per-step envelopes and scope provenance visible; do not replace
  warnings with one success summary. Bound both individual and aggregate
  output so a combined response cannot swamp the agent's context.

Before a product decision, select **actual CLI-only tasks** with pre-known
multi-operation needs, plus small and large traces, named-process ETW cases,
unsupported providers, and a dynamically chosen follow-up. Compare the
existing CLI, the proposed CLI shape, and MCP where supported using the
same binary and trace per arm. Test output parity, partial failures, warmed
and first-use caches, absolute time and memory, command selection, answer
quality, token/call/repair budgets, and whether the candidate was actually
available to the agent. Run the repository's deterministic contracts and
repeat live-agent trials across models before claiming agent efficacy.
There is **no automatic percentage acceptance floor** for this prototype;
report the absolute benefit and tradeoff at the measured boundary.
