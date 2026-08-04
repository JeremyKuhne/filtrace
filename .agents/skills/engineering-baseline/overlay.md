---
core: engineering-baseline
core-pin: v0.13.0
---

# Engineering baseline overlay

## Filtrace binding

- Filtrace is an existing net10.0 CLI, library, and MCP repository. Use the
  brownfield assessment path; do not replace its established scaffold with the
  greenfield templates.
- [AGENTS.md](../../../AGENTS.md) owns build, test, style, frozen-contract, and
  publishing rules. Remote repository settings and publishing remain explicit
  approval boundaries.
- The product performance surface is
  [benchmarks/Filtrace.Benchmarks](../../../benchmarks/Filtrace.Benchmarks), while
  binary trace fixtures remain under [fixtures](../../../fixtures).
- Validate repository changes with the Release build/test commands and all contract
  gates listed in AGENTS.md. Validate skill inventory and provenance with
  [Test-AgentSkills.ps1](../../../tools/Test-AgentSkills.ps1).
