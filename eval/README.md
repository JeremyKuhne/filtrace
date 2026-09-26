# filtrace eval harness

The eval harness measures filtrace's fitness for an agent mid-investigation. It
has two arms, both shipped here: a deterministic, no-LLM gate that runs in CI, and
a live agent arm that scores a real model locally.

The deterministic gate remains active. The public
[Filtrace roadmap](../docs/roadmap.md) records EP1 natural-adoption measurement
complete, EP2 v1 stopped inconclusive, and the separately authorized complete-answer
set graded. Both EP2 variants passed 12/12 answers with zero false confidence, but
candidate treatment delivery was verified in only 11/12 answers. No measured
execution or rerun is active. This page documents the harness and evidence boundary
rather than creating a parallel implementation queue.

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
  Copilot's own model and records the actual identity; pass `-Model` to pin one.
  An explicit `McpDll` run does not resolve or require the separate Filtrace CLI
  output.
- **`copilot` -> cli arm** is an experimental Windows-only EP1 evidence arm. It
  invokes a byte-verified owned copy of the current checkout's native apphost
  bundle, not a global `filtrace`. Before execution, an isolated per-run hook
  requires one literal PowerShell invocation of that apphost, the exact owned
  trace path, a task-derived read-only verb/option family, bounded typed values,
  and a final `--format json`. The JSONL parser independently requires a correlated
  `powershell` start/completion pair with the same shape. It accepts the required
  `command` and `description` members, optional literal `mode: "sync"`, and an
  optional integer `initial_wait` from 1 through 120; every other argument member is
  rejected. The successful completion
  must contain filtrace schema 18 and an operation matching the executed read-only
  verb. Its result must also contain an operation-specific typed anchor such as
  rank rows, callers, a report count, events, processes, or a tree root; an empty
  result object is not successful evidence. The parser retains only explicitly
  modeled evidence-bearing event types, rejects unknown or duplicate members and
  malformed nested payloads before interpretation, and requires the sole `result`
  event to be the final nonempty JSONL record. Known protocol chatter is discarded
  rather than treated as evidence. Decoy text, extra commands, writes, unknown
  tools, malformed events, post-result events, and failed completions do not pass.
- **`copilot` -> cli-skill arm** uses the same host, explicit model, task prompt,
  and PowerShell access as the Copilot cli arm, but adds the native `skill` and
  bounded `view` tools and copies the exact shipped
  `.agents/skills/filtrace` tree into the owned workspace and enables normal
  project-skill discovery. It requires one enabled `source: project` Filtrace
  entry at the copied path, one exact `skill {"skill":"filtrace"}` invocation,
  a successful completion, and one injected `<skill-context name="filtrace">`
  whose body exactly matches `SKILL.md` after frontmatter and its leading blank
  separator are removed and CRLF is normalized to LF. Startup metadata alone is
  not use evidence. For a controlled entrypoint experiment,
  `-SkillEntrypointPath eval/skill-variants/<name>/SKILL.md` substitutes only that
  repository-relative file; related files still come from the shipped skill and
  every selected source byte remains inventoried and hash-checked.

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
access. Their available analysis tool is only `powershell`; cli-skill additionally
receives `skill` and `view`. `view` is hook-limited to the copied, hash-bound skill
inventory, four requests, and 64 MiB of cumulative requested source. The other
observed host built-ins are explicitly excluded. On Windows, a handshake
launcher joins a kill-on-close Job Object before it may start Copilot, so children
remain owned and are terminated when the bounded run ends even if the host exits first.
One invocation is capped at 64 total strict task iterations so copied immutable
bundles and retained run evidence cannot grow without a run-wide bound. Before
launch, actual fixture/CLI/skill input sizes plus the maximum per-run artifact and
runtime budgets must also fit a 2 GiB projected retained-byte cap.
Manifest-backed tasks are rejected before host launch until the strict context can
copy and attest their dependency closure without changing relative paths.
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
Task-derived `--children` values are frozen to the canonical include/exclude choice,
and `--pid` values are positive 32-bit integers. Retained raw start/completion events
remain necessary when an experiment grades the exact selector and returned effective
scope.
Unknown tool argument members and command options, including environment, input,
timeout, sandbox, output, symbol/network, and native-symbol options, are rejected.
For `view`, the hook requires one exact path from the copied skill inventory and an
optional bounded two-integer line range. The post-run parser independently validates
the same request and returned source bytes before accepting the read as evidence.
The raw hook envelope and string-valued `toolArgs` are parsed for duplicate members
before PowerShell object coercion. The parent ledger independently applies the same
rule to its request envelope and nested view arguments.

