---
core: address-pr-feedback
core-pin: v0.10.0
---

# Address PR feedback overlay

## Bindings

- Re-read [AGENTS.md](../../../AGENTS.md) before every review round. Editing a
  valid finding is authorized by the request; commit, push, thread resolution, and
  re-review remain remote publishing actions requiring an explicit verb.
- Re-run the affected tests first, then the full Release suite and applicable
  CLI/MCP/docs/eval/agent-skill gates before proposing a push.
- If feedback touches `.agents/`, hand off to agent-files-review as part of the
  same round.
- If feedback touches PowerShell, invoke the
  [powershell-scripting skill](../powershell-scripting/SKILL.md) and perform its
  adjacent-state expansion before proposing a push. A comment's reported input
  is one witness: also test neighboring null/empty/unrecognized,
  absent/success/failure/incomplete, mode/outcome, or budget-boundary states owned
  by the same branch. Record the applicable boundary row and evidence in working
  notes before proposing the push; fixing only the reported cell is incomplete.