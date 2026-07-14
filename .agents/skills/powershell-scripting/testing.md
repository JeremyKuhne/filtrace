# Testing PowerShell

Validation needs multiple layers because each catches a different class of defect.
Start with the narrowest behavior test for the changed path, then expand according
to host, platform, privilege, and output risk.

## 1. Parse under every supported host

The parser loaded in the current process only proves syntax for that PowerShell
version. Run the parser in both `powershell.exe` and `pwsh` when Windows PowerShell
5.1 and PowerShell 7 are supported.

```powershell
function Test-PowerShellSyntax([string] $Path) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        (Resolve-Path -LiteralPath $Path).Path,
        [ref]$tokens,
        [ref]$parseErrors)

    if ($parseErrors.Count -ne 0) {
        throw ($parseErrors | Out-String)
    }
}
```

Invoke that check in a clean `-NoProfile` process for each host. Include every
changed `.ps1`, `.psm1`, and `.psd1`; data files may need their own parser or
`Test-ModuleManifest` check.

A parser pass does not prove that a cmdlet parameter, .NET API, automatic variable,
or module exists at runtime.

## 2. Run PSScriptAnalyzer

Use the repository's pinned version and settings when present:

```powershell
Invoke-ScriptAnalyzer -Path $scriptPath -Settings $settingsPath
```

Treat analyzer findings as review inputs, not automatic truth. Resolve or record
intentional suppressions narrowly with a reason. Pay particular attention to:

- unapproved exported verbs and accidental `Write-Host` data output
- plaintext password-like parameters and dynamic code execution
- state-changing functions that claim no `ShouldProcess` support
- declared-variable, shadowing, unused-value, and unreachable-code findings
- compatibility rules configured for the actual PowerShell target profiles

An analyzer running on PowerShell 7 does not replace parsing and executing on 5.1.

## 3. Exercise behavior with Pester

Pin a modern Pester version rather than relying on the Windows PowerShell inbox
version. Keep unit tests deterministic and use `TestDrive:` for isolated files.
Mock at true external boundaries such as web requests, clock, or native adapters;
do not mock the function whose control flow is under test.

Test reusable functions in process, but test these behaviors by launching an actual
child `powershell.exe` or `pwsh` process with `-NoProfile`, not by dot-sourcing the
entry script into the current session:

- `exit` status and separation of stdout/stderr
- `-NoProfile` startup and ambient working directory independence
- parser and runtime behavior of each supported host
- native argument boundaries and `$LASTEXITCODE`
- process timeout, cancellation, and cleanup
- module import/export and script-scope initialization

For a fake native command, record each received argument separately and choose a
requested exit code. A PowerShell script is not a perfect substitute for every
native parser, so use a tiny native fixture when Windows command-line parsing is
the behavior being validated.

A fake `Start-Process` can pin parent control flow, timeout handling, and output
schema, but it cannot prove UAC behavior, integrity-boundary handle access, real
child lifetime, or process-tree cleanup. Name the layer each test covers and retain
a real-child test or manual Windows elevation gate for the behavior the fake omits.
When production code handles method or property failures, make the fake trigger
them too: `WaitForExit()` throws, `ExitCode` is inaccessible, or redirected output
cannot be read. A timeout-only fake does not cover those catches.

## 4. Test the failure paths

At minimum, cover the rows relevant to the change:

| Boundary | Cases |
| --- | --- |
| Parameters | omitted, empty, explicit empty array, zero, negative, min/max, conflicting set |
| Paths | spaces, quotes, wildcard chars, `$`, Unicode runtime value, trailing separator, relative, missing |
| Trust | `..` escape, sibling-prefix collision, UNC/network path, URI, symlink where relevant |
| Native command | success, nonzero, launch failure, stderr, malformed or missing structured output |
| Optional dependency | absent, present/success, valid-empty, present/nonzero, exception, malformed/incomplete result; fallback differs where policy differs |
| Process | quick exit, slow exit, timeout, inaccessible handle, cancellation, child left running |
| Elevation | already elevated, UAC accepted, UAC cancelled, unsupported OS, stable working directory |
| Files | preexisting output, reused nonempty run, partial write, corrupt input, encoding and BOM |
| JSON/status | one/many/empty items, nested depth, missing/empty/unrecognized enum-like values, round trip |
| Concurrency | same lock key rejects overlap, different keys proceed, crash releases ownership |
| Selection | stale historical files ignored, paired artifacts share identity, order changes |
| Output modes | each changed outcome crossed with every mode that reaches the branch; exit code, parseability, and diagnostic routing |
| Multi-output budgets | authoritative artifact under budget while a wrapped projection exceeds; fallback under budget and artifact remains complete |
| Cleanup | throw at each acquisition step, temp files/handles/listeners removed in `finally` |

