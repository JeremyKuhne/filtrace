# Local Filtrace testing redesign plan

**Status:** Phase 1 merged in
[PR #98](https://github.com/JeremyKuhne/filtrace/pull/98). Phase 2 baseline
capture and bounded overlay input merged in
[PR #99](https://github.com/JeremyKuhne/filtrace/pull/99), and the fixed
per-worktree lock merged in
[PR #100](https://github.com/JeremyKuhne/filtrace/pull/100). Prepared CLI package
validation and fresh private installation merged in
[PR #101](https://github.com/JeremyKuhne/filtrace/pull/101). Structured MCP
publication and baseline restoration are implemented locally on
`local-testing-budget-rebaseline`; skill mutation has not begun.

**Last verified:** 2026-08-31 against `origin/main` at `ee24807`. PR #94 was
closed without merge after PR #98 established the replacement.

## Decision

Keep the repository-scoped local-testing outcome, but do not merge PR #94's
current implementation. Build a narrower replacement from `main` after this plan
is accepted.

The replacement will:

- support one state schema, four persisted statuses, and five explicit
  operations;
- manage only fixed, repository-local MCP, skill, CLI, and state paths;
- use one immutable resource plan and one per-target lock;
- reject links beneath managed roots instead of supporting arbitrary aliases;
- keep PowerShell as a thin entry point and put state and filesystem mutation in
  a small .NET helper with unit-testable types;
- carry forward PR #94's failure cases as tests, not its compatibility branches.

PR #94 remains useful as a threat-model corpus. It was closed as superseded after
PR #98 merged; its failure cases carry forward as focused tests rather than
compatibility branches.

## Why reset

The workflow goal is sound: build one checkout, activate it only for one
consumer repository, and restore the exact prior state. The implementation
boundary is not.

At the point of this assessment:

| Signal | Current value |
| --- | ---: |
| Added lines in PR #94 | approximately 4,700 |
| Production script lines | 2,093 |
| Production functions | 64 |
| Production `if` statements | 204 |
| Production `try` statements | 29 |
| `Test-LocalFiltrace.ps1` | 2,222 lines |
| Follow-up commits | 19 |
| Review threads | 36 |
| Path / alias safety threads | 24 |

The recurring findings are concentrated in five coupled responsibilities:

1. canonical path identity and link handling;
2. resource ownership and lock identity;
3. schema-version migration;
4. restore and cleanup state transitions;
5. transactional mutation and rollback.

Each invocation currently reconstructs those facts across many branches. Fixing
one branch repeatedly exposes a neighboring state that applies a validation too
early, computes a different identity, or treats a legacy path as a current owned
workspace. More local conditionals increase that risk.

## Outcomes

The replacement is complete when a contributor can:

1. run one command from a Git consumer repository;
2. exercise the current Filtrace checkout through an isolated CLI, project MCP
   entry, and project skill;
3. refresh local mode without replacing the original baseline;
4. restore the exact prior MCP and skill state after success or interruption;
5. run the ordinary contract without elevation on Windows, Linux, and macOS.

The implementation must also be small enough to review linearly. Targets, not
hard compatibility promises:

- non-coordinator support code at or below 1,300 source lines;
- stateful active-resource mutation and recovery coordinator at or below 700
  source lines;
- total .NET helper at or below 2,000 source lines;
- end-to-end PowerShell contract at or below 1,000 lines;
- no function over 100 source lines;
- no more than one place that constructs resource paths or classifies state.

Count physical lines in tracked C# source files under
`tools/Filtrace.LocalTesting`, excluding generated output. The coordinator count
includes code that applies or restores the CLI, MCP, and skill and sequences
their durable state transitions. Resource and state models, serialization and
validation, baseline readers, path guards, and the target lock count as support.

After PR #101 the helper measured 1,433 lines: 1,212 support lines and 221
active-resource CLI installation lines. The original 1,500-line total left only
67 lines even though 479 lines remained in the original coordinator allowance.
The revised limits preserve the 700-line ceiling on coupled mutation and recovery
logic and leave the measured support code 88 lines of headroom within its
1,300-line budget. The arithmetic is 1,300 support lines plus 700 coordinator
lines for a 2,000-line total. If support remains at 1,212 and the coordinator
reaches its ceiling, the helper reaches 1,912 lines; MCP mutation, skill
publication, Refresh, and recovery therefore share the 479 coordinator lines
remaining after the CLI installer.

If either budget cannot hold, stop and reduce scope instead of moving code to an
excluded location, compressing readable code, or adding another compatibility
mode.

The coordinator limit starts with code that applies or restores active CLI,
MCP, and skill changes. Pure resource/state models, bounded baseline readers, and
the target-lock primitive count toward the total-helper limit instead. This keeps
both budgets measurable without rewarding compressed prerequisite code.

## V1 scope

### Supported parameters

The public wrapper supports only:

- `-Action Install|Restore`;
- `-TargetRepository`, defaulting to the current directory;
- `-Configuration Debug|Release`.

`-SkipBuild` and `-SkipValidation` may exist as explicitly internal
contract-test switches. They are not documented as normal user workflow.

V1 does not support:

- custom MCP, skill, state, or CLI paths;
- user-profile MCP or user-scope skill installation;
- global .NET tool mutation;
- non-Git target directories;
- automatic import of PR #94 state schemas.

Removing those options eliminates the resource-alias registry and most
ancestor/descendant overlap combinations while preserving the user's stated
goal: changes stay scoped to the repository where testing was requested.

### Fixed resource plan

Resolve the target once with Git and construct one immutable plan:

| Resource | Location |
| --- | --- |
| Target root | `git rev-parse --show-toplevel` |
| Per-worktree Git directory | `git rev-parse --absolute-git-dir` |
| State root | `<git-dir>/filtrace-local-testing` |
| State manifest | `<state-root>/state.json` |
| Lock | `<git-dir>/filtrace-local-testing.lock` |
| CLI | `<state-root>/tools` |
| Packages and backups | `<state-root>/artifacts` |
| MCP configuration | `<target>/.vscode/mcp.json` |
| Skill destination | `<target>/.agents/skills/filtrace` |

Using the per-worktree Git directory gives independent linked worktrees
independent state while making every Filtrace source checkout targeting that
worktree share the same lock and baseline. Two different linked worktrees
intentionally keep separate state because they mutate different `.vscode` and
`.agents` trees. No machine-wide ownership registry is needed.

The helper creates missing `.vscode` and `.agents/skills` parent directories
before publishing their fixed children. The baseline records which fixed parents
it created, and Restore removes only those that are still empty.

## Architecture

### Thin PowerShell wrapper

Keep `Use-LocalFiltrace.ps1` as the discoverable entry point. It should only:

1. validate PowerShell and `dotnet` availability;
2. locate the Filtrace source checkout and target repository;
3. invoke the .NET helper with structured arguments;
4. preserve the helper's exit code and human diagnostics.

It must not own path comparison, JSON mutation, locking, backup, or
state-machine logic.

### Typed .NET helper

Add a repository tool such as `tools/Filtrace.LocalTesting/` targeting `net10.0`
and using only BCL dependencies. Its core types should be small and explicit:

- `ResourcePlan`: every fixed path, computed once;
- `Baseline`: prior MCP property, prior skill backup metadata, and fixed parent
  directories created by Install;
- `LocalTestingState`: schema version, status, source checkout, and baseline;
- `Operation`: `FreshInstall`, `ResumeInstall`, `Refresh`, `Restore`, or
  `CleanupRetry`;
- `LocalTestingCoordinator`: ordered active-resource changes and durable status
  transitions.

Pure planning and transition logic belongs in unit-testable methods. Filesystem
and process calls sit behind narrow adapters, but do not introduce a general
virtual filesystem framework.

Each CLI, MCP, and skill mutator owns resource-scoped staging, publication,
validation, and idempotent restoration. V1 does not persist a per-mutation
journal or promise all-or-nothing rollback to the previous active local version.
The manifest status and immutable baseline are the durable recovery protocol: a
failed Install or Refresh may leave mixed active-resource versions in
`installing`, after which Install resumes toward one coherent active set or
Restore converges to the baseline.

### Trust and link policy

The target Git repository and the Filtrace source checkout are user-selected
local inputs. Managed destinations are still treated defensively:

- canonicalize the target and Git directory once;
- reject a reparse point or symbolic link in any component below the canonical
  target root leading to `.vscode/mcp.json` or `.agents/skills/filtrace`;
- reject links inside an existing skill before backup;
- reject a linked state root or lock path;
- recursively delete only the exact fixed state root after validating its
  marker;
- do not derive ownership from case-folded path hashes.

The V1 guarantee is narrow: cooperating Filtrace processes cannot hold the fixed
per-target lock concurrently when the platform honors `FileShare.None`. Windows
rejects incompatible opens; Unix uses .NET's advisory file lock and rejects the
runtime setting that explicitly disables file locking. Exclusion on a Unix
filesystem where .NET cannot apply its advisory lock is best-effort and outside
the guarantee.

A same-user process that ignores the lock or replaces filesystem components
after validation is outside the threat model. The design does not claim to
sandbox against a hostile process with the same account.

## State model

V1 writes `schemaVersion: 1` at the new fixed state location. Unsupported values
at that location fail with an actionable message; they are not migrated by the
normal engine. This version is independent of PR #94's schemas 2-7 because those
manifests live at different paths and are never opened by the replacement engine.

- `installing`: the baseline is durable and active resources may be partial.
  Retry Install or start Restore.
- `active`: the local CLI, MCP, and skill are active. Refresh or Restore is
  allowed.
- `restoring`: baseline restoration may be partial. Only Restore may resume.
- `cleanup`: target resources are restored and only private state remains.
  Delete private artifacts and state without inspecting active resources.

Classify the operation immediately after acquiring the target lock and reading
the manifest. A `cleanup` retry branches before reading overlays, validating
active packages, or inspecting target content that no longer belongs to local
mode.

The baseline is immutable after the first successful write. Refresh replaces
only active local artifacts and never captures a new baseline.

## Mutation and recovery contract

### Prepare

Build, validate, and pack the source checkout before mutating the consumer.
Prepared artifacts live outside the consumer state root until the target lock is
acquired.

### Fresh Install

1. Acquire the per-target lock.
2. Build and validate the immutable `ResourcePlan`.
3. Capture MCP and skill baselines into the private state root.
4. Write `installing` state atomically.
5. Install the isolated CLI.
6. Atomically update the MCP file.
7. Stage and transactionally publish the skill.
8. Write `active` state atomically.

Any failure after step 4 leaves enough state for Restore. Do not delete the
baseline merely because Install failed.

The status remains `installing` after a failure. Until Resume Install or Restore
completes, the CLI, MCP, and skill may represent different local builds and local
mode is not considered active.

If a CLI install exceeds its deadline, retain its private operation directory
and process identifier. Because portable process APIs cannot confirm descendant
termination, block retry and recovery until the operator confirms termination
and removes that quarantine.

### Resume Install

1. Acquire the same lock and require `installing` state.
2. Validate the fixed plan, immutable baseline, and absence of a timeout
  quarantine.
3. Replay CLI installation, MCP update, and skill publication with the newly
  prepared content, without recapturing the baseline.
4. Write `active` state and leave baseline bytes unchanged.

Each resource operation must be idempotent when the prepared bytes match and
safely replace partial or older local content when they differ.

### Refresh

1. Acquire the same lock and require `active` state.
2. Validate the fixed plan and immutable baseline.
3. Validate bounded consumer overlay input.
4. Write `installing` state while retaining the original baseline.
5. Replace only active CLI, MCP, and skill artifacts using resource-scoped
  staging and publication.
6. Write `active` state and leave baseline bytes unchanged.

A failure after step 4 follows the same Resume Install or Restore paths as an
interrupted Fresh Install. Refresh does not attempt cross-resource rollback to
the prior local build.

### Restore

1. Acquire the same lock.
2. Set `restoring` before the first target mutation.
3. Restore CLI, MCP, and skill from the baseline.
4. Set `cleanup` only after all target resources are restored.
5. Delete backup artifacts.
6. Delete `state.json` last, then remove the empty state root if possible.

Every restoration step is idempotent. An interruption leaves `restoring`, and
the next Restore replays the fixed sequence from the immutable baseline rather
than consulting a mutation journal.

If cleanup stops after step 4, the next Restore classifies as Cleanup Retry and
performs only steps 5 and 6.

### Cleanup Retry

1. Acquire the same lock and require `cleanup` state.
2. Delete private backup artifacts without inspecting active resources.
3. Delete `state.json` last, then remove the empty state root if possible.

Cleanup Retry is idempotent. An empty leftover state directory is harmless and
may be reused by the next Fresh Install.

## Legacy transition

PR #94 was closed without merge, so its schemas are not a shipped compatibility
contract. Do not copy schema 2-7 branching into the replacement engine.

Before trying the replacement, anyone who ran PR #94 must restore using the
exact PR #94 checkout that created the state. The replacement wrapper should
detect the known PR #94 default state locations and stop with that instruction:

- `<filtrace-source>/artifacts/local-testing/state.json`;
- `<filtrace-source>/artifacts/local-testing/repositories/*/state.json`;
- the corresponding `<state-path>.workspace` directories.

The wrapper enumerates these paths but does not parse or mutate the manifests. A
PR #94 setup created with a custom `-StatePath` cannot be discovered safely; its
operator must restore it explicitly from the checkout and path that created it.

If preserving a legacy restore path is necessary, quarantine it in a one-shot
script that is not called by the V1 state engine and remove it after the
transition window. That decision requires evidence of an external user, not
merely the existence of review-era schemas.

## Implementation sequence

### Phase 0 - approve the reset

**Status:** Complete. V1 requires a Git target, and linked worktrees keep
independent state while Filtrace source checkouts targeting the same worktree
share its lock and baseline. The active PR #94 schema-2 setup was restored before
Phase 1 began, and PR #94 was closed as superseded after PR #98 merged.

- Review this plan.
- Confirm the fixed paths and Git-repository requirement.
- Restore any active PR #94 local setup.
- With explicit approval, close PR #94 as superseded only after the replacement
  PR is opened.

### Phase 1 - plan and state core

**Status:** Complete in PR #98. The helper and test projects contain fixed
`ResourcePlan` derivation, schema-1 source-generated serialization, atomic state
replacement, and explicit operation classification. The 53 focused tests passed
on Windows and Linux ARM64 before merge.

- Add the .NET helper project and test project.
- Implement `ResourcePlan`, manifest serialization, operation classification,
  and atomic state writes.
- Add pure unit tests for every status and invalid transition.

**Exit:** no consumer mutation exists yet; plan and state tests pass on Windows
and Linux ARM64.

### Phase 2 - Install and Refresh

**Status:** In progress. PR #99 merged exact MCP baseline semantics, bounded and
fingerprinted prior-skill capture, managed-path link rejection, and bounded
`overlay.md` input. PR #100 merged the fixed per-worktree lock after Windows and
Linux ARM64 validation. PR #101 merged prepared CLI package validation and fresh
private installation through an isolated one-package NuGet source, including
bounded package parsing, non-timeout cleanup, timeout quarantine, and installed
package verification. Its Windows and Linux ARM64 checks passed. The helper is
1,433 lines: 1,212 of the 1,300-line support budget and 221 of the 700-line
coordinator budget. The current local increment reuses bounded JSONC parsing for
baseline capture and mutation, atomically publishes the direct local MCP server,
preserves unrelated configuration and file metadata, and idempotently restores
the prior `filtrace` property and container/file shape while retaining later
additions. The helper is now 1,739 lines: 1,273 support and 466 coordinator.
Windows validation passes; Linux ARM64 validation for this increment remains
open. It does not yet mutate the skill resource.

- Implement resource-scoped skill staging and publication with the bounded
  consumer overlay.
- Wire Fresh Install, Resume Install, and Refresh through the coordinator while
  preserving baseline bytes.

**Exit:** fresh/previous MCP and skill combinations install and refresh without
global writes.

### Phase 3 - Restore and cleanup retry

- Implement ordered restore and the `restoring`/`cleanup` transitions.
- Add deterministic failpoints before and after every target mutation.
- Prove each interruption resumes without recapturing a baseline or touching an
  unrelated path.

**Exit:** every failpoint converges to the exact original state.

### Phase 4 - wrapper, docs, and CI

- Replace the large PowerShell implementation with the thin wrapper.
- Replace the current contract with a compact end-to-end matrix.
- Update README, CONTRIBUTING, docs, and CI.
- Run all repository gates and a read-only review pass.

**Exit:** the replacement PR is reviewable without relying on PR #94 history.

## Test strategy

### Unit tests

- fixed path derivation for normal repositories and linked worktrees;
- all operation classifications and invalid status/action pairs;
- `schemaVersion: 1` acceptance and rejection of missing, malformed, zero, and
  every known PR #94 value from 2 through 7 at the new state location;
- immutable baseline behavior;
- MCP object edits preserving unrelated properties;
- coordinator publication and restoration order, including replay from
  `installing` and `restoring`.

### Cross-platform integration contract

- fresh target with absent MCP and skill;
- existing MCP, skill, and consumer overlay;
- repeated Refresh preserving the first baseline;
- Restore after complete Install;
- Restore after every injected partial failure;
- cleanup retry with changed or missing active resources;
- failed Refresh followed by both Resume Install and Restore convergence;
- concurrent actions for the same target and independent actions for two
  targets;
- links in every managed ancestor and nested links in an existing skill;
- oversized and linked overlays;
- Git linked worktree isolation;
- paths containing spaces, Unicode, and case-distinct names where supported;
- proof that global CLI, user MCP, and user skill state never changes.

Use subprocess tests for locks, exit codes, and crash/retry behavior. Keep pure
state and plan tests in the .NET test project. Do not reproduce every branch in
both layers.

## Acceptance gates

The replacement cannot ship until all of these hold:

- one supported manifest schema;
- no user-selectable managed paths;
- no global or user-profile mutation;
- one target-derived lock and no machine-wide ownership registry;
- cleanup retry branches before active-resource validation;
- no recursive delete outside the fixed, marker-validated state root;
- all target mutations have deterministic failure-injection coverage;
- Windows and Linux ARM64 CI pass the full contract;
- macOS path behavior is either exercised or recorded as a manual gap;
- `dotnet test filtrace.slnx -c Release` and every existing repository contract
  remain green;
- non-coordinator support remains at or below 1,300 lines, the active-resource
  coordinator remains at or below 700 lines, and the complete helper remains at
  or below 2,000 lines, or scope is reduced again.

## Explicitly deferred

- arbitrary target paths and user-scope activation;
- global CLI compatibility;
- migration of PR #94 state schemas;
- multiple simultaneous Filtrace source checkouts controlling one target;
- a general-purpose filesystem transaction framework;
- remote, UNC, or network-backed target repositories.

These can return only with a concrete user scenario and dedicated threat model.

## Open decisions

Resolve these during plan review, before implementation:

1. How long should the one-shot PR #94 cleanup guidance remain available?

The Git-target, linked-worktree, 1 MiB overlay-limit, size-budget, and
status-driven recovery decisions are closed for V1. Resolve the remaining
transition-window question before Phase 4 ships the wrapper.