Before returning `permissionDecision: allow`, the hook consumes an allowance from
a monotonic in-memory ledger owned by the evaluator process. The host can neither
rewrite nor reset that state. The parent ledger independently parses each exact
command or view request against its immutable task policy and computes the retained
hash and requested-byte count itself; a direct client cannot authorize a
caller-supplied hash, relabel an analysis as help, or spend view capacity on another
path. Analysis hashes are capped at `min(MaxSteps, task.maxCalls)`, help hashes use
a separate cap for the top level plus each task-derived command family, and bounded
view requests share the same authoritative snapshot. A help lookup therefore cannot
consume a one-call analysis budget. Each pipe request is capped at 64 KiB and a
two-second read, so a stalled client cannot hold the ledger through the hook's
decision window. The retained
Copilot CLI 1.0.82 nonce probe established that `preToolUse` runs first and that an
explicit allow bypasses both `permissionRequest` and the normal shell deny. Missing,
malformed, crashed, or timed-out hooks receive no explicit allow and therefore
fall through to the normal deny. Conservative consumption is intentional: if an
allowed command never appears in the transcript, ledger/transcript mismatch rejects
the iteration. Successful PowerShell results must use the observed object with equal
`content` and `detailedContent`, one JSON payload, and the exact trailing
`<shellId: N completed with exit code 0>` line before schema-18 and operation checks.

The native `skill` tool is not shell execution and does not use the PowerShell
hook. Its display result can be shorter than the source, so `detailedContent` is
not treated as complete evidence. The evaluator verifies the model-visible
`<skill-context>` message instead. Copilot removes YAML frontmatter and normalizes
CRLF to LF before injection, and its wrapper consumes the blank separator before
the Markdown body; the complete normalized wrapper and body must match. Results
keep the raw file SHA-256, decoded source hash, expected context hash, observed
context hash, discovery metadata, and correlated skill call ID as separate evidence.
The matching enabled project-skill inventory must occur before the native skill
invocation; appending discovery metadata after use does not attest discovery.

Before launch, the runner accepts only a tracked, HEAD-clean file beneath
`tests/Filtrace.Core.Tests/Fixtures`, rejects UNC/reparse paths, and caps it at
512 MiB. It inventories and hashes at most 256 CLI files / 512 entries / 512 MiB
and 64 skill files / 128 entries / 16 MiB, hashes source bytes before and after the
streaming copy, hashes each destination, and rechecks immutable inputs after the
host exits. Every copied immutable CLI/runtime, fixture, skill, hook, policy, and
configuration file is held with read sharing only for the host lifetime; viewable
skill hashes are also embedded in the immutable hook policy. Before each periodic
artifact scan excludes those inputs, it also
requires their attested lengths and ordinary non-reparse paths; growth is rejected
while the host is still running. Dynamic host artifacts are independently capped
at 16 MiB and 512 total files/directories. On Windows, host distribution assets unpacked beneath the
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
iteration record. The wall-time deadline and periodic artifact/runtime scans
continue after redirected streams close; a host process that remains alive is
stopped at the configured deadline.
JSONL event objects and the Filtrace JSON document embedded in a successful shell
completion reject recursive duplicate members before PowerShell object conversion.
The separately requested usage JSON is capped at 1 MiB and rejects duplicate
members recursively. It requires and validates premium cost, request count,
current model, and the exact token-count structure, while accepting additive root telemetry.
Only those four required fields are projected into the stable result schema; the
complete raw usage file and its hash remain retained.
If timeout, output, artifact, or launch enforcement throws before normal retention,
the runner writes at most 8 KiB of captured stdout/stderr prefixes under an
evaluator-owned sibling `failures/<run-id>` directory before rethrowing. This
failure record is separate from the host-writable artifact budget.

