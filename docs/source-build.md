# FastTrace Source Build

Filtrace uses `Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.6 by default. For
bounded FastTrace source evaluation, pass an explicit absolute FastTrace checkout path:

```pwsh
dotnet build
dotnet build -p:FastTraceRepoRoot="<absolute FastTrace checkout path>"
```

The source build replaces the TraceEvent package reference with a project reference
to `src/fasttrace/fasttrace.csproj`; the published Touki dependency is unchanged.
Use separate checkouts or `-p:ArtifactsPath="<owned build directory>"` when retaining
both engines' outputs. Changing engine selection requires a restore; do not reuse
`--no-restore` assets from the other selection.
Use FastTrace `73b32fe690491bc3d3ba05080ae2d5eb59ba01cb` only to reproduce the
historical replacement-assessment baseline. A future independently authorized
source-build experiment must pin its selected FastTrace revision rather than
silently reusing this historical hash. This namespace-adjusted source integration
is not a binary assembly-identity drop-in.

To publish an owned native CLI output on a native x64 host, first install the
[Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites)
for that host, then run:

```pwsh
dotnet publish src/Filtrace/Filtrace.csproj -c Release -r win-x64 `
  -p:PublishAot=true -p:PackAsTool=false -p:IsPackable=false `
  -p:RestoreLockedMode=false `
  -p:FastTraceRepoRoot="<absolute FastTrace checkout path>" `
  -o "<owned output directory>"
```

Use `linux-x64` instead of `win-x64` on a Linux x64 host. Native AOT compilation
must run on the target operating system. A RID publish can modify
`src/fasttrace/packages.lock.json` in the FastTrace checkout; prefer an isolated
clone or worktree for source publishing and inspect its state afterward.

Windows native PDB/source resolution requires an appropriately licensed existing
`msdia140.dll` in the native deployment, for example `amd64/msdia140.dll` beneath
the Windows x64 output directory. FastTrace does not automatically include
or redistribute DIA in the output. Managed portable PDB support does not require DIA.

This path is build-only evaluation guidance. It does not change Filtrace's default
engine, publish packages or version tags, or authorize distributing source-built
CLI or MCP packages. The measured consumer and replacement limits are summarized
in the [design](design.md#performance-evidence-and-dependency-boundaries);
the local validation contract is below.

## Local Validation And Blocked CI

Run the adoption check locally on a host matching the requested runtime identifier:

```pwsh
./tools/Test-FastTraceSourceBuild.ps1 `
  -FastTraceRepoRoot "<absolute FastTrace checkout path>" `
  -RuntimeIdentifier win-x64 `
  -OutputDirectory "<new owned evidence directory>"
```

The check builds the complete framework-dependent and Native AOT CLIs, then requires
exact JSON agreement for six committed-fixture queries. Use an isolated checkout for
the restore/build outputs described above. A local x64 pass does not validate ARM64
or another operating system.

Separate local Linux x64 JIT/Native AOT execution verified six matching
fixture queries and an explicit EventPipe handoff for unsupported ETW
collection. This is bounded x64 evidence, not Linux ARM64, macOS, or
the blocked hosted matrix.

The `source adoption` CI matrix remains **blocked** while FastTrace is
private. Its attempted ARM64/macOS rows stopped at checkout, not at an
executed build; they are not native-platform validation. Local evaluation
does not authorize a visibility change, cross-repository credential, or
package publication.

The workflow definition is retained for later use, but its automatic PR trigger is
removed. Do not dispatch it while access remains blocked. Ordinary CI and review
still gate source-build changes; the deferred matrix is not counted as passing.
When runnable, its artifact list contains only the summary, build/query logs, and
per-query JSON, not traces, symbols, assemblies, or native binaries.

## Current Check Limits

This is an attended functional check, not a hardened unattended runner. Its process
waits have no local deadline. If a child hangs, stop the run and verify the owned
process tree has exited before retrying.

The summary records Git revisions, not dirty-tree source hashes. Use clean isolated
checkouts for attributable runs, or retain source status and changed-file hashes
separately. Native restore can change the FastTrace lock file, as noted above.

The six-query script checks project publishes, not Release configuration propagation
through a solution. That behavior was separately validated by matching the deployed
FastTrace DLL to the Release output and distinguishing it from Debug. Automated
configuration assertions, dirty-tree provenance, and process deadlines are deferred
in the historical harness-hardening backlog. They are documented limits, not active
primary-plan work.
