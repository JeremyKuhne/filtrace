---
core: create-pr
core-pin: v0.14.0
---

# Create PR overlay

## Bindings

- [AGENTS.md](../../../AGENTS.md) is authoritative for publishing. Apply either its
  ordinary explicit-publication checkpoint or its user-confirmed continuous,
  plan-scoped mode; the latter persists beyond the latest message until its stated
  stop condition.
- In continuous mode, the AGENTS rules override the core's per-step branch-name,
  commit, push, and PR-metadata confirmation checkpoints within the confirmed scope.
- The canonical remote is `origin`; PRs target `main`.
- Run the pre-pr-self-review workflow and all filtrace gates before publishing.
- Stage by explicit path when the worktree contains more than one logical change.