These rows align with the [adjacent-state map](review.md#adjacent-state-map).
For $N$ modes and $M$ changed outcomes that share a decision branch, record the
$N \times M$ cells. Reduce the matrix only when separate control flow proves a
combination cannot reach the change.

Do not merely assert that source text contains `try` or `$LASTEXITCODE`. Trigger the
failure and observe the contract when practical.

Use distinct fake modes rather than one catch-all `$null` result. A fake dependency
should be able to report not-found, success, valid-empty, nonzero, throw, malformed,
and incomplete output independently. Assert both positive behavior and suppression:
the intended fallback appears only for absence, while failed or unverifiable states
do not emit commands or claims that require successful verification.
Force the absent mode explicitly even when CI has the real command installed; the
runner's PATH is not coverage of a deployment without that dependency.

## 5. Verify bytes and streams

For deterministic text output, inspect raw bytes:

- expected UTF-8 bytes and BOM policy
- newline policy if the artifact is compared byte-for-byte
- no host decoration or formatted-table truncation
- encoded byte count within the documented budget

For machine stdout, launch the script as a process, capture stdout and stderr
separately, parse stdout, and assert the exit code. Include a case that writes
diagnostics and one that times out. Redirection behavior changed in PowerShell 7.4,
so test binary or merged native streams on every supported host rather than
extrapolating from one.

When one operation writes a manifest or sidecar and also emits a JSON handoff,
measure the exact serialized bytes of each surface independently. Include an
adversarial case where the authoritative file remains under budget but repeated
identifiers or warning wrappers would push the full handoff over its limit. Assert
that the emitted fallback parses, stays under budget, preserves status and operation
identity, points to or locates the complete artifact, and is itself remeasured.

## 6. Validate help and `ShouldProcess`

- Run `Get-Help <script> -Full` and verify every public parameter is present with
  accurate mode and side-effect wording. Include internal/reserved relaunch switches
  that remain discoverable in the public parameter block.
- Invoke state-changing commands with `-WhatIf` and assert no file, process,
  network, or configuration change occurred.
- Test `-Confirm:$false` noninteractively. Do not let CI wait for a prompt.
- Check that unsupported-platform and privilege errors are early and actionable.

## 7. Build the compatibility matrix honestly

Use the smallest matrix that covers every claim:

| Claim | Minimum evidence |
| --- | --- |
| PowerShell 7 only, cross-platform | Parse and behavior on each supported OS |
| Windows PowerShell 5.1 and PowerShell 7 | Parse in both; behavior in both for changed runtime path |
| Windows-only elevation | Non-elevated and elevated Windows runs plus unsupported-OS rejection |
| Native tool integration | Fake-driven failure coverage plus at least one real-tool smoke test |
| Browser loopback handoff | Automated HTTP contract plus a real target-browser smoke test |
| Concurrency-safe | Deterministic overlapping processes, not sequential approximation |

If a required environment is unavailable, do not silently reduce the support
claim. Run what is available and report the exact gap.

## Recommended validation order

1. One regression test for the defect or requested behavior.
2. One adjacent-state expansion for the same decision branch.
3. Parser in the current host, then every other supported host.
4. Focused Pester file or contract script.
5. PSScriptAnalyzer with repository settings.
6. Real native/elevation/browser smoke test when the changed boundary requires it.
7. Broader repository tests and a final diff review.

Keep test diagnostics faithful: assertion text must describe the predicate and
expected behavior, or a failure will send the next reviewer in the wrong direction.