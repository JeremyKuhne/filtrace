# Repository-local activation and recovery

**Status:** The fixed-path V1 workflow is shipped. This is an operator guide,
not another work plan. Windows PowerShell 5.1 and PowerShell 7 are covered by
the wrapper contract; a real Windows PowerShell 7 Install/Refresh/Restore
round trip was retained in September 2026. Real Unix activation and
interruption/quarantine recovery are not claimed as end-to-end validated.

Use the current checkout for one **Git** consumer repository without
replacing a global Filtrace installation:

```pwsh
./tools/Use-LocalFiltrace.ps1 -Action Install -TargetRepository ../consumer -Configuration Release
./tools/Use-LocalFiltrace.ps1 -Action Restore -TargetRepository ../consumer
```

`Install` on an already-active target is a Refresh; it does not replace
the original baseline. `-TargetRepository` defaults to the caller's
current directory. The wrapper requires PowerShell 5.1 or 7, directly
launchable `git` and `dotnet`, and the .NET 10 SDK. It does not elevate,
mutate a global tool, or upload a prepared package.

## Owned locations

The target worktree's `git rev-parse --absolute-git-dir` defines an
immutable [resource plan](../tools/Filtrace.LocalTesting/ResourcePlan.cs):

| Resource | Owned location |
| --- | --- |
| Durable state and isolated CLI | `<git-dir>/filtrace-local-testing/state.json` and `tools/` |
| Baseline/package artifacts | `<git-dir>/filtrace-local-testing/artifacts/` |
| Lock shared by source checkouts targeting this worktree | `<git-dir>/filtrace-local-testing.lock` |
| MCP configuration | `<target>/.vscode/mcp.json` |
| Shipped skill | `<target>/.agents/skills/filtrace` |

Linked Git worktrees have separate Git directories and target files. A
second Filtrace source checkout cannot silently replace the controlling
checkout's active state. No custom state, CLI, MCP, or skill destination
is supported. The implementation rejects links beneath managed roots
and preserves unrelated target content; it is not a sandbox against a
hostile process running as the same user.

## Recovery by durable status

The helper prepares source artifacts before changing the target, then
captures an **immutable** baseline before publishing the private CLI,
MCP config, and skill. It records four schema-1 statuses in `state.json`;
a failure after the baseline is written may leave some active resources
from the new checkout. Do not assume cross-resource rollback.

| Status in state.json | Meaning | Safe next command |
| --- | --- | --- |
| `installing` | Fresh Install or Refresh stopped with a baseline retained; the active CLI/MCP/skill may differ. | Retry `-Action Install` from the same source checkout to resume, or run `-Action Restore` to return to the baseline. |
| `active` | All three private resources were published. | Run `-Action Install` to refresh from the same source checkout, or `-Action Restore` to finish local mode. |
| `restoring` | Baseline restoration may be partial. | Retry `-Action Restore`; it replays the fixed restoration order. |
| `cleanup` | The target resources were restored; only private state/artifacts remain. | Retry `-Action Restore`; cleanup does **not** re-read or mutate current target resources. |

Restore removes the CLI, restores the captured Filtrace MCP baseline
(removing the locally added entry if none existed), preserves current
unrelated configuration, restores the skill baseline, cleans only the
fixed private artifacts, and removes `state.json` **last**.
An empty leftover state directory is harmless. Keep the state and
baseline intact after a failure; do not delete the target's `.vscode`
or `.agents` trees or manually replace a state status to force recovery.

Source preparation has a separate owned
`.filtrace-local-testing-preparation` directory in the source checkout's
Git directory. A timed-out or incomplete build, test, or pack retains it
and blocks another preparation without changing the consumer installation.
Confirm all related processes have stopped, then remove only the exact
private directory named in the diagnostic, not the consumer's baseline.

If `dotnet tool install` times out and descendant termination cannot be
confirmed, the helper retains the owned operation directory and PID and
**blocks retry**. Follow its diagnostic, confirm that the owned process
tree has stopped, and only then clear the named quarantine before retrying.
Do not interpret a stopped parent alone as proof that descendants exited.

Old PR #94 schemas 2-7 are **not** the shipped schema or an automatic
migration target. If the wrapper reports its known legacy state
locations, restore using the exact old checkout and path that created
that state before activating this V1 workflow. An old custom state path
cannot be safely discovered by this wrapper.

The [coordinator](../tools/Filtrace.LocalTesting/LocalTestingCoordinator.cs)
and [wrapper](../tools/Use-LocalFiltrace.ps1) are the implementation
contracts. The replacement of PR #94 with fixed ownership and resumable
state was completed by PR #120; future extensions require a concrete
contributor scenario, not a general-purpose filesystem transaction layer.
