# filtrace docs

The [roadmap](roadmap.md) is Filtrace's one current work queue.
[Design](design.md) retains product contracts and the durable findings of
completed investigations. The remaining detail pages are operational guides
or evidence for a specific still-relevant choice; none is a parallel plan.

| Page | What it is |
| --- | --- |
| [design.md](design.md) | Product principles, enforced gates, and durable results from completed investigations. |
| [roadmap.md](roadmap.md) | The current conditional work, concrete triggers, and short decision/evidence index. |
| [workflow.md](workflow.md) | How to capture, orient, rank, drill, and compare; canonical CLI/MCP catalogs and single-sourced agent guidance. |
| [traps.md](traps.md) | Single-sourced evidence and interpretation pitfalls for the shipped skill. |
| [source-build.md](source-build.md) | Explicit build-only FastTrace source integration and Native AOT commands. |
| [local-testing.md](local-testing.md) | Current repository-local activation ownership and recovery by durable status. |
| [filtrace-etl-trimming.md](filtrace-etl-trimming.md) | Fixture relogging steps and the managed-frame fidelity limit. |
| [multi-operation-query-reuse.md](multi-operation-query-reuse.md) | Private CLI/MCP reuse measurements and conditional multi-operation design options; no public command approved. |
| [release-validation.md](release-validation.md) | Merged-main/CI provenance and isolated package-install smoke checks for future releases. |

Git history and release tags retain the full experiment and PR records. Do
not turn one of those records into another standing implementation queue.

## Single-sourced blocks

This directory is the single source of truth for filtrace's workflow text. The
marked blocks below are embedded verbatim into the shipped skill and the README;
[tools/Test-Docs.ps1](../tools/Test-Docs.ps1) fails CI when a copy drifts. Edit
the block here, then run `tools/Test-Docs.ps1 -Fix` to refresh every copy.

| Source | Marked blocks | Embedded into |
| --- | --- | --- |
| [workflow.md](workflow.md) | `verbs`, `scopes`, `agents-snippet`, `tools` | `verbs` and `scopes` -> the skill's detailed guide; `scopes` and `agents-snippet` -> the README; `tools` is reference-only |
| [traps.md](traps.md) | `traps` | the skill's detailed guide |

Everything outside a marked block is ordinary prose. The CLI and MCP help is a
separate contract, validated by [tools/Test-CliHelp.ps1](../tools/Test-CliHelp.ps1)
and [tools/Test-McpServer.ps1](../tools/Test-McpServer.ps1), not embedded from here.
