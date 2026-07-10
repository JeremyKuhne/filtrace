---
core: manage-skills
core-pin: v0.10.0
---

# Manage skills overlay

Repository-specific bindings for filtrace.

## Ownership

- Provenance-bearing portable cores under `.agents/skills/` come from
  `JeremyKuhne/agent-skills` and must remain identical to their pinned upstream
  artifact unless explicitly listed as a pending-upstream divergence below. Put
  filtrace-specific paths and policy in `overlay.md`.
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

## Pending upstream divergence

Until the commons publishes its next reviewed release after `v0.10.0`, filtrace
mirrors the no-HTML-entity readability fixes prepared in commons source commit
[`1b3da57`](https://github.com/JeremyKuhne/agent-skills/commit/1b3da57) for:

- `pre-pr-self-review/SKILL.md`;
- `security-review/checklist.md`;
- `security-review/unsafe-apis.md`.

[Test-AgentSkills.ps1](../../../tools/Test-AgentSkills.ps1) permits only the
deterministic entity substitutions in those files and fails when this temporary
allowance becomes stale.