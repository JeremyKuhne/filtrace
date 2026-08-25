# Test a local Filtrace build end to end

Use the repository helper when an unreleased change must be exercised through the
same CLI, MCP, and agent-skill surfaces as a shipped Filtrace release. It switches
all three surfaces together and records enough state to reverse the switch.

## Prerequisites

- .NET 10 SDK selected by [global.json](../global.json).
- PowerShell 7 or later.
- VS Code stable, or an explicit `-McpConfigPath` for another installation or
  profile.
- GitHub CLI with `gh skill` support (2.90 or later) only when manually
  re-vendoring a shipped skill without a saved baseline.

Run the helper from the repository root:

```pwsh
./tools/Use-LocalFiltrace.ps1
```

The command performs these operations in order:

1. Builds the solution and packs both heads under
   `artifacts/local-testing/packages`.
2. Runs the local MCP protocol check against the built DLL.
3. Records the current global CLI version and exact package bytes, the existing
  `filtrace` MCP entry, and the existing skill directory under
  `artifacts/local-testing`.
4. Reinstalls the CLI at the exact local package version from a NuGet
   configuration containing only the generated package directory.
5. Points the global VS Code `filtrace` MCP entry directly at the local
   `Filtrace.Mcp.dll`. This bypasses `dnx` and NuGet resolution.
6. Vendors the complete local skill into the GitHub Copilot user skill directory,
   preserving an existing consumer-owned `overlay.md`.

The first install owns the baseline. Running the command again after source or
skill edits refreshes the local packages and vendored skill without replacing
that baseline. Use `-SkipBuild` only when the existing local packages and DLL are
known to be current.

The baseline is written before the first user-level change. If install fails
after that point, fix the reported cause and either run Install again or run
Restore; do not delete the state directory to retry.

An already-running chat may need an MCP tool refresh or a new chat before the
new server and skill are discovered.

## Vendor the skill into a project

The default skill destination is the current user's Copilot skill directory. To
check in or test the skill as a project dependency, point the helper at the
consumer's vendor-neutral skill directory and give that setup its own state
directory:

```pwsh
./tools/Use-LocalFiltrace.ps1 `
  -SkillDestination D:\repos\consumer\.agents\skills\filtrace `
  -StatePath artifacts\local-testing\consumer\state.json
```

The entire skill directory travels: `SKILL.md`, `README.md`, and every bundled
script. An existing `overlay.md` remains consumer-owned. Use the same arguments
for restore.

For VS Code Insiders or a nondefault profile, also pass that installation's
`mcp.json`:

```pwsh
./tools/Use-LocalFiltrace.ps1 `
  -McpConfigPath "$env:APPDATA\Code - Insiders\User\mcp.json"
```

## Verify local mode

The helper already runs the MCP protocol check and verifies the installed CLI
version. These commands provide a quick visible confirmation:

```pwsh
dotnet tool list --global
Get-Command filtrace
./tools/Test-McpServer.ps1 -Configuration Release
./tools/Test-LocalFiltrace.ps1 -Configuration Release
```

The MCP entry is local only when its command is `dotnet` and its sole argument is
the Release or Debug DLL under this checkout. A `dnx` command selects a shipped
package instead.

## Restore the saved setup

Restore with the same path overrides used for install:

```pwsh
./tools/Use-LocalFiltrace.ps1 -Action Restore
```

Restore reinstalls the exact prior CLI package bytes from the local backup (or
removes the CLI when none was installed), restores or removes only the
`filtrace` MCP property, and restores the prior skill directory. It does not need
the original package feed to restore the CLI. Other MCP entries added while local
mode was active are retained. A changed `overlay.md` is carried back onto a
restored prior skill. When no skill existed before local mode, a newly added
overlay is retained beside the state path as `state.json.restored-overlay.md`
before the local skill is removed.

Do not delete `artifacts/local-testing/state.json` while local mode is active; it
is the rollback record. A successful restore consumes it so the next install can
capture a fresh baseline.

Restore validates all retained backups before changing anything. If a later
restore step fails, the state remains marked `restore-in-progress`; fix the
reported cause and run Restore again with the same path arguments.

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