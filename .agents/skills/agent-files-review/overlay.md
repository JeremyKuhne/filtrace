---
core: agent-files-review
core-pin: v0.10.0
---

# Agent files review overlay

## Bindings

- [AGENTS.md](../../../AGENTS.md) is the repository's direct agent standard.
  Filtrace intentionally does not maintain a generated
  `.github/copilot-instructions.md` mirror; treat mirror-specific checklist items
  as not applicable.
- Run [Test-AgentSkills.ps1](../../../tools/Test-AgentSkills.ps1) for provenance,
  portfolio metadata, overlays, and repository-relative links.
- Run [Test-Docs.ps1](../../../tools/Test-Docs.ps1) for the local filtrace skill's
  single-sourced blocks, CLI/MCP catalog completeness, and packaged-artifact links.
- Vendored cores are read-only mirrors. Local changes belong in overlays. The
  [filtrace core](../filtrace/SKILL.md) is canonical here and follows the docs-first
  update path instead.