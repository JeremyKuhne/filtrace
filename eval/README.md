# filtrace eval harness

The eval harness measures filtrace's fitness for an agent mid-investigation. It
has two arms, both shipped here: a deterministic, no-LLM gate that runs in CI, and
a live agent arm that scores a real model locally.

The deterministic gate remains active. The public
[Filtrace roadmap](../docs/roadmap.md) makes the bounded EP1 CLI-plus-skill
preflight active while deferring broader comparative-agent harness work. This page
documents the available harness rather than creating a parallel implementation queue.

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
error, output, hint, and discovered skill-use regressions - surfaces the
deterministic gate cannot see. It is non-deterministic and needs a model host, so
live runs remain local/occasional; CI runs only its deterministic fake-host
contract and the no-LLM gate.

**Four host/arm combinations are wired:**

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
- **`copilot` -> cli arm** is an experimental Windows-only PP03 evidence arm. It
  invokes a byte-verified owned copy of the current checkout's native apphost
  bundle, not a global `filtrace`. Before execution, an isolated per-run hook
  requires one literal PowerShell invocation of that apphost, the exact owned
  trace path, a task-derived read-only verb/option family, bounded typed values,
  and a final `--format json`. The JSONL parser independently requires a correlated
  `powershell` start/completion pair with the same shape. It accepts the required
  `command` and `description` members, optional literal `mode: "sync"`, and an
  optional integer `initial_wait` from 1 through 30; every other argument member is
  rejected. The successful completion
  must contain filtrace schema 17 and an operation matching the executed read-only
  verb. The parser admits only explicitly recorded event type names with a basic
  JSON object shape. Decoy text, extra commands, writes, unknown tools, unknown
  event types, malformed events, and failed completions do not pass.
- **`copilot` -> cli-skill arm** uses the same host, explicit model, and available
  tools and task prompt as the Copilot cli arm, but copies the exact shipped
  `.agents/skills/filtrace` tree into the owned workspace and enables normal
  project-skill discovery. It requires one enabled `source: project` Filtrace
  entry at the copied path, one exact `skill {"skill":"filtrace"}` invocation,
  a successful completion, and one injected `<skill-context name="filtrace">`
  whose body exactly matches `SKILL.md` after frontmatter and its leading blank
  separator are removed and CRLF is normalized to LF. Startup metadata alone is
  not use evidence.

The strict Copilot CLI arms create a GUID-owned workspace outside the repository
when `-OutDir` equals or is beneath this checkout. They reject any selected
workspace with an ancestor `AGENTS.md`, so discovery cannot reach unattested
instructions. The child environment starts empty, copies only required
OS/process/path/locale/.NET variables, and redirects `HOME`, `USERPROFILE`,
`XDG_CONFIG_HOME`, `APPDATA`, `LOCALAPPDATA`, and `COPILOT_HOME` to owned empty
directories. Token, provider, custom-instruction, MCP, and telemetry environment
settings are not inherited. The CLI-only arm disables custom instructions; the
skill arm enables discovery inside an otherwise empty owned workspace containing
only the copied project skill. Both disable builtin MCPs, remote export/control,
auto-update, bash environment loading, user prompts, and automatic system-temp
access. Their available tools are only `powershell`, plus `skill` for cli-skill;
the other observed host built-ins are explicitly excluded.
There is no shell allow rule. Normal permissions deny all shell, write, and URL
requests. They also pass `read` to `--deny-tool`; Copilot CLI 1.0.82 accepts that
argument. Retained real-host probes established that a matching `preToolUse` allow
executes `view` while a missing allow falls through to the normal read denial.

