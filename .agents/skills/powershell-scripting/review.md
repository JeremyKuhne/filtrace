# Reviewing PowerShell

Review the changed behavior and its contracts, not just the syntax. A parser and
PSScriptAnalyzer can be clean while the script hangs, emits the wrong exit code,
selects stale data, or corrupts output under another host.

## Review procedure

1. Read the script's comment-based help, callers, CI invocation, and nearest tests.
   Write down the supported hosts, platforms, output modes, and side effects.
2. Trace every success and failure path from parameter binding to final output and
   exit status. Include early returns, `throw`, `exit`, timeout, cancellation, and
   cleanup.
3. Inventory every native command and `Start-Process` call. Verify argument
   boundaries, working directory, stream handling, immediate exit-code checks,
   process lifetime, and elevation behavior.
4. For each external dependency or optional capability, write the state matrix:
  absent, success-with-data, valid-empty, nonzero/exception, and malformed or
  incomplete output. Verify that states sharing a sentinel really share policy.
5. Trace every path from input to filesystem, network, command line, regex,
   serializer, and log. Check grammar transitions and privilege boundaries.
6. Identify all acquired resources and shared names. Confirm exact lock identity,
   unique run ownership, stable artifact pairing, and `finally` cleanup.
7. Compare APIs and syntax against the oldest PowerShell and operating system the
   script claims to support. Do not infer runtime support from a modern parser.
8. Run the cheapest behavior test that can falsify each risky assumption. Add a
   regression test for a concrete defect, not merely a source-text assertion when
   executable behavior is available.

## Adjacent-state map

Classify the changed decision, then expand the matching row. Cross only dimensions
that reach the same changed branch; do not generate a blind full product.

| Changed boundary | Adjacent states to inspect |
| --- | --- |
| Enum/status input | missing property, `$null`, empty, each known value, unrecognized value |
| Optional dependency | absent, success-with-data, valid-empty, nonzero, throw, malformed/incomplete result |
| Output mode | each changed outcome in every mode that traverses the branch; verify exit, parseability, and stream routing |
| Process lifetime | normal exit, timeout, wait throws, exit code inaccessible, cancellation, cleanup failure |
| Size limit | below limit, exact boundary, above limit, fallback serialized and remeasured |
| Resource ownership | acquisition fails, body fails, release fails, overlapping owner, abandoned resource |

