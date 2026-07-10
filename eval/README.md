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
  live agent arm (below). The gate does not grade `prompt`/`expect` (it only checks
  their mirrored MCP QA fields); the live arm ignores `steps`/`assert`.
- Before running, the gate verifies every task has a matching [mcp-qa.jsonl](mcp-qa.jsonl)
  row with the same prompt, fixture, OS guard, and expected answer, and that every
  referenced `trace_*` tool exists. This keeps the deterministic/live/MCP task
  representations from drifting silently.
- `assert` checks run against a step's parsed JSON (default: the last step).
  Supported: `topFrame` (result.rows[0].frame), `field` + `equals` (a dotted path
  like `result.gcCount`), `hintContains`, and `jsonContains`.

## Live agent arm (shipped, run locally)

[Invoke-AgentEval.ps1](Invoke-AgentEval.ps1) is the **LLM arm**: it gives a real
agent only a task's natural-language `prompt` and lets the model choose which
filtrace commands to run, then scores whether it reached the right answer and at
what cost. That catches MCP descriptions/server instructions and CLI command,
error, output, and hint regressions - surfaces the deterministic gate cannot see.
It does **not** currently evaluate the shipped `SKILL.md`; that needs a separate arm
with repository customizations enabled. It is non-deterministic and needs a model
host, so it runs locally / occasionally, never in CI; the deterministic gate stays
the regression net.

**Two host/arm combinations are wired:**

- **`ollama` -> cli arm** (local, no metered API). The harness mediates a ReAct
  loop: the model emits one action per turn (`RUN: <args>` or `ANSWER: <text>`),
  the harness runs `filtrace <args>` on its behalf (only the allowlisted analysis
  verbs, never a shell) and feeds back the JSON (including error text, so the
  model can self-correct). The system prompt supplies the verb names; this exercises
  argument selection, errors, result envelopes, and hints, not automatic skill or
  top-level CLI-help discovery.
- **`copilot` -> mcp arm** (the GitHub Copilot CLI - the production target;
  metered, needs `copilot login`). The runner hands Copilot the task and the
  locally built filtrace MCP server (via `--additional-mcp-config`) and lets the
  agent drive the `trace_*` tools itself, then parses its JSONL transcript. This
  exercises the **MCP tool descriptions** the cli arm never touches, on the real
  production agent. It deliberately passes `--no-custom-instructions`, isolating
  the MCP contract from `AGENTS.md` and the filtrace skill. By default it uses
  Copilot's own model (the result records the actual model, e.g.
  `claude-opus-4.6`); pass `-Model` to pin one.

`claude` is recognized but not yet wired. The trace path is masked back to
`<TRACE>` in transcripts and answers so it does not leak.

```pwsh
# Local model (cli arm), a quick two-task sample.
./eval/Invoke-AgentEval.ps1 -AgentHost ollama -Model deepseek-r1:8b -Tasks cpu-hotspot,gc-report -N 1

# Copilot CLI (mcp arm) - drives the trace_* tools on the production agent.
# Build the MCP server first: dotnet build src/Filtrace.Mcp/Filtrace.Mcp.csproj -c Release
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Tasks cpu-hotspot,gc-report -N 1

# A fuller measurement (medians get meaningful around N = 5-10).
./eval/Invoke-AgentEval.ps1 -AgentHost ollama -Model deepseek-r1:8b -N 10
```

Each (task, iteration) records **success** (the answer contains every `expect`
substring; transcript review remains necessary), **calls** (filtrace invocations), **tokens** (the offline estimate of
the tool output the agent consumed - the same accounting the gate uses), and
**wall-time**, plus a per-command transcript. Results land under `eval/results/`
(git-ignored) as JSON with a median summary.

