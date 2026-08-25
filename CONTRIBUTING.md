# Contributing

Contributions are welcome.

## Pull requests

By submitting a pull request you:

1. Confirm that you wrote the code (or otherwise have the right to contribute
   it), **and**
2. Agree that your contribution is licensed under the MIT License that governs
   this project (see [LICENSE](LICENSE)).

You retain copyright to your work; you simply grant Jeremy W. Kuhne and all
downstream users a perpetual, irrevocable MIT license to use, modify, and
redistribute it.

## Building and testing

filtrace targets **.NET 10** and uses the Microsoft.Testing.Platform runner.

```pwsh
dotnet build filtrace.slnx -c Release
dotnet test filtrace.slnx -c Release
```

CI also runs ten contract/eval checks that must stay green; run them locally before
opening a PR:

```pwsh
./tools/Test-CliHelp.ps1 -Configuration Release
./tools/Test-McpServer.ps1 -Configuration Release
./tools/Test-LocalFiltrace.ps1 -Configuration Release
./tools/Test-Docs.ps1
./tools/Test-CaptureBenchmarkTrace.ps1
./tools/Test-CaptureProjectTrace.ps1
./tools/Test-FiltraceAnalysis.ps1 -Configuration Release
./tools/Test-TrackDInvestigation.ps1
./eval/Invoke-Eval.ps1
./tools/Test-AgentSkills.ps1 -VerifyUpstream -ReferenceValidation
```

The full agent-skill check requires GitHub CLI with `gh skill` support and
Node.js with `npx`.

The local-setup check uses isolated tool paths to prove exact local CLI package
installation and offline rollback, plus preservation of prior MCP and skill
state, unrelated MCP entries, and consumer overlays without touching real user
configuration. The remaining scripts guard CLI/MCP contracts, shared
documentation, capture helpers, replayable analysis, the Track D wrapper,
deterministic evals, and vendored skills.
The deterministic eval runs the canonical trace-analysis tasks and enforces answer,
call-count, and output-token baselines without invoking an LLM.
The agent-skill check validates the v0.14.0 commons pins and overlays, compares
vendored cores with fresh upstream installs, runs the reference validator, and
checks readability and repository-relative links.

To exercise a working tree through the installed CLI, MCP server, and Filtrace
skill, then return to the prior setup, see
[Test a local Filtrace build end to end](docs/local-testing.md).

## Conventions

- Latest C# (C# 14). Use C# keyword types (`int`, not `Int32`); prefer explicit
  types with target-typed `new` over `var`; use `is null` / `is not null`.
- Write XML doc comments on public members. Do not use HTML entities in comments
  or docs; write the character directly or use plain words so the source remains
  readable.
- File header on every C# file:

  ```c#
  // Copyright (c) 2025 Jeremy W Kuhne
  // SPDX-License-Identifier: MIT
  // See LICENSE file in the project root for full license information
  ```

## Frozen contracts - do not rename

Two identifiers are deliberately fixed (see [AGENTS.md](AGENTS.md)):

- the **`trace_*` MCP tool names**, which are the public tool contract that agent
  clients bind to;
- the **`TraceQ.Fixtures.HotLoopBench`** namespace, baked into the committed
  binary captures the parity oracles compare against - renaming it desyncs the
  goldens from their fixtures.

## AI agent customizations

Project-wide rules for AI coding agents live in [AGENTS.md](AGENTS.md).