The strict arms remain Windows-only and require both `-Model` and `-ExpectedModel`; requested and observed
identities must be nonempty and exactly equal using ordinal comparison. Missing,
different-case, case-distinct multiple identities, or mismatched identity fails.
Identity comes from the host's
`session.tools_updated` current model; a provider's lower-level chunk model label is
not substituted for it. At least one valid identity event must precede the first
model or tool call; a matching identity reported only after analysis does not attest
the work. Exact identities remain in local result evidence so comparison can verify
matched arms. The default `eval/results/` location is ignored and never committed;
public PRs and documentation use role labels rather than those private identifiers.
The deterministic fake validates generated hook placement,
literal-command decisions, recorded input shape/order, malformed/unknown inputs,
bounded description metadata, unsafe option rejection, exact concurrent cap
consumption, no-hook fallback, strict shell-result extraction, launch arguments,
and transcript/state agreement. A retained native Copilot CLI 1.0.82 protocol probe
on 2026-09-13 discovered Filtrace from the owned project path, invoked it through
the native skill tool, and matched the configured evaluation-model identity. It
completed in 3.355 seconds, reported
one premium request, and emitted complete host token accounting. This verifies
host, model, and skill protocol availability; it is not the ready-capture efficacy
smoke. If an isolated home cannot use platform-keyring authentication, the run must
fail clearly; do not copy credentials or weaken isolation.

### EP1 protocols and completed natural-adoption measurement

The v1 measured attempt stopped after its first session with zero valid pairs. Its
usage file contained additive root telemetry that the frozen parser rejected, and
an otherwise permitted `rank` request carried `initial_wait: 120` beyond the frozen
30-second metadata ceiling. The result is operational evidence only, not evidence
for or against skill efficacy.

The [v2 ready-capture protocol](protocols/ep1-ready-capture-v2.json)
accepts bounded duplicate-free additive root usage telemetry while persisting only
the four required accounting fields, and accepts `initial_wait` through 120 seconds
without changing the independent 600-second process deadline. It otherwise binds
the same held-out scope/attribution task, exact public input hashes, evaluator-only
expected facts, balanced four-pair order, eight maximum host sessions, and a 240-credit
ceiling. It is the frozen repair design, not a description of the later completed
measurement. It reports descriptive arm and paired values without a winner label, uses
no automatic replacement pair, and stops for a user decision. After each pair, a
reviewer grades the two exact final answers under randomized opaque identifiers,
without arm, order, model, skill, cost, timing, transcript, or path metadata. The
pair's rubric record is hashed before the arm map is revealed. This masks the arm
label but cannot hide wording that may itself suggest skill use, so the report calls
the grading arm-masked rather than inference-blind. No final answer, a wrong answer,
or an overconfident answer is a valid measured quality outcome and remains in the
four-pair denominator; broken identity, accounting, transcript, or grade evidence
stops the experiment incomplete without a replacement pair. `Test-Docs.ps1`
validates the protocol's shape, identities, order, bounds, record schema, grading
boundary, and privacy.

The [v3 ready-capture protocol](protocols/ep1-ready-capture-v3.json) is now the
**sole prepared, not authorized** skill-versus-CLI experiment. V1 and v2 and
their record schemas remain byte-preserved, hash-checked historical designs;
their one-task/credit-ceiling rules do not apply to v3. No v3 host session has
been authorized or run. Separate permission must bind the final v3 protocol
hash, a private exact user-selected host model identifier (no substitution),
the source and CLI/skill/evaluator input hashes, and the 16-session limit before
any host work. Preparation or a passing offline check does not grant permission.
The protocol pins the updated `SKILL.md` and complete 11-file skill tree as
LF-normalized text hashes, plus their raw Windows entrypoint/manifest hashes
and both public fixture SHA-256 hashes. Every docs-validation platform checks
the portable skill hashes; Windows also requires the exact raw manifest in
the private checkpoint and each skill session's inventory.

