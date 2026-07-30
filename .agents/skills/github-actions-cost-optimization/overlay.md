---
core: github-actions-cost-optimization
core-pin: v0.13.0
---

# GitHub Actions cost optimization overlay

## Bindings

- Filtrace runs two workflows: [ci.yml](../../../.github/workflows/ci.yml) on
  pull requests to `main` and [publish.yml](../../../.github/workflows/publish.yml)
  on release tags. There is no scheduled workload.
- `ci.yml` already applies several of the core's optimizations. Do not propose
  them again as new savings: `cancel-in-progress` concurrency keyed to the PR,
  runner-and-arch-scoped NuGet caching, Release-only builds, per-job
  `timeout-minutes`, and the cheap `ubuntu-slim` aggregator that carries the
  required `ci` status-check name.
- The job split is deliberate, not redundancy: Linux ARM64 owns the
  cross-platform contract checks, and Windows owns the full suite plus the
  deterministic eval because ETL and thread-time paths are Windows-only. Never
  propose collapsing the Windows job or moving an ETW-dependent check to Linux.
- The `ci` aggregator name is a required status check in the branch ruleset.
  Renaming or removing it, or removing a job from its `needs` list, is a
  protection change and needs explicit confirmation.
- All six contract checks named in [AGENTS.md](../../../AGENTS.md) must keep
  running on every pull request. Moving one to a manual or scheduled workflow
  weakens the gate and is out of scope for a cost change.

## Updating

When the core is re-pinned, update `core-pin`, review these bindings against the
new core, and run
[Test-AgentSkills.ps1](../../../tools/Test-AgentSkills.ps1) with
`-VerifyUpstream -ReferenceValidation`.