For each iteration the runner writes hook configuration only under that run's
isolated `COPILOT_HOME/hooks`. Its command hooks use direct `exec` plus argument
arrays to a copied, hashed PowerShell 7 guard. One `preToolUse` hook validates the
exact recorded input members (`sessionId`, `timestamp`, `cwd`, `toolName`, and
`toolArgs`) and the session and working directory. For `powershell`, it accepts the
observed `command` plus a nonempty, single-line description of at most 256
characters. It also accepts the two optional host metadata members described above,
with the same bounds used for transcript evidence. The description and host fields
are metadata; only the command is parsed. The command must be one all-single-quoted
PowerShell AST invocation using the exact owned apphost and fixture. Permitted verbs
and option names come from the task's canonical analysis steps, while text and
numeric values remain typed and bounded rather than fixed to the expected answer.
Unknown tool argument members and command options, including environment, input,
timeout, sandbox, output, symbol/network, and native-symbol options, are rejected.

Before returning `permissionDecision: allow`, the hook atomically appends the
command hash to a ledger capped at `min(MaxSteps, task.maxCalls)`. The retained
Copilot CLI 1.0.82 nonce probe established that `preToolUse` runs first and that an
explicit allow bypasses both `permissionRequest` and the normal shell deny. Missing,
malformed, crashed, or timed-out hooks receive no explicit allow and therefore
fall through to the normal deny. Conservative consumption is intentional: if an
allowed command never appears in the transcript, ledger/transcript mismatch rejects
the iteration. Successful PowerShell results must use the observed object with equal
`content` and `detailedContent`, one JSON payload, and the exact trailing
`<shellId: N completed with exit code 0>` line before schema-17 and operation checks.

The native `skill` tool is not shell execution and does not use the PowerShell
hook. Its display result can be shorter than the source, so `detailedContent` is
not treated as complete evidence. The evaluator verifies the model-visible
`<skill-context>` message instead. Copilot removes YAML frontmatter and normalizes
CRLF to LF before injection, and its wrapper consumes the blank separator before
the Markdown body; the complete normalized wrapper and body must match. Results
keep the raw file SHA-256, decoded source hash, expected context hash, observed
context hash, discovery metadata, and correlated skill call ID as separate evidence.

Before launch, the runner accepts only a tracked, HEAD-clean file beneath
`tests/Filtrace.Core.Tests/Fixtures`, rejects UNC/reparse paths, and caps it at
512 MiB. It inventories and hashes at most 256 CLI files / 512 entries / 512 MiB
and 64 skill files / 128 entries / 16 MiB, hashes source bytes before and after the
streaming copy, hashes each destination, and rechecks immutable inputs after the
host exits. Dynamic host artifacts are independently capped at 16 MiB and 512
total files/directories. On Windows, host distribution assets unpacked beneath the
fixed isolated `home/AppData/Local/copilot/pkg` cache are measured separately at
256 MiB total, 1,024 files/directories, and 128 MiB per file. No other home path is
excluded. This classification is path-based accounting and does not infer which
process wrote a cache entry. Combined captured output is capped at 10 MiB and wall
time at 10 minutes. After a completed host process, exact UTF-8 stdout and stderr are
written with create-new semantics to `logs/host-stdout.jsonl` and
`logs/host-stderr.log` before ledger or JSONL parsing. They count against the dynamic
artifact budget. If the remaining byte budget cannot hold both, a bounded
`logs/host-output-not-retained.txt` diagnostic is written instead. A parser failure
does not publish an eval result, but the retained raw files remain available and a
diagnostic-write failure does not replace the parser error. These controls, measured
artifact/runtime bytes, and the input manifests are retained in each successful
iteration record. The wall-time deadline continues after redirected streams close;
a host process that remains alive is stopped at the configured deadline.

