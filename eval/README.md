# filtrace eval harness

The eval harness measures filtrace's fitness for an agent mid-investigation. It
has two arms, both shipped here: a deterministic, no-LLM gate that runs in CI, and
a live agent arm that scores a real model locally.

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
  "prompt": "This is a .NET CPU profile. Which method is the hottest by self-time, and which method calls it?",
  "fixture": "tests/Filtrace.Core.Tests/Fixtures/folding.speedscope.json",
  "os": "any",
  "steps": [
    { "args": ["cpu", "{fixture}", "--top", "5"] },
    { "args": ["callers", "{fixture}", "MyApp.Inner"] }
  ],
  "assert": [
    { "step": 0, "topFrame": "MyApp.Inner" },
    { "step": 1, "hintContains": "MyApp.Work" }
  ],
  "expect": ["MyApp.Inner", "MyApp.Work"]
}
```

- `{fixture}` is substituted with the task's fixture path; the harness appends
  `--format json` to every step (the form an agent consumes).
- `os: "windows"` guards tasks that read an `.etl` (the ETW conversion is
  Windows-only); they skip cleanly on the Linux CI leg.
- `steps` + `assert` drive the deterministic gate; `prompt` + `expect` drive the
  live agent arm (below). The gate ignores `prompt`/`expect`; the arm ignores
  `steps`/`assert`.
- `assert` checks run against a step's parsed JSON (default: the last step).
  Supported: `topFrame` (result.rows[0].frame), `field` + `equals` (a dotted path
  like `result.gcCount`), `hintContains`, and `jsonContains`.

## Live agent arm (shipped, run locally)

[Invoke-AgentEval.ps1](Invoke-AgentEval.ps1) is the **LLM arm**: it gives a real
agent only a task's natural-language `prompt` and lets the model choose which
filtrace commands to run, then scores whether it reached the right answer and at
what cost. That is what catches a description, help text, or skill that reads well
to a human but steers a model wrong - the surfaces the deterministic gate cannot
see. It is non-deterministic and needs a model host, so it runs locally /
occasionally, never in CI; the deterministic gate stays the regression net.

**Arm: cli.** The harness mediates a ReAct loop - the model emits one action per
turn (`RUN: <args>` or `ANSWER: <text>`), the harness runs `filtrace <args>` on
its behalf (only the allowlisted verbs, never a shell) and feeds back the JSON
(including any error text, so the model can self-correct), until the model answers
or hits the call budget. The trace path is masked as `<TRACE>` and substituted at
run time.

**Host: ollama** (local, no metered API). `copilot` and `claude` are recognized
but not yet wired - the runner reports that and exits cleanly.

```pwsh
# A quick two-task sample against a local model.
./eval/Invoke-AgentEval.ps1 -Model gpt-oss:20b -Tasks cpu-hotspot,gc-report -N 1

# A fuller measurement (medians get meaningful around N = 5-10).
./eval/Invoke-AgentEval.ps1 -Model deepseek-r1:8b -N 10
```

Each (task, iteration) records **success** (the answer contains every `expect`
substring), **calls** (filtrace invocations), **tokens** (the offline estimate of
the tool output the agent consumed - the same accounting the gate uses), and
**wall-time**, plus a per-command transcript. Results land under `eval/results/`
(git-ignored) as JSON with a median summary.

**MCP arm.** [mcp-qa.jsonl](mcp-qa.jsonl) is the mcp-builder-style QA file: per
task, the question, the `trace_*` tool an ideal MCP-arm run should call, and the
expected answer. Driving a model through a live MCP client is a heavier runner
left as future work; the file is the tool-selection reference and the seed for it.

### Example local run

A sample on this repo's fixtures (host `ollama`, model `deepseek-r1:8b`, N = 1,
cli arm) - `gpt-oss:20b` would not load in that environment, so a smaller local
model stood in:

| Task | Success | Calls | Tokens |
|---|---|---|---|
| cpu-hotspot | 100% | 4 | 146 |
| alloc-hotspot | 100% | 4 | 403 |
| gc-report | 100% | 1 | 368 |
| jit-report | 100% | 1 | 2251 |

The model self-corrected a wrong flag from the CLI's error text, which is why the
two-step tasks took more than the canonical call count - exactly the agent
overhead this arm is meant to measure. See
[docs/implementation-plan.md](../docs/implementation-plan.md), milestone **M5**.
