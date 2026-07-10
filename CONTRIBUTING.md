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

CI also runs four contract/eval checks that must stay green; run them locally before
opening a PR:

```pwsh
./tools/Test-CliHelp.ps1 -Configuration Release
./tools/Test-McpServer.ps1 -Configuration Release
./tools/Test-Docs.ps1
./eval/Invoke-Eval.ps1
```

The first asserts every CLI verb is documented and within the help budget; the
second drives the MCP server over stdio and checks stdout purity, the tool-list
schema budget, and a real `tools/call` round-trip. The docs check guards shared
workflow blocks, skill links, command/tool coverage, and packaged skill contents.
The deterministic eval runs the canonical trace-analysis tasks and enforces answer,
call-count, and output-token baselines without invoking an LLM.

## Conventions

- Latest C# (C# 14). Use C# keyword types (`int`, not `Int32`); prefer explicit
  types with target-typed `new` over `var`; use `is null` / `is not null`.
- Write XML doc comments on public members. Use plain ASCII (`-`) in comments and
  docs, not em-dashes or HTML entities.
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
