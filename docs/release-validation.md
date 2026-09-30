# Release validation

This is the operational release-check contract, not a second roadmap.
It was added after [v0.10.0](https://github.com/JeremyKuhne/filtrace/releases/tag/v0.10.0)
was published using the previous workflow. It does not retroactively change that
release, and passing these checks never authorizes a tag or NuGet push.

## Provenance before publication

The [publish workflow](../.github/workflows/publish.yml) requires a clean
candidate in `main` history before building or requesting a publishing key.
The candidate must be the recorded merge commit of one PR targeting this
repository's `main`; its tree must match that PR's exact reviewed head.

The latest GitHub Actions checks for agent files, Linux ARM64, Windows, and
the required `ci` aggregate must all have completed successfully on that
head. Missing, failed, skipped, pending, ambiguous, malformed, or incomplete
evidence is rejected. This supports squash merges without requiring a CI
check on the distinct squash commit itself.

For a read-only check of a clean candidate checkout:

```pwsh
./tools/Assert-ReleaseReady.ps1 `
  -TagName <proposed-v-version> `
  -CommitSha <approved-merge-commit-sha> `
  -Repository JeremyKuhne/filtrace
```

The proposed tag name is validated as a canonical semantic version, with
no build metadata. The command creates no tag, release, package, or
repository setting. [Test-ReleaseReadiness.ps1](../tools/Test-ReleaseReadiness.ps1)
pins the provenance and query-failure boundaries.

## Check the packed artifacts, not just build outputs

[Test-ReleasePackages.ps1](../tools/Test-ReleasePackages.ps1) requires exactly
the CLI and MCP packages at one expected version. It validates their
nuspec/tool identities and the stamped MCP manifest, then installs from
an isolated local feed into owned tool paths and caches.

The installed CLI must return the expected version, canonical help,
valid `info`/`rank` results, and a usage failure for a removed command.
The installed MCP server must pass initialize, the exact 18-tool list,
stdout purity, schema budget, and a real tool call. EventPipe and
speedscope fixtures are copied; ETL is additionally required on Windows.

```pwsh
./tools/Test-ReleasePackages.ps1 `
  -PackagesDirectory <new-local-package-directory> `
  -ExpectedVersion <exact-package-version> `
  -OutputDirectory <local-evidence-parent>
```

Each invocation claims a unique run. Bounded logs, package/output hashes,
protocol results, and `smoke.json` are retained on success or failure;
only its installed tools, feed, caches, homes, and copied fixtures are
removed. Nothing is installed globally, captured, elevated, or published.

[Test-ReleasePackagesContract.ps1](../tools/Test-ReleasePackagesContract.ps1)
uses fake archives and child processes to test failure, timeout, output,
and cleanup behavior. Ordinary Linux PR CI runs both deterministic
contracts and the real package smoke; publish runs the real smoke on the
tag-version packages before NuGet login.
