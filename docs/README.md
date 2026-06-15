# filtrace docs (single source)

This directory is the single source of truth for filtrace's workflow text. The
marked blocks in these pages are embedded verbatim into the shipped skill, the
README, and (by contract) the CLI/MCP help;
[tools/Test-Docs.ps1](../tools/Test-Docs.ps1) fails CI when a copy drifts.

| Page | Single source for | Embedded into |
|---|---|---|
| [workflow.md](workflow.md) | the verb catalog, the MCP tool catalog, the agent snippet | the skill, the README |
| [traps.md](traps.md) | the trap catalog | the skill |
| [publishing.md](publishing.md) | how filtrace is packaged and published | - |
| [implementation-plan.md](implementation-plan.md) | the living roadmap (M0-M6) | - |

Edit a marked block here, then run `tools/Test-Docs.ps1 -Fix` to refresh every
embedded copy. See [implementation-plan.md](implementation-plan.md), milestone
**M4**, for the knowledge-layer plan.
