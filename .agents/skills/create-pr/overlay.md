---
core: create-pr
core-pin: v0.10.0
---

# Create PR overlay

## Bindings

- [AGENTS.md](../../../AGENTS.md) is authoritative for publishing. Do not push or
  open a PR without an explicit publishing verb in the user's most recent message.
- The canonical remote is `origin`; PRs target `main`.
- Run the pre-pr-self-review workflow and all filtrace gates before publishing.
- Stage by explicit path when the worktree contains more than one logical change.