**MCP QA file.** [mcp-qa.jsonl](mcp-qa.jsonl) maps each task to the `trace_*` tool
an ideal MCP run should call and the expected answer (mcp-builder style). The
`copilot` arm requires every listed tool to appear as a successful call (extra
orientation calls are allowed), so a lucky answer substring without the intended
trace evidence does not pass. The file is also the seed for a future host-less
MCP-client runner.

### Coverage boundary

The corpus is a focused regression suite, not complete proof of investigative
quality. Its tasks cover orientation, ranking/measure choice, callers/callees,
process inventory, trees, timelines, raw events, GC, and JIT. They do not yet cover
`trace_lines`, `trace_heatmap`, `trace_classify`, `trace_diff`, `trace_export`,
`trace_threadpool`, or `trace_diskio`, and they do not exercise a full
orient -> rank -> drill -> compare run on one realistic capture.

Live success means the final answer contains each task's expected substring; on the
Copilot arm, every expected MCP tool must also succeed. That is deterministic enough
for baseline/candidate comparison, but it is not semantic grading: review transcripts
before accepting a surface change, especially when an answer can still be correct for
the wrong reason. The current arms also cannot establish
that a `SKILL.md` edit helped, because neither loads it. Measure skill revisions in a
separate run with customizations enabled rather than attributing an MCP-only result
to the skill.

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
overhead this arm is meant to measure.

A `copilot` mcp-arm sample (model `claude-opus-4.6`, the CLI default) on the same
`gc-report` task answered correctly in **1** `trace_gc` call - the agent selects
the right tool straight from the MCP descriptions:

| Task | Host / arm | Success | Calls | Tokens |
|---|---|---|---|---|
| gc-report | copilot / mcp | 100% | 1 | 1501 |

See [docs/implementation-plan.md](../docs/implementation-plan.md), milestone **M5**.

## Tuning the measured surfaces (the loop)

The point of the live arm is to improve the surfaces it actually presents to an
agent - MCP tool descriptions/server instructions, or CLI arguments, errors,
results, and hints - and test whether a change helped without regressing. These
surfaces are compiled in, so a candidate is a rebuilt working tree. `SKILL.md` is
outside this loop until a customization-enabled arm is added. The measured loop:

```pwsh
# 1. Baseline at HEAD, across a couple of models (evaluator diversity).
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models claude-opus-4.6,gpt-5.2 -N 5 -Label baseline

# 2. Edit a surface - e.g. a [Description] on a trace_* tool in TraceTools.cs - and rebuild.
dotnet build src/Filtrace.Mcp/Filtrace.Mcp.csproj -c Release

# 3. Candidate, same models, the other label.
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models claude-opus-4.6,gpt-5.2 -N 5 -Label candidate

# 4. Compare; non-zero exit if any model regressed.
./eval/Compare-EvalRuns.ps1 -Baseline baseline -Candidate candidate
```

- **`-Models`** runs the matrix across several models in one invocation; **`-Label`**
  stamps each result so the comparer can pair them. For copilot, `--model` gives
  real model diversity - which is why a second host (e.g. Claude Code) is not
  needed for overfitting detection.
- **[Compare-EvalRuns.ps1](Compare-EvalRuns.ps1)** pairs the latest run per
  (label, host/arm/model) and reports, per task, the success / calls / tokens
  delta with a verdict. The verdict is the design's regression budget (which also
  absorbs LLM noise): a **success drop on any model**, **>15% token growth** on any
  task, or a run present on only one side is a **REGRESSION/REJECT** (exit 1);
  higher success, fewer calls, or a **token drop beyond noise (>5%)** is an
  improvement, and smaller token deltas stay neutral. The per-model rows are the
  overfitting detector - a change that helps one model but regresses another is
  rejected.
- Drafting the revision (the design's "agent-drafted" step) is manual or a separate
  agent prompt; the machinery above is the deterministic score-and-compare it feeds.

Token counts are not comparable **across** hosts/arms (the cli arm counts CLI JSON
stdout; the mcp arm counts the MCP result payload) - compare within a host.