The strict arms remain Windows-only and require both `-Model` and `-ExpectedModel`; requested and observed
identities must be nonempty and exactly equal using ordinal comparison. Missing,
different-case, case-distinct multiple identities, or mismatched identity fails.
Identity comes from the host's
`session.tools_updated` current model; a provider's lower-level chunk model label is
not substituted for it. The deterministic fake validates generated hook placement,
literal-command decisions, recorded input shape/order, malformed/unknown inputs,
bounded description metadata, unsafe option rejection, exact concurrent cap
consumption, no-hook fallback, strict shell-result extraction, launch arguments,
and transcript/state agreement. A retained native Copilot CLI 1.0.82 protocol probe
on 2026-09-13 discovered Filtrace from the owned project path, invoked it through
the native skill tool, and reported exact model ID `gpt-5.6-sol-fast` with display
name `GPT-5.6 Sol Fast (Internal only)`. It completed in 3.355 seconds, reported
one premium request, and emitted complete host token accounting. This verifies
host, model, and skill protocol availability; it is not the ready-capture efficacy
smoke. If an isolated home cannot use platform-keyring authentication, the run must
fail clearly; do not copy credentials or weaken isolation.

`claude` is recognized but not yet wired. The trace path is masked back to
`<TRACE>` in persisted transcripts and answers. The owned path and fixture-derived
CLI output are still sent to the selected model; only committed eval fixtures are
allowed, and the raw trace is never attached as a model resource. `--no-remote-export`
prevents host transcript export, not the model interaction itself.

```pwsh
# Local model (cli arm), a quick two-task sample.
./eval/Invoke-AgentEval.ps1 -AgentHost ollama -Model deepseek-r1:8b -Tasks cpu-hotspot,gc-report -N 1

# Copilot CLI (mcp arm) - drives the trace_* tools on the production agent.
# Build the MCP server first: dotnet build src/Filtrace.Mcp/Filtrace.Mcp.csproj -c Release
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Tasks cpu-hotspot,gc-report -N 1

# Experimental Copilot CLI arm. Use the exact model id reported by the installed host.
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Arm cli -Model <model-id> `
  -ExpectedModel <model-id> -Tasks cpu-hotspot,gc-report -N 1

# Same host/model/task, with the shipped project skill genuinely discovered and verified.
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Arm cli-skill -Model <model-id> `
  -ExpectedModel <model-id> -Tasks cpu-hotspot,gc-report -N 1

# Repeated observations; summaries remain descriptive rather than statistical.
./eval/Invoke-AgentEval.ps1 -AgentHost ollama -Model deepseek-r1:8b -N 10
```

Each (task, iteration) records **success** (the answer contains every `expect`
substring and required evidence passes; transcript review remains necessary),
**calls** (filtrace invocations), **tokens** (the offline estimate of observed tool
result payloads - not inferred model context), and **wall-time**, plus transcript,
host usage, model evidence, execution paths/hashes, skill evidence, and warnings.
Results land under `eval/results/` (git-ignored) as schema-v3 JSON with a median
summary. `hostUsage` retains the result event. `hostUsageFile` separately records
the bounded `--usage-output-file`, its hash, and detailed input/output/cache counts;
a missing file remains explicitly unavailable rather than becoming zero.

### EP1 preflight checkpoint

The 2026-09-13 preflight used Copilot CLI 1.0.82 and exact model ID
`gpt-5.6-sol-fast`. The fail-closed fake-host contract passed, including wrong or
missing model, skill, tool, result, and usage evidence. A restricted live protocol
probe confirmed the display name `GPT-5.6 Sol Fast (Internal only)`, project-skill
discovery, native `skill` invocation, and complete injected context.

One `cpu-hotspot` smoke per arm then used the same CLI hash
`5c3b150ec7ed6b149670f40fd4ea24273ad5c1e77b9d0ab334b09fa1253d14b5`
and fixture hash
`2891c917c511763561ec18ad8982eba82a81dda72a47e0774de76790272145d1`.
Both returned `MyApp.Inner` at 16 ms / 64% self weight and `MyApp.Work` as its
100% caller.

| Arm | Analysis / help / denied calls | Observed result tokens | Host elapsed | Host input / cache-read / cache-write / output tokens |
|---|---:|---:|---:|---:|
| CLI only | 2 / 1 / 0 | 932 | 23.850 s | 12 / 19,313 / 7,464 / 690 |
| CLI plus discovered skill | 2 / 1 / 2 | 955 | 43.512 s | 21 / 56,761 / 11,398 / 1,324 |

