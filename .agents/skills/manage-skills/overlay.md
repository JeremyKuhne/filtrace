---
core: manage-skills
core-pin: v0.10.0
---

# Manage skills overlay

Repository-specific bindings for filtrace.

## Ownership

- Provenance-bearing portable cores under `.agents/skills/` come from
  `JeremyKuhne/agent-skills` and must remain identical to their pinned upstream
  artifact. Put filtrace-specific paths and policy in `overlay.md`.
- The [filtrace skill](../filtrace/SKILL.md) is different: it is a repo-specific,
  tool-shipped core whose canonical source is this repository. Edit the marked
  blocks in [docs/workflow.md](../../../docs/workflow.md) or
  [docs/traps.md](../../../docs/traps.md), then run
  [Test-Docs.ps1](../../../tools/Test-Docs.ps1) with `-Fix`.
- Only the local filtrace skill is packed into `KlutzyNinja.Filtrace.Mcp`.

## Pulling a commons release

Use exact source paths and an immutable pin:

```pwsh
gh skill install JeremyKuhne/agent-skills skills/<name> --pin vX.Y.Z --agent github-copilot --scope project --force
```

Review the vendored diff, update the overlay's `core-pin`, and run
[Test-AgentSkills.ps1](../../../tools/Test-AgentSkills.ps1) with
`-VerifyUpstream -ReferenceValidation`. Never move a local filtrace binding into
the vendored core.