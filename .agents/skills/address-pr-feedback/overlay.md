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