Each arm reported one premium request. The skill arm's discovery, invocation, and
context hashes verified. This single pair proves protocol and accounting readiness;
it does not establish an efficacy advantage. The next accepted evidence is three
alternating pairs on a frozen ready-capture task with transcript-level grading.

Substring matching removes thousands separators from digit runs on both sides
first, so a task can pin `4309` and an answer that says "4,309" still matches.
Without that, an `expect` list measures a model's number formatting rather than its
analysis - it scored two correct answers as failures before it was added.

On the MCP arm each response is also broken into **text**, **structured**, and
**wire** tokens, because the same payload is currently carried in both
`content[0].text` and `structuredContent` and the transport experiment has to see
them apart. A measured single-call example: text 36, structured 36, complete wire
result 171 - the payload twice, inside a wrapper roughly 4-5x its size. The
headline `tokens` metric stays the text copy so it remains comparable with the cli
arm and with existing baselines. What the *client* put in front of the model is
host-specific, so it is not inferred: the host's own reported usage is recorded
verbatim as `hostUsage` instead.

**Known limit of that split at large payloads.** One measured call that returned a
big event page recorded text 261 against structured 10,057 and wire 22,207 - and
wire being about twice structured says two full-size copies really were on the
wire. The text figure is therefore the host transcript's copy, which appears to be
truncated above some size, not the wire's. Confirm against raw JSONL before drawing
a transport conclusion from a text/structured ratio on responses larger than a few
hundred tokens.

`-McpDll` points the mcp arm at an explicitly built server - how a transport or
surface variant published under `artifacts/` is measured against the baseline
without editing committed tasks. A labeled run with fewer than three iterations
per task warns: medians from one or two samples cannot support a surface decision.

**MCP QA file.** [mcp-qa.jsonl](mcp-qa.jsonl) maps each task to the `trace_*` tool
an ideal MCP run should call and the expected answer (mcp-builder style). The
`copilot` arm requires every listed tool to appear as a successful call (extra
orientation calls are allowed), so a lucky answer substring without the intended
trace evidence does not pass. The file is also the seed for a future host-less
MCP-client runner.

Three optional fields grade *intent* rather than the current tool names:

| Field | Effect |
|---|---|
| `expectOperations` | Every listed operation must have been called successfully, on either arm. Operations are surface-neutral (`rank`, `source`, `events`, ...) via [Get-OperationName.ps1](Get-OperationName.ps1), so a task keeps grading correctly after a tool is renamed or folded into another. |
| `forbidOperations` | Attempting any listed operation fails the iteration, whether or not the call succeeded - the way to state "do not reach for source lines on a speedscope profile", which filtrace rejects anyway, so grading only successful calls would never see the mistake. |
| `maxCalls` | A per-task call budget tighter than the global `-MaxSteps`, for tasks whose whole point is that one call suffices. |
| `maxResponseTokens` | A ceiling on the **largest single response** an iteration pulled, not the iteration's total. Restraint is a response-size property, not a call-count one - one call asking for ten thousand event records still answers the question, and `maxCalls` cannot see it - but summing across calls would fail an iteration whose responses were each well inside the ceiling. |

The deterministic gate validates all three against the operation vocabulary, so a
typo fails CI instead of silently never matching.

### Coverage boundary

The corpus is a focused regression suite, not complete proof of investigative
quality. Its tasks cover orientation, frame-name versus source/PDB quality,
ranking/measure choice, callers/callees, process inventory, trees, timelines, raw
events, GC, JIT, thread-pool behavior, physical disk I/O, lifecycle phases, and
manifest batch/diff workflows. They do not yet cover
`trace_lines`, `trace_heatmap`, `trace_classify`, or `trace_export`, and they do not exercise a full
orient -> rank -> drill -> compare run on one realistic capture.

Three comprehension scenarios associated with conditional PP09 workflow work cannot
be expressed against today's surface and are deliberately absent rather than faked.
Their priority comes from the public roadmap, not this harness document:

| Scenario | Why not yet |
|---|---|
| Reject or repair `root` plus `benchmark` | It is an error path, and the deterministic gate requires every step to exit 0. |
| Disambiguate several matching frames | There is no ambiguity diagnostic to assert - `callers <prefix>` silently aggregates every match. |
| Choose `classify` over a generic report for native runtime CPU | The committed ETW fixture resolves 0% of its CPU frames, so `classify` returns one `other` category. |

Live success still uses expected substrings rather than a general semantic grader.
The MCP arm requires its expected MCP tools; strict CLI arms require matched local
apphost evidence; cli-skill also requires verified project discovery, invocation,
and injected context. These checks reject unsupported provenance but do not establish
that the skill caused a better answer. The matched ready-capture smoke and semantic
grader remain EP1 work.

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

See [docs/design.md](../docs/design.md) for the measures this harness enforces, and
[docs/roadmap.md](../docs/roadmap.md) for the completed VN0 decisions and conditional
Filtrace backlog.

## Tuning the measured surfaces (the loop)

The point of the live arm is to improve the surfaces it actually presents to an
agent - MCP tool descriptions/server instructions, or CLI arguments, errors,
results, and hints - and test whether a change helped without regressing. These
surfaces are compiled in, so a candidate is a rebuilt working tree. Skill changes
use matched `cli-skill` and `cli` runs; the MCP-only loop below remains appropriate
for MCP descriptions and server instructions. The measured loop:

```pwsh
# 1. Baseline at HEAD, across a couple of models (evaluator diversity).
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models <model-a>,<model-b> -N 5 -Label baseline

# 2. Edit a surface - e.g. a [Description] on a trace_* tool in TraceTools.cs - and rebuild.
dotnet build src/Filtrace.Mcp/Filtrace.Mcp.csproj -c Release

# 3. Candidate, same models, the other label.
./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models <model-a>,<model-b> -N 5 -Label candidate

# 4. Compare; non-zero exit if any model regressed.
./eval/Compare-EvalRuns.ps1 -Baseline baseline -Candidate candidate
```

- **`-Models`** runs several configured models in one invocation; **`-Label`**
  stamps each result so the comparer can pair them. These are descriptive paired
  observations, not a statistical overfitting test.
- **Verify model ids and metering with the installed host before a live run.** Do
  not derive an id from a display label or reuse an unobserved name. Start with a
  coordinator-approved reduced preflight:

  ```pwsh
  ./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Models <candidates> -Tasks event-count-only,cpu-hotspot -N 1
  ```

  Then inspect the host-reported identity and `iterations[].hostUsage`. This slice
  records those values but makes no availability, cost, or effectiveness claim for
  any named model.
- **[Compare-EvalRuns.ps1](Compare-EvalRuns.ps1)** pairs the latest run per
  (label, host/arm/model) and reports, per task, the success / calls / tokens
  delta with a verdict. The verdict is a configured policy threshold: a **success
  drop on any model**, **>15% token growth** on any
  task, or a run present on only one side is a **REGRESSION/REJECT** (exit 1);
  higher success, fewer calls, or a **token drop over 5%** is an improvement, and
  smaller token deltas stay neutral. Schema-v3 inputs must carry verified exact
  model identity, unique task summaries derived exactly from their iterations, and
  matching iteration identities. A Copilot MCP run may use the host default with a
  null requested model only when it retains one verified observed identity;
  malformed or unverified matching results reject the comparison.
- Drafting the revision (the design's "agent-drafted" step) is manual or a separate
  agent prompt; the machinery above is the deterministic score-and-compare it feeds.

Token counts are not comparable **across** hosts/arms (the cli arm counts CLI JSON
stdout; the mcp arm counts the MCP result payload) - compare within a host.
