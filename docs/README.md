# filtrace docs (single source)

This directory is the single source of truth for filtrace's workflow text.
Specific marked blocks in these pages are embedded verbatim into the shipped
skill and the README; [tools/Test-Docs.ps1](../tools/Test-Docs.ps1) fails CI when
an embedded copy drifts. (The CLI/MCP help is a separate contract, validated by
[tools/Test-CliHelp.ps1](../tools/Test-CliHelp.ps1) and
[tools/Test-McpServer.ps1](../tools/Test-McpServer.ps1), not embedded from here.)

| Page | Marked blocks | Embedded into |
|---|---|---|
| [workflow.md](workflow.md) | `verbs`, `tools`, `agents-snippet` | `verbs` -> the skill; `agents-snippet` -> the README; `tools` is reference-only |
| [traps.md](traps.md) | `traps` | the skill |
| [implementation-plan.md](implementation-plan.md) | completed implementation history | - |
| [traceevent-surface-assessment.md](traceevent-surface-assessment.md) | pinned TraceEvent 3.2.3 assessment | - |
| [filtrace-etl-trimming.md](filtrace-etl-trimming.md) | ETL relogging decision record | - |
| [pvanalyze-vs-filtrace.md](pvanalyze-vs-filtrace.md) | current capability comparison | - |
| [filtrace-improvement-plan.md](filtrace-improvement-plan.md) | completed capability-initiative history | - |
| [vnext-improvement-plan.md](vnext-improvement-plan.md) | single living improvement roadmap | - |

Only the blocks with a consumer above are drift-checked; the rest of each page
(and the README outside its embedded blocks) is ordinary prose. Edit a marked
block here, then run `tools/Test-Docs.ps1 -Fix` to refresh every embedded copy.
The historical knowledge-layer decision is summarized in
[implementation-plan.md](implementation-plan.md); current improvements belong only
in [vnext-improvement-plan.md](vnext-improvement-plan.md).
