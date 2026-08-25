# Test a local Filtrace build end to end

Use the helper from the repository where an unreleased Filtrace change needs to be
exercised. The current working repository is the unit of scope: the helper installs
an isolated CLI, writes that repository's MCP entry, vendors that repository's
skill, and records enough state to reverse every switch.

## Prerequisites

- .NET 10 SDK selected by [global.json](../global.json).
- PowerShell 7 or later.
- A consumer repository where project MCP and skill configuration can be tested.
- GitHub CLI with `gh skill` support (2.90 or later) only when manually
  re-vendoring a shipped skill without a saved baseline.

Run the helper while the consumer repository is the current directory. The helper
finds the Filtrace checkout from its own script path, not from the working directory:

```pwsh
Set-Location D:\repos\consumer
& D:\repos\filtrace\tools\Use-LocalFiltrace.ps1
```

The command performs these operations in order:

1. Resolves the current directory as `TargetRepository`.
2. Builds the Filtrace solution and packs both heads into target-keyed ignored
  storage under the Filtrace checkout's `artifacts/local-testing` directory. All
  `dotnet` operations run from that checkout, so a consumer `global.json` cannot
  change the SDK selected for Filtrace.
3. Runs the local MCP protocol check against the built DLL.
4. Records the prior project MCP entry, project skill, and isolated CLI in a
  versioned manifest. Every manifest owns a dedicated `state.json.workspace`
  directory; cleanup never treats the manifest's parent as owned.
5. Installs the CLI with `dotnet tool install --tool-path` under that workspace.
  The existing global `filtrace` command is not changed. The selected package
  bytes are verified against the newly packed package.
6. Points `.vscode/mcp.json` in the consumer repository directly at the local
  `Filtrace.Mcp.dll`. This bypasses `dnx` and NuGet resolution.
7. Vendors the complete local skill into `.agents/skills/filtrace` in the consumer
  repository, preserving an existing consumer-owned `overlay.md`.

The helper prints the exact isolated `filtrace` executable path. Use that path for
CLI-only checks and pass it to helpers that expose `-FiltracePath`. Agent analysis
inside the consumer repository can use the project-local `trace_*` MCP tools.

The first install owns the baseline. Running the command again from the same
consumer repository after source or skill edits refreshes local mode without
replacing that baseline. Use `-SkipBuild` only when the existing target-keyed
packages and DLL are known to be current.

The baseline is written before the first consumer configuration change. If install
fails after that point, fix the reported cause and either run Install again or run
Restore; do not delete the manifest or its owned workspace. One process holds an
exclusive lock keyed by `StatePath` for the complete action, so an overlapping
install or restore is rejected before MCP, skill, CLI, or manifest mutation.

An already-running chat may need an MCP tool refresh or a new chat before the
project server and skill are discovered.

## Select another target or path

From the Filtrace checkout, target a consumer explicitly:

```pwsh
./tools/Use-LocalFiltrace.ps1 -TargetRepository D:\repos\consumer
```

The defaults and their scope are:

| Surface | Default |
| --- | --- |
| MCP | `<target>/.vscode/mcp.json` |
| Skill | `<target>/.agents/skills/filtrace` |
| CLI | Isolated `--tool-path` under the manifest-owned workspace |
| State | Target-keyed ignored storage under `<filtrace>/artifacts/local-testing/repositories` |

`-McpConfigPath`, `-SkillDestination`, `-CliToolPath`, and `-StatePath` override
those paths. A user-profile MCP path or user skill destination is therefore an
explicit broadening of scope, not the default. `-SkipCli` leaves every CLI install
unchanged when only MCP and skill behavior needs testing.

For VS Code Insiders or a nondefault profile, an explicit user MCP override is
still available:

```pwsh
./tools/Use-LocalFiltrace.ps1 `
  -TargetRepository D:\repos\consumer `
  -McpConfigPath "$env:APPDATA\Code - Insiders\User\mcp.json"
```

The entire skill directory travels: `SKILL.md`, `README.md`, and every bundled
script. An existing `overlay.md` remains consumer-owned. The helper rejects exact,
ancestor, or descendant overlap between the destination and the Filtrace source
skill before any target write, resolving existing symbolic-link and junction
ancestors to their physical targets first.

## Verify local mode

The helper runs the MCP protocol check and verifies the installed CLI package
version and bytes. Use the exact CLI path printed by Install for a visible smoke
test:

```pwsh
& '<printed-filtrace-path>' --version
D:\repos\filtrace\tools\Test-McpServer.ps1 -Configuration Release
D:\repos\filtrace\tools\Test-LocalFiltrace.ps1 -Configuration Release
```

The project MCP entry is local only when its command is `dotnet` and its sole
argument is the Release or Debug DLL under the Filtrace checkout. A `dnx` command
selects a shipped package instead. The global `filtrace` command, global MCP file,
and user skill are not evidence for the repository-scoped setup.

## Restore the saved setup

Run Restore from the same consumer repository:

```pwsh
& D:\repos\filtrace\tools\Use-LocalFiltrace.ps1 -Action Restore
```

If Install used `-TargetRepository` or a custom `-StatePath`, use the same selector
to locate the manifest. Once found, the manifest supplies the recorded MCP, skill,
and CLI paths; they do not need to be repeated.

Restore reinstalls exact prior CLI package bytes for an explicit pre-existing tool
path, or removes the target-owned CLI when none existed. It restores or removes
only the `filtrace` MCP property and restores the prior skill directory. Other MCP
entries added during local testing are retained. A changed `overlay.md` is carried
back onto a restored prior skill. When no skill existed before local mode, a newly
added overlay is retained beside the state path as `state.json.restored-overlay.md`
before the local skill is removed.

When the project MCP file or skill parent directories did not exist before local
mode, Restore removes only the empty paths it created. Files or entries added later
prevent that cleanup and are retained.

Restore validates retained CLI bytes and the complete prior skill-directory
fingerprint before changing anything. If a later restore step fails, the manifest
remains marked `restore-in-progress`; fix the reported cause and run Restore again.
A successful restore consumes the manifest and its owned workspace.

## Restore a legacy global setup

Early versions of this workflow used `artifacts/local-testing/state.json` and
changed the global CLI, user MCP entry, and user skill. When that version-2 manifest
exists, run Restore from the Filtrace checkout before starting a repository-scoped
install:

```pwsh
Set-Location D:\repos\filtrace
./tools/Use-LocalFiltrace.ps1 -Action Restore
```

The helper recognizes the legacy default manifest and restores it, but refuses to
refresh the old broad setup. Custom version-2 manifests can also be restored with
`-StatePath`; generic sibling directories beside a custom manifest are preserved.
Version-3 repository-scoped manifests from the preview workflow are likewise
restore-only; the next Install records a version-4 manifest with skill-backup
integrity metadata.

## Return manually to shipped releases

When no saved state exists, remove the local CLI and install the current stable
NuGet release:

```pwsh
dotnet tool uninstall --global KlutzyNinja.Filtrace
dotnet tool install --global KlutzyNinja.Filtrace
```

Replace the global MCP `filtrace` entry with package resolution through `dnx`:

```json
{
  "type": "stdio",
  "command": "dnx",
  "args": ["KlutzyNinja.Filtrace.Mcp", "--yes"]
}
```

Then replace the locally copied user skill with the skill from the matching
Filtrace release tag:

```pwsh
gh skill install JeremyKuhne/filtrace .agents/skills/filtrace `
  --pin v0.6.3 --agent github-copilot --scope user --force
```

Use the current release tag instead of `v0.6.3`. For a project-vendored skill,
run the command from that repository with `--scope project`. The shipped
`KlutzyNinja.Filtrace.Mcp` package also carries the complete skill under
`skills/filtrace`.

To pin every surface to one older shipped release, use the package version for
both NuGet heads and the corresponding `v` tag for the skill:

**Keep all three versions aligned.** A CLI package, MCP package, and skill from
different releases may expose incompatible commands, tools, or guidance.

```pwsh
$version = '0.6.3'
dotnet tool install --global KlutzyNinja.Filtrace --version $version
```

```json
{
  "type": "stdio",
  "command": "dnx",
  "args": ["KlutzyNinja.Filtrace.Mcp@0.6.3", "--yes"]
}
```

```pwsh
gh skill install JeremyKuhne/filtrace .agents/skills/filtrace `
  --pin v0.6.3 --agent github-copilot --scope user --force
```

Remove an already installed CLI before changing it to an older version. Keeping
the CLI package, MCP package, and skill tag aligned avoids testing mixed contracts.
