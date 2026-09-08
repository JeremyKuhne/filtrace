# filtrace docs

The cross-repository primary plan controls sequencing. Local pages preserve product
principles, a conditional backlog, executable measurement detail, operational
guidance, and completed decision records; they are not independent work queues.

| Page | What it is |
| --- | --- |
| [Primary plan](https://github.com/JeremyKuhne/fasttrace/blob/main/docs/primary-plan.md) | Canonical ordering and completion plan; filtrace's feature backlog is conditional on it. |
| [design.md](design.md) | Principles, goals, non-goals, and the measures of success every change is judged against. |
| [roadmap.md](roadmap.md) | Subordinate Filtrace source plan: completed product decisions and conditional backlog items selected only through the primary plan. |
| [competitive-analysis.md](competitive-analysis.md) | How filtrace differs from other .NET performance tools, and what to learn from each. |
| [parallelism-opportunities.md](parallelism-opportunities.md) | Executable BenchmarkDotNet and CLI self-profiling detail for PP02/PP08 Track D experiments. |
| [source-build.md](source-build.md) | Explicit build-only FastTrace source integration and Native AOT commands. |
| [local-testing-redesign.md](local-testing-redesign.md) | Completed design and recovery record for repository-scoped local checkout activation. |
| [stack-traversal-experiment.md](stack-traversal-experiment.md) | Measurement record for the indexed stack traversal retained in PR #126. |
| [workflow.md](workflow.md) | How to drive filtrace: capture, orient, rank, drill, compare, plus the command and tool catalogs. |
| [traps.md](traps.md) | The reasoning errors a trace invites, and how to avoid them. |
| [traceevent-surface-assessment.md](traceevent-surface-assessment.md) | What the pinned TraceEvent 3.2.6 package does and does not provide, and which roadmap items that gates. |
| [filtrace-etl-trimming.md](filtrace-etl-trimming.md) | Why the ETW process-tree relog is a fixture tool rather than a shipped verb. |

Git history and release tags remain authoritative for what landed. Completed work
stays here only when its design rationale, measured tradeoffs, or recovery contract
continues to constrain future changes.

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