Record the relevant rows in working notes with expected policy and evidence. A row
may cite an executable test, a manual platform gate, or an explicit residual gap;
an unexamined cell is not evidence. Use the corresponding
[failure-path test matrix](testing.md#4-test-the-failure-paths) to turn each
applicable row into executable cases.

## High-signal search targets

Search the changed script and nearby helpers for these constructs, then inspect
their semantics rather than banning them mechanically:

- `Invoke-Expression`, `ScriptBlock]::Create`, interpolated `-Command`, and `--%`
- `Start-Process`, `-ArgumentList`, `-Verb RunAs`, `-Wait`, and `-PassThru`
- native commands, `$LASTEXITCODE`, `$?`, `2>&1`, `*>`, and `Tee-Object`
- `Write-Error` near `exit`, broad `catch`, empty `catch`, and script-scope `trap`
- `Set-Content`, `Out-File`, redirection, `ConvertTo-Json`, and `ConvertFrom-Json`
- `Get-ChildItem` sorted by time, reused output directories, and PID-only temp names
- `Join-Path`, `Resolve-Path`, `GetFullPath`, `-replace`, wildcard-aware `-Path`
- `$IsWindows`, hardcoded `pwsh` or `powershell.exe`, and Windows-only .NET APIs
- `ReadAllBytes`, `HttpListener`, unbounded waits, and resources disposed only at
  the normal end of the script
- output-format switches whose error and timeout branches emit a different shape

## Patterns that deserve proof

| Plausible pattern | Failure to look for | Evidence required |
| --- | --- | --- |
| `$ErrorActionPreference = 'Stop'` | Native nonzero exits still continue on Windows PowerShell 5.1 | Explicit saved `$LASTEXITCODE` and a failure test |
| `Write-Error; exit $code` | `Write-Error` terminates before the intended exit | One outer translator or an explicitly nonterminating write |
| `Start-Process -ArgumentList $array` | Elements are joined and reparsed; spaces or quotes change boundaries | Target-specific encoding and adversarial argv tests |
| `Start-Process -Verb RunAs -Wait` | Parent hangs or cannot inspect the elevated handle | Bounded wait, exception handling, timeout test |
| Hardcoded `pwsh` | Windows PowerShell-only machine cannot relaunch | Current-host discovery and both-host parser checks |
| `if ($IsWindows)` | Variable is absent in Windows PowerShell 5.1 | A cross-edition platform helper and host tests |
| Parse stdout as JSON | Native command may already have failed | Exit check before parse and malformed-output test |
| Missing tool and failed tool both return `$null` | A fallback intended for absence runs after nonzero exit, exception, or invalid output | Preserve outcome provenance; test absent, success, valid-empty, failure, and malformed/incomplete states |
| `Set-Content -Encoding utf8` | BOM differs between 5.1 and 7 | Byte-level encoding assertion |
| `-replace $path` | Path is regex; `$` in replacement is special | Literal replacement or escaped pattern/replacement tests |
| `$PID` in a temp filename | Collision after crash or concurrent reuse | GUID/atomic create plus overlap test |
| Cleanup at script end | Early throw or cancellation leaks resource | `finally` and failure-path assertion |
| `catch { return }` | Corrupt input silently produces partial success | Warning/failure naming the file and parse cause |
| Newest file under a shared tree | Stale artifact from an earlier run wins | Isolated run directory and stable identity pairing |
| Pair arrays by sorted index | Independent orderings silently mismatch | Shared key or proof-generating assertion |
| Lock by only one dimension | Independent jobs contend or same job races | Lock key equal to the documented resource identity |
| Create output directory with `-Force` | Reused run mixes stale and fresh files | Reject or explicitly clean a nonempty operation directory |
| One artifact fits its budget | A derived stdout/sidecar projection exceeds its own budget through repeated identifiers or wrappers | Measure every exact encoded surface; exercise a bounded fallback while the authoritative artifact stays complete |
| Empty or unknown status string | Warning logic mistakes uncertainty for a state | Explicit normalized `unknown` value |
| `exit 0` on timeout | Machine mode emits no promised JSON | Structured timeout result or nonzero documented failure |
| `ReadAllBytes` to serve a file | Large input doubles memory or causes OOM | Stream copy and large-file test |
| Bind error says "port in use" | Actual cause is access denied or URL reservation | Preserve exception and actionable alternatives |
| CORS header on loopback server | Secure-origin browser still sends PNA preflight | OPTIONS/PNA handling tested in target browser |
| `GetFullPath` plus `Test-Path` | Relative link can escape the allowed root | Canonical containment check with `..` and sibling-prefix tests |
| Rooted path accepted as local | UNC or network URI triggers remote access | Explicit local-resource policy and rejection test |
| A parser pass in PowerShell 7 | Syntax or API is unavailable in 5.1 | Parser under both hosts and behavior on claimed runtime |

## Contract checks

### Parameters and help

- Do validation attributes reject zero, negative, empty, and out-of-range values
  before opaque framework exceptions occur?
- Are all discoverable parameters documented, and does the synopsis describe what
  the current implementation actually does?
- Does each mode accept only parameters it can honor? An allowlist that forwards a
  universal `--format` or single input shape to incompatible commands is a defect.
- Does an explicitly supplied empty collection fail when doing no work would be
  misleading?

### Output and status

- Does stdout contain only promised data in machine mode?
- For every output mode, do success, partial completion, timeout, and failure
  produce the promised parseable shape and exit status?
- Are token, size, count, and success metrics computed from the exact data consumed
  or emitted, including failures and truncation?
- Is each serialized surface budgeted independently? If a projection falls back,
  is the fallback itself measured and does it retain status, operation identity,
  and a route to complete details?
- Does every failure preserve the original native exit code when that is the most
  useful status?
- Can the code distinguish unavailable capability from attempted-but-failed or
  incomplete capability? Are fallbacks limited to the states that justify them?
- On timeout or failure, does machine output retain a non-secret breadcrumb to
  diagnostics without polluting its structured stdout contract?
- Do warnings state uncertainty accurately? Do not label provider-dependent or
  unobserved behavior as disabled when it is merely unknown.
- Do assertion messages describe the condition that actually failed?

### Files, identity, and comparison

- Are grouping and comparison keys complete? Host, architecture, model, project,
  target, and mode may all affect whether two records are comparable.
- Does update mode preserve unsupported-platform data or fail fast instead of
  deleting baseline entries it skipped?
- Are files selected from the current operation only? Are every trace and sidecar
  paired by a shared stem or another stable identity?
- Are deterministic artifacts bounded, explicitly encoded, and parseable after
  write?

### Security and privilege

- Can quotes, newlines, trailing backslashes, wildcards, replacement `$` tokens,
  or path separators cross into a second grammar?
- Can an input cause network access when only local files are intended?
- Does the elevated child perform only required privileged work, with no restore,
  build, or unrelated artifact ownership changes?
- Are logs and machine results operation-specific, bounded, and free of secrets?

## Writing findings

Lead with findings ordered by severity. Each finding should identify the relevant
file and line, the triggering input or state, the observable impact, and the
smallest credible repair or missing test. Distinguish a proven defect from an open
question. Do not spend the review on aliases or brace style while exit status,
argument parsing, cleanup, or compatibility remains unverified.

When no defect is found, say so and state the unexecuted matrix or residual risk,
especially elevation, real native tooling, browser handoff, and older-host tests.

After fixing a review finding, expand one state outward before declaring the round
complete. Review comments identify one observed input, not the full equivalence
class: test adjacent null/empty/unrecognized, absent/success/failure/incomplete,
mode/outcome, or under/over-budget states controlled by the same branch.