V3 tests the **updated shipped** Filtrace skill against the **same current CLI
bits without the skill**. Its two public fixtures ask different quality-first
questions: [ETW unresolved frame](tasks/30-quality-first-unresolved.json)
preserves HotLoopBench tree scope, the `?` rank row, and 51 contributing records
while preferring a frame-name quality check to `callers '?'`; the
[mixed speedscope rank](tasks/31-mixed-unknown-resolved.json) retains the unknown
row and denominator alongside resolved rows. That 100 ms public profile has
three positive intervals under `App.Main`: `?` 60 ms (60%), `App.Work` 25 ms
(25%), and `App.Other` 15 ms (15%). `callers 'App.Work'` accounts for only
its own 25 ms through `App.Main`, not the unknown 60 ms. Speedscope `info`
reports three samples and `symbolResolutionRate: 1.0`; the latter is hard-coded
and **cannot** grade row resolution.

Each new task's required sequence is **rank, then info** on the same public
fixture and children/process scope. Those two verbs are permitted by the
strict Copilot command hook in both arms; QA requires successful `rank` and
`info` calls. Step 0 asserts the ranked rows and the structured `info` hint;
step 1 checks frame-name quality (ETW: 51 records, 0% named; speedscope:
three evented intervals, aggregate 1.0). The speedscope number does not
override the `?` row's 60% share. The mixed task has a third, explicitly
`optional: true` `callers 'App.Work'` step: the strict hook permits it and
the deterministic gate verifies its 25 ms `App.Main` caller, but the live
runner does **not** require it for success. An attempted `callers '?'` or
source-line operation counts as a quality failure, not invented machine
evidence or resolution of the unknown row.

F3's resolved-frame suggestion for a `diff` remains advisory until the
comparison arm has a verified matching scope; v3 does not silently promote it
to a cross-arm drill command.
The task/QA/fixture hashes and exact expected rows belong only to evaluator
evidence, never to the model-visible prompt or workspace.

Each task has four fresh, independently isolated skill/CLI pairs with two of
each first-arm order, interleaved across tasks: eight pairs, **16 host sessions
maximum**, zero replacements. A started invalid session stops the global
sequence incomplete. Each session permits six analysis steps and a separate
600-second native timeout. Existing per-session output, runtime, fixture, CLI,
skill, and artifact-byte limits remain; the record-file ceiling rises from 32
to 48 only because eight pairs require 43 top-level records. There is **no
per-session or overall host AI credit ceiling**: `-NoHostAiCreditLimit` makes
the runner's `maxAiCredits: 0` an uncapped sentinel, not a zero-credit budget.
Actual credits and token usage still have to be present, finite, and reconciled
in the final report.

The [v3 record schema](protocols/ep1-ready-capture-record-v3.schema.json)
and shared [record validator](Test-ReadyCaptureRecords.ps1) enforce frozen task
and model identities, credit accounting without a cap, contiguous stopped
state, complete per-task summaries, and non-credit budgets. The grader sees
only a task identifier and randomized blind IDs with the two final answers;
arm, order, model/skill evidence, cost, timing, transcripts, and paths remain
private. Five task-specific answer criteria and false-confidence notes are
frozen before unblinding. Incorrect answers still count as valid quality
outcomes; missing machine or grade evidence does not. Report per-task and
overall descriptive paired deltas and order effects, never a winner claim.

Run the **offline, fake-only** preparation checks without a model:

```pwsh
./eval/Test-ReadyCaptureProtocolV3.ps1
./tools/Test-Docs.ps1
```

The first command checks the v1/v2 historical hashes, both v3 task/QA/fixture
hashes and deterministic baselines, balanced order, stopped authorization and
privacy; it also creates and removes fake 16-session records under `eval/` to
exercise the schema and validator (including invalid-session, wrong-model,
uncapped-usage, and attempted-replacement cases). `Test-Docs.ps1` includes
that gate alongside the historical v2 replay. Neither command performs an AI
call or grants launch permission.

The deterministic JSON token estimator counts absolute fixture paths. In the
long F3 worktree, three *existing* tasks exceeded their 15% budget solely
because the same paths recur in output: `manifest-batch`, frozen task 23, and
`manifest-case-drill`. A disposable short drive alias (removed immediately
after the run) passed **31/31** tasks without raising any historical baseline.
The two new baselines (ETW rank + info: **2 calls / 1,033 tokens**; mixed
rank + info + optional callers: **3 calls / 568 tokens**) were measured
through the same short-path policy. Do not expand old baselines to accommodate
a checkout-path artifact.

The user subsequently authorized a private natural-adoption protocol: 10 matched
pairs / 20 fresh chats, with an identical prompt that did not instruct the skill arm
to invoke the skill. Skill non-use, abstention, wrong answers, and overconfidence
counted; only infrastructure failures could be replaced. Three infrastructure-invalid
attempts were retained outside the behavioral denominator. A parser-only amendment
recognized `prompt_cache_break` as non-evidence chatter and carried prior dialogs
and results forward byte-for-byte.

All 20 counted schema-v3 results validated. The project skill was discovered and
invoked in every eligible chat. Under the fixed five-criterion rubric, strict passes
were 5/10 with the skill and 3/10 with CLI-only; false-confidence scope errors were
5/10 and 6/10. The skill arm consumed 42,848 observed result tokens versus 10,505,
with 13 versus 12 successful analysis calls and 10 host credits in each counted arm.
Strong time and position patterns prevent a winner claim.

The command evidence isolated the mechanism tested by the completed follow-up: all
five failed skill chats used an unscoped `rank` and accepted
`context.scope.processMode: "automatic"`; all five passing skill chats used
`--process HotLoopBench`. The follow-up changed only the skill's named-process
acceptance contract and added two deterministic tasks without changing frozen EP1
task 23: [named-process root scope](tasks/28-named-process-self-scope.json)
and [exact-PID tree scope](tasks/29-exact-pid-tree-scope.json). The evaluator's
`-SkillEntrypointPath` can substitute the retained EP1 `SKILL.md` at the same project
discovery path while every related skill file remains shared and hash-attested.
PRs #136 and #137 subsequently merged the candidate and bounded strict-policy
support. EP2 v1 froze 12 balanced pairs but stopped during pair 10 first after a
post-exposure treatment-integrity failure; it remains inconclusive. A separate
authorization collected only the five unrun answers. Across all 24 answers, both
variants passed 12/12 with zero false confidence and 12 paired ties. Treatment
delivery was verified 12/12 for the baseline and 11/12 for the candidate. This is
descriptive answer completion, not a recovered efficacy experiment, and no rerun is
scheduled.

For the historical v2 record schema, and for v3 only after a separate launch
authorization, run `eval/Test-ReadyCaptureRecords.ps1` with the matching protocol
and schema paths after each grade is serialized and hashed. It
must pass before the private arm map is revealed and again before the final report.
It schema-validates each record and recomputes session fields, arm summaries,
paired deltas, order summaries, and quality/cost classifications from the retained
result and grade files. `Test-Docs.ps1` runs its complete-artifact mutation suite
in CI.

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
Each accepted skill-file view retains its attested source byte/text hashes, exact
request hash and range, requested bytes, returned-content and logical-payload hashes,
character counts, line coverage, and truncation protocol; aggregate skill fields
retain total requested and returned content.

For the ready-capture protocol, runner `success` is only the machine-evidence
result. It does not produce the protocol's semantic `answerCriteria` or
`falseConfidence` fields. Those fields come only from the separately frozen,
arm-masked pair-grade record described above; a final report must keep the two
sources distinct and must never synthesize a missing grade from runner success.

Schema-v3 labeled records also retain one arm-neutral input identity per task: the
task file hash, matching MCP-QA row hash, primary fixture hash, and a deterministic
closure hash over canonical file arguments plus captures referenced by manifests.
`Compare-EvalRuns.ps1` requires those identities, model/host/arm, task set, iteration
count, and call budget to match before calculating deltas. CLI and skill hashes are
deliberately excluded from this identity because they are the surfaces an A/B run
may change. Comparison tolerance must be a finite fraction from zero through one,
and raw duplicate members are rejected before conversion. Schema-v3 records require
their complete root evidence, nonnegative integer transport metrics, and exact
host-specific nested object and array shapes; summary rows accept only the documented
schema members.
The graded answer event must follow every counted successful analysis completion
and, for `cli-skill`, the verified injected skill context. Strict CLI transcript
entries retain the parser's derived operation, including `report --kind` intent,
rather than inferring it later from the surface verb. Tool evidence must preserve
causal `start < completion < context/answer` order, and strict CLI grading requires
every operation derived from the task's canonical steps in addition to explicit
task expectations.
Results land under `eval/results/` (git-ignored) as schema-v3 JSON with a median
summary. Schema-v3 rows retain exact `SuccessCount` for comparison while
`Success%` remains a rounded display value; older schema-v3 rows derive the count
from their validated iterations. `hostUsage` retains the result event. `hostUsageFile` separately records
the bounded `--usage-output-file`, its hash, and detailed input/output/cache counts;
a missing file remains explicitly unavailable rather than becoming zero. An
available file must contain nonnegative premium-request/user counts, a nonempty
current model, and integer input/cache-read/cache-write/output token counts;
malformed or incomplete accounting fails before a result is published. A successful
iteration requires at least one validated source: result-event usage or the detailed
usage file. When the detailed file is present, its `currentModel` must exactly match
the one model identity observed in the JSONL stream.

### EP1 preflight checkpoint

The 2026-09-13 preflight used Copilot CLI 1.0.82 and a fixed model identity retained
in private evidence. The fail-closed fake-host contract passed, including wrong or
missing model, skill, tool, result, and usage evidence. A restricted live protocol
probe confirmed the configured identity, project-skill
discovery, native `skill` invocation, and complete injected context.

One `cpu-hotspot` smoke per arm then used the same CLI hash
`b0528f858d11a2f864c42334354fb22e74dcea710ed3b6a4ad3f9c737e89ef4f`
and fixture hash
`2891c917c511763561ec18ad8982eba82a81dda72a47e0774de76790272145d1`.
Both returned `MyApp.Inner` at 16 ms / 64% self weight and `MyApp.Work` as its
100% caller.

| Arm | Analysis / help / denied calls | Observed result tokens | Host elapsed | Host input / cache-read / cache-write / output tokens |
|---|---:|---:|---:|---:|
| CLI only | 2 / 1 / 1 | 932 | 29.957 s | 15 / 26,442 / 7,699 / 870 |
| CLI plus discovered skill | 2 / 1 / 1 | 955 | 32.704 s | 18 / 46,173 / 11,050 / 1,001 |

Each arm reported one premium request. The skill arm's discovery, invocation, and
context hashes verified. This single pair proved protocol and accounting readiness;
it did not establish an efficacy advantage. The later natural-adoption measurement
and the bounded EP2 candidate follow-up are described above.

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
that the skill caused a better answer. EP1 and the bounded EP2 follow-up are complete.
Future live evaluation requires a new question and authorization; it does not resume
the named-process comparison.

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

A `copilot` mcp-arm sample using the CLI-default model on the same `gc-report`
task answered correctly in **1** `trace_gc` call - the agent selects the right
tool straight from the MCP descriptions. The exact model identity remains in the
private evidence:

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
