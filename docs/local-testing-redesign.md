# Local Filtrace testing redesign plan

**Status:** Phase 1 merged in
[PR #98](https://github.com/JeremyKuhne/filtrace/pull/98). Phase 2 baseline
capture and bounded overlay input merged in
[PR #99](https://github.com/JeremyKuhne/filtrace/pull/99), and the fixed
per-worktree lock merged in
[PR #100](https://github.com/JeremyKuhne/filtrace/pull/100). Prepared CLI package
validation and fresh private installation merged in
[PR #101](https://github.com/JeremyKuhne/filtrace/pull/101). Structured MCP
publication and baseline restoration merged in
[PR #102](https://github.com/JeremyKuhne/filtrace/pull/102). Reversible skill
publication merged in
[PR #105](https://github.com/JeremyKuhne/filtrace/pull/105). Fresh Install,
Resume Install, and Refresh coordinator wiring merged in
[PR #107](https://github.com/JeremyKuhne/filtrace/pull/107). Phase 3 restore and
cleanup coordinator wiring is implemented; the Phase 4 wrapper remains pending.

**Last verified:** 2026-09-05 for Phase 3 on `pp01-local-testing-restore`, based
on `bc6752b`. The 242 local-testing tests pass in Debug and Release on Windows.
PR #94 was closed without merge after PR #98 established the replacement.

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

PR #94 accumulated compatibility branches and overlapping ownership rules across
many follow-up rounds. Its recurring findings concentrated in five coupled
responsibilities:

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
5. run the ordinary contract without elevation on the primary development
   platform.

## Engineering standard

This is contributor testing infrastructure, not a normal product feature or a
security boundary. It operates on a user-selected local checkout and consumer
repository, using artifacts built by that same user. Engineering effort must be
proportional to that scenario.

Aim for robust and maintainable behavior:

- preserve the immutable baseline and unrelated consumer content;
- use bounded reads and fixed managed paths so ordinary mistakes cannot redirect
  or exhaust the helper;
- make normal Install, Refresh, Restore, retry, and interruption paths converge
  predictably;
- fail with an actionable error while retaining recoverable state when an
  operation cannot safely continue;
- keep resource ownership, state transitions, and filesystem mutation in
  cohesive, directly tested types;
- prefer straightforward BCL code over compatibility layers or a general
  transaction framework.

Do not add production-grade machinery solely for hostile same-user mutation,
distributed coordination, network filesystems, automatic migration of review-era
state, every filesystem metadata variant, or an exhaustive platform matrix.
Those scenarios require a demonstrated contributor need before they expand V1.
Source-line counts are not a gate; readability, cohesion, duplication, and the
ability to review and test each change are the maintainability gates.

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
it created, and Restore removes only those that are still empty. Skill staging
and retirement use fixed hidden siblings directly under `.agents`, outside the
agent-discovered `skills` directory.

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
package verification. Its Windows and Linux ARM64 checks passed. The next
increment reused bounded JSONC parsing for baseline capture and mutation,
atomically published the direct local MCP server, preserved unrelated
configuration and file metadata, and idempotently restored the prior `filtrace`
property and container/file shape while retaining later additions. PR #102 merged
that increment after Windows and Linux ARM64 validation.
The current local increment stages the bounded source skill outside the discovered
skills directory, verifies the staged fingerprint, carries the exact consumer
overlay into each publication, atomically swaps fixed sibling directories, and
idempotently restores either the exact backup or the absent baseline. Fixed
staging and retirement paths make interrupted swaps recoverable. The helper is
validated on Windows; broader platform validation is backlog work. PR #105 merged
that increment. The current increment wires Fresh Install, Resume Install, and
Refresh through the coordinator while preserving the first baseline. CLI
replacement is staged beside the fixed private tool directory so an install
failure or timeout leaves the prior CLI intact and retains the operation
quarantine when manual recovery is required.

- Wire Fresh Install, Resume Install, and Refresh through the coordinator while
  preserving baseline bytes.

**Exit:** fresh/previous MCP and skill combinations install and refresh without
global writes.

### Phase 3 - Restore and cleanup retry

**Status:** Implemented and locally validated. The coordinator writes `restoring`
before target mutations, restores CLI/MCP/skill and baseline-created empty parents,
then writes `cleanup`. Private artifacts are deleted before the state manifest.
Cleanup retry branches before active-resource and baseline inspection.

Failure-injection tests cover each coordinator resource boundary, retry from
`restoring`, and cleanup-only retry with changed active resources. CLI cleanup
honors timeout quarantine and remains limited to the fixed private directory.
Real UAC and hostile same-user filesystem races are not covered by these tests.

**Exit:** representative interruption paths converge to the exact original state.

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

### End-to-end integration contract

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
- paths containing spaces and Unicode;
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
- expected partial resource and durable-state boundaries have focused
  failure-injection coverage;
- the full contract passes on the primary development platform;
- `dotnet test filtrace.slnx -c Release` and every existing repository contract
  remain green.

## Explicitly deferred

- arbitrary target paths and user-scope activation;
- global CLI compatibility;
- migration of PR #94 state schemas;
- multiple simultaneous Filtrace source checkouts controlling one target;
- a general-purpose filesystem transaction framework;
- remote, UNC, or network-backed target repositories.

These can return only with a concrete user scenario and dedicated threat model.

## Validation backlog

- Run the local-testing contract on Linux ARM64 and macOS as non-blocking
  follow-up validation. Address concrete failures without treating exhaustive
  platform coverage as a V1 release gate.

## Open decisions

Resolve these during plan review, before implementation:

1. How long should the one-shot PR #94 cleanup guidance remain available?

The Git-target, linked-worktree, 1 MiB overlay-limit, proportional-robustness,
and status-driven recovery decisions are closed for V1. Resolve the remaining
transition-window question before Phase 4 ships the wrapper.
