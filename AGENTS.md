# AGENTS.md

Instructions for AI coding agents working in the **filtrace** repository. Applies
to GitHub Copilot, Claude Code, OpenAI Codex, Cursor, Aider, Gemini CLI, and any
other tool that supports the [AGENTS.md](https://agents.md/) standard.

## Project overview

`filtrace` is a .NET **trace analyzer**: a command-line tool and an MCP server
that rank, drill into, diff, and export CPU / allocation / exception /
thread-time profiles from `.nettrace`, `.etl`, and speedscope captures. It runs
on **.NET 10 only** - it reads traces produced by both modern .NET and .NET
Framework, but the analyzer itself targets net10.0.

Layout:

- `src/Filtrace/` - the CLI (`filtrace` command; ConsoleAppFramework verbs)
- `src/Filtrace.Core/` - the analysis library and public object model
- `src/Filtrace.Mcp/` - the MCP stdio server exposing the `trace_*` tools
- `benchmarks/Filtrace.Benchmarks/` - BenchmarkDotNet performance harness for the analysis core
- `benchmarks/Filtrace.PerfWorkload/` - parameterized CPU/activity trace workload for Track D
- `tests/Filtrace.*.Tests/` - unit and parity tests (Microsoft.Testing.Platform runner)
- `fixtures/` - HotLoopBench and the committed binary captures the tests read
- `tools/` - CI contract scripts (CLI help lint, MCP server check)
- `docs/`, `eval/`, `.agents/skills/` - single-source workflow text, eval harness, shipped skill

## Build and test

- `dotnet build filtrace.slnx -c Release`
- `dotnet test filtrace.slnx -c Release`
- `dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter *FoldingAggregatorBenchmarks*`
- `dotnet run -c Release --project benchmarks/Filtrace.Benchmarks -- --filter *TimelineProviderBenchmarks* --job short`

For deeper performance investigation, build and analyze with this checkout's CLI:

```pwsh
dotnet build src/Filtrace/Filtrace.csproj -c Release
$filtraceDll = (Resolve-Path src/Filtrace/bin/Release/net10.0/filtrace.dll).Path
$trace = (Resolve-Path tests/Filtrace.Core.Tests/Fixtures/threadpool.nettrace).Path
dotnet $filtraceDll info $trace
```

Do not use an installed global `filtrace` or the MCP server to profile this repository;
that can silently analyze with different code. For an A/B investigation, use one fixed
locally built baseline CLI to analyze both arms.

CI also runs nine contract and evaluation checks that must stay green:

- `tools/Test-CliHelp.ps1 -Configuration Release` - every canonical command appears
  in top-level help, hidden preview aliases remain callable but absent, each help
  stays within budget, and README examples use only canonical commands.
- `tools/Test-McpServer.ps1 -Configuration Release` - stdout is pure JSON-RPC,
  the tool-list schema stays within the token budget, and a real `tools/call`
  round-trips.
- `tools/Test-Docs.ps1` - shared workflow blocks, command/tool catalogs, and the
  packaged filtrace skill stay synchronized.
- `tools/Test-CaptureBenchmarkTrace.ps1` - run artifacts stay isolated, overlap
  is rejected, every case enters the manifest, and exact child symbols are used.
- `tools/Test-CaptureProjectTrace.ps1` - EventPipe recorder profiles are
  negotiated before build/launch and the effective recorder contract is retained.
- `tools/Test-FiltraceAnalysis.ps1 -Configuration Release` - decisive read-only
  queries retain exact arguments, input/output hashes, quality rejections, and
  replay refuses changed trace bytes before running.
- `tools/Test-TrackDInvestigation.ps1` - the Track D A/B wrapper reconstructs a
  neutral fake no-op, retains failed-run diagnostics, and gates its test adapter.
- `eval/Invoke-Eval.ps1 -Configuration Release` - canonical trace tasks keep
  their answers, call counts, and output budgets.
- `tools/Test-AgentSkills.ps1 -VerifyUpstream -ReferenceValidation` - commons
  cores match the v0.14.0 artifacts, and their overlays, metadata, readability,
  and links are valid.

## Frozen contracts - do not rename

- **The `trace_*` MCP tool names** (`trace_rank`, `trace_callers`, `trace_lines`,
  ...) are the public tool contract that agent clients bind to. You may add
  tools, but do not rename or remove existing ones without a deliberate
  breaking-change decision.
- **The `TraceQ.Fixtures.HotLoopBench` namespace** is baked into the committed
  binary captures (`.etl` / `.nettrace`) that the parity oracles compare against.
  Those captures cannot be regenerated without elevated ETW, so renaming the
  namespace would desync the goldens from their fixtures. Leave it as-is - it is
  the one deliberate exception to the otherwise-uniform `Filtrace` naming.

## Dependencies

`Filtrace.Core` references **`KlutzyNinja.Touki`** as a published NuGet
`PackageReference` (not a project reference). Keep it that way - it is what makes
the repo build standalone.

## Coding style

- Latest C# (C# 14). Use C# keyword types (`int`, `string`, `bool`), not
  `Int32` / `String` / `Boolean`.
- Prefer explicit types with target-typed `new` over `var`:
  `List<string> list = new();`, `int[] values = [1, 2, 3];`.
- Use `is null` / `is not null`, not `== null` / `!= null`.
- Write XML doc comments on public methods, properties, and types; indent XML by
  one space per nesting level.
- Do not use HTML entities in comments or docs. Write the character directly or
  use plain words so the source remains readable.
- Use this header on every C# file:

```c#
// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information
```

## Publishing changes

Publishing changes is opt-in. Outside the continuous mode below, never `git push`
or open / merge a pull request without an explicit instruction in the user's most
recent message - an explicit verb such as `push`, `commit and push`, `open the PR`,
`ship it`, or an equivalent. Local commits on a feature branch are reversible and
fine; publishing is not. When in doubt, stop and ask one short yes/no question.

A user may explicitly confirm continuous execution of a bounded slice of the
[canonical primary plan](https://github.com/JeremyKuhne/fasttrace/blob/main/docs/primary-plan.md).
Within that slice, choose task-derived branch names and PR metadata; commit, push,
and open PRs; address CI, reviews, and discussions; and squash merge after required
checks pass and required exact-head reviews approve the commit being merged. Do not
ask again at every step. This authorization survives status questions and context
resumes until the user pauses or changes it, or the slice completes. It does not
authorize unrelated work.

Neither mode implies permission to force-push, rewrite history, or perform
destructive cleanup. Do not change repository visibility, publish a new package,
create release or version tags, or trigger a publish workflow. Local build, pack,
and AOT compilation are validation, not permission to distribute artifacts.
Checkpoint with the user before changing contracts or scope, bypassing or proceeding
past an unresolved required gate, or acting with uncertain repository rights.
