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
- `tests/Filtrace.*.Tests/` - unit and parity tests (Microsoft.Testing.Platform runner)
- `fixtures/` - HotLoopBench and the committed binary captures the tests read
- `tools/` - CI contract scripts (CLI help lint, MCP server check)
- `docs/`, `eval/`, `.agents/skills/` - single-source workflow text, eval harness, shipped skill

## Build and test

- `dotnet build filtrace.slnx -c Release`
- `dotnet test filtrace.slnx -c Release`

CI also runs two contract checks that must stay green:

- `tools/Test-CliHelp.ps1 -Configuration Release` - every verb appears in the
  top-level help, each verb's `--help` stays within the line budget, and the
  README documents every verb.
- `tools/Test-McpServer.ps1 -Configuration Release` - stdout is pure JSON-RPC,
  the tool-list schema stays within the token budget, and a real `tools/call`
  round-trips.

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
- Use plain ASCII (`-`, `"`, `...`) in comments and docs, not typographic Unicode
  (no em-dashes) and not HTML entities.
- Use this header on every C# file:

```c#
// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information
```

## Publishing changes

Never `git push` or open / merge a pull request without an explicit instruction
in the user's most recent message - an explicit verb such as `push`,
`commit and push`, `open the PR`, `ship it`, or an equivalent. Local commits on a
feature branch are reversible and fine; publishing is not. When in doubt, stop
and ask one short yes/no question.
