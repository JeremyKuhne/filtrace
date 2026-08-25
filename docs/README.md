# filtrace docs

Four pages carry the thinking, three carry the operational text, and two are
technical decision records.

| Page | What it is |
|---|---|
| [design.md](design.md) | Principles, goals, non-goals, and the measures of success every change is judged against. |
| [roadmap.md](roadmap.md) | The only page holding unshipped work: priorities, gates, and open decisions. |
| [competitive-analysis.md](competitive-analysis.md) | How filtrace differs from other .NET performance tools, and what to learn from each. |
| [parallelism-opportunities.md](parallelism-opportunities.md) | Executable BenchmarkDotNet and CLI self-profiling plan for Track D. |
| [workflow.md](workflow.md) | How to drive filtrace: capture, orient, rank, drill, compare, plus the command and tool catalogs. |
| [traps.md](traps.md) | The reasoning errors a trace invites, and how to avoid them. |
| [local-testing.md](local-testing.md) | Reversibly test a checkout through the installed CLI, MCP server, and vendored skill, then return to shipped releases. |
| [traceevent-surface-assessment.md](traceevent-surface-assessment.md) | What the pinned TraceEvent 3.2.3 package does and does not provide, and which roadmap items that gates. |
| [filtrace-etl-trimming.md](filtrace-etl-trimming.md) | Why the ETW process-tree relog is a fixture tool rather than a shipped verb. |

Shipped work is not documented here. Git history and the release tags record what
landed; the lessons that outlived an initiative are principles in
[design.md](design.md).

## Single-sourced blocks

This directory is the single source of truth for filtrace's workflow text. The
marked blocks below are embedded verbatim into the shipped skill and the README;
[tools/Test-Docs.ps1](../tools/Test-Docs.ps1) fails CI when a copy drifts. Edit the
block here, then run `tools/Test-Docs.ps1 -Fix` to refresh every copy.

| Source | Marked blocks | Embedded into |
|---|---|---|
| [workflow.md](workflow.md) | `verbs`, `scopes`, `agents-snippet`, `tools` | `verbs` and `scopes` -> the skill; `scopes` and `agents-snippet` -> the README; `tools` is reference-only |
| [traps.md](traps.md) | `traps` | the skill |

Everything outside a marked block is ordinary prose. The CLI and MCP help is a
separate contract, validated by [tools/Test-CliHelp.ps1](../tools/Test-CliHelp.ps1)
and [tools/Test-McpServer.ps1](../tools/Test-McpServer.ps1), not embedded from here.
