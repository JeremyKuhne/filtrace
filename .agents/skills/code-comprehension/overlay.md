---
core: code-comprehension
core-pin: v0.13.0
---

# Code comprehension overlay

## Bindings

- [AGENTS.md](../../../AGENTS.md) and [.editorconfig](../../../.editorconfig) win
  on any conflict with the core's screening thresholds. Filtrace spells names out
  in full, prefers explicit types with target-typed `new`, and documents public
  members with XML comments.
- Judge `Filtrace.Core` analysis types against the trace-analysis vocabulary
  (samples, stacks, frames, folds, roots, inclusive/self time). A name that reads
  clearly in general prose but conflicts with that vocabulary is the misleading
  case the core ranks first.
- CLI verbs in `src/Filtrace` and MCP tools in `src/Filtrace.Mcp` are thin
  adapters over `Filtrace.Core`. Structural complexity belongs in the library, not
  in a verb or tool handler.
- Apply this screen to PowerShell under `tools/`, `eval/`, and `fixtures/` too,
  alongside the [powershell-scripting skill](../powershell-scripting/SKILL.md).
- Agent-facing prose in `docs/workflow.md`, `docs/traps.md`, and the packaged
  filtrace skill is under token budgets enforced by
  [Test-Docs.ps1](../../../tools/Test-Docs.ps1) and
  [Test-McpServer.ps1](../../../tools/Test-McpServer.ps1). Report a readability
  finding there as a suggestion; the budget gate decides.

## Updating

When the core is re-pinned, update `core-pin`, review these bindings against the
new core, and run
[Test-AgentSkills.ps1](../../../tools/Test-AgentSkills.ps1) with
`-VerifyUpstream -ReferenceValidation`.
