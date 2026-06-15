# filtrace eval harness

The eval harness measures filtrace's fitness for an agent mid-investigation. It
has two arms; this directory currently ships the first.

## Deterministic gate (shipped, runs in CI)

[Invoke-Eval.ps1](Invoke-Eval.ps1) is the **free, no-LLM regression gate**. Each
task under [tasks/](tasks/) is a question, a committed fixture, and the canonical
tool sequence an ideal agent would run to answer it. The harness runs that
sequence directly and checks the three things the M5 design cares about:

- **success** - the tool produced the right answer (every step exits 0 and the
  task's assertions hold).
- **calls** - the canonical path stays within the call budget (design goal **G1**,
  <= 6 tool calls).
- **tokens** - the output an agent would consume stays within budget (design goal
  **G2**), tracked against [baselines.json](baselines.json) with a 15% growth
  tolerance (the design's regression budget). Token cost is the offline estimate
  from [tools/Get-TokenEstimate.ps1](../tools/Get-TokenEstimate.ps1).

```pwsh
# Compare against committed baselines (what CI runs).
./eval/Invoke-Eval.ps1

# Regenerate baselines after adding a task or a deliberate output change, then commit.
./eval/Invoke-Eval.ps1 -Update
```

A task is one JSON file in [tasks/](tasks/):

```json
{
  "id": "cpu-hotspot",
  "title": "Rank CPU self-time, then drill into the hottest frame's callers.",
  "fixture": "tests/Filtrace.Core.Tests/Fixtures/folding.speedscope.json",
  "os": "any",
  "steps": [
    { "args": ["cpu", "{fixture}", "--top", "5"] },
    { "args": ["callers", "{fixture}", "MyApp.Inner"] }
  ],
  "assert": [
    { "step": 0, "topFrame": "MyApp.Inner" },
    { "step": 1, "hintContains": "MyApp.Work" }
  ]
}
```

- `{fixture}` is substituted with the task's fixture path; the harness appends
  `--format json` to every step (the form an agent consumes).
- `os: "windows"` guards tasks that read an `.etl` (the ETW conversion is
  Windows-only); they skip cleanly on the Linux CI leg.
- `assert` checks run against a step's parsed JSON (default: the last step).
  Supported: `topFrame` (result.rows[0].frame), `field` + `equals` (a dotted path
  like `result.gcCount`), `hintContains`, and `jsonContains`.

## Live agent arms (later M5 slice, not yet built)

The headless-agent runners that score an actual agent's reasoning - the design's
four arms over the ten tasks, with success / tokens / calls / wall-time capture -
are the second arm. They are run locally / occasionally against the Copilot CLI
and local models (no metered API needed); the deterministic gate above is the
cheap regression net under them. See
[docs/implementation-plan.md](../docs/implementation-plan.md), milestone **M5**.
