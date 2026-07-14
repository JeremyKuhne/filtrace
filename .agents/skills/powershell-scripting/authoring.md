# Authoring reliable PowerShell

Use this page when creating or changing a script, function, or module. For native
processes and elevation, also read [native-processes.md](native-processes.md).

## Shape the entry point

Distinguish a CLI entry script from reusable functions:

- A user-facing script owns parameter binding, help, machine-output modes, and
  process exit codes. Translate an internal failure into an exit code once, at
  this outer boundary.
- A reusable function returns objects and writes PowerShell error records. It
  should not call `exit`, terminate the host, or preformat data for display.
- A module explicitly exports its public functions. Keep initialization and
  dot-sourcing free of surprising filesystem, network, or process side effects.

Use `[CmdletBinding()]` for public scripts and functions so common parameters and
consistent binding are available. Consider `PositionalBinding = $false` for an
automation-facing interface. Use approved verbs and singular nouns for exported
commands; preserve established public names unless a breaking change is intended.

For a state-changing command, `SupportsShouldProcess` only adds `-WhatIf` and
`-Confirm`; it does not protect anything by itself. Call
`$PSCmdlet.ShouldProcess($target, $action)` immediately around each side effect.
Avoid claiming `-WhatIf` support when some writes occur outside that guard.

## Parameters and help

- Give parameters concrete types. Prefer `[switch]` to a mandatory Boolean and
  distinguish omitted, empty, and explicit values where that affects behavior.
- Use `[Parameter(Mandatory)]`, `[ValidateNotNullOrEmpty()]`, `[ValidateSet()]`,
  and `[ValidateRange()]` for independent constraints. Validate relationships
  between parameters in normal code so the error can name the full condition.
- Use parameter sets when modes require different inputs. Make each set
  unambiguous and choose a default only when it is genuinely safe.
- Treat aliases, accepted enum-like values, defaults, and pipeline binding as
  compatibility commitments.
- Do not use raw `$args` for a public contract. In an advanced function, `$args`
  is unavailable for unbound values anyway.
- Put comment-based help where `Get-Help` recognizes it. Document every public or
  discoverable parameter, including an internal relaunch switch if it remains in
  the public parameter block. Test the rendered help rather than trusting layout.
- Use `#Requires` for a real pre-execution requirement. Do not require elevation
  there if the script body is responsible for self-elevation, because the body
  will never run to perform the handoff.

## Output is an interface

PowerShell has multiple observable streams. Assign each message intentionally:

| Intent | Channel |
| --- | --- |
| Data for a caller or pipeline | Success output, preferably typed objects |
| Recoverable or terminal failure | Error stream or a terminating error |
| Actionable caution | Warning stream |
| Opt-in operational detail | Verbose or Debug stream |
| Host-only presentation | Information stream / `Write-Host`, used sparingly |
| Long-running activity | Progress stream, with noninteractive behavior tested |

Prefer implicit output to `Write-Output`. Remember that every unassigned
expression and many method or cmdlet calls emit success output. Assign unwanted
results to `$null` or cast to `[void]`; `return` does not erase output already
emitted. Preserve arrays deliberately because pipeline enumeration can turn one
array result into many objects.

For a JSON or other machine-readable mode:

- Reserve stdout for exactly that format. Send status and diagnostics elsewhere.
- Define what success, partial completion, timeout, and failure emit in every
  supported output mode.
- Never return exit code zero with empty or malformed stdout when the contract
  promises a structured result.
- Serialize one bounded object at the end rather than streaming incidental
  objects into the serializer.
- Treat stdout, manifests, sidecars, and logs as independent output contracts.
  Repeated property names, case identifiers, or diagnostic wrappers can make a
  derived projection larger than the complete source artifact.

`Write-Host` is appropriate for display-only UI, not returned data. `Write-Verbose`
is usually the right channel for optional status. Do not convert objects to tables
or strings until the presentation boundary.

## Error ownership

PowerShell cmdlet errors can be terminating or nonterminating. Native nonzero
exit status is a separate mechanism. Handle both deliberately.

- Use `try`/`catch` around operations whose terminating errors you can add context
  to or recover from. Do not use an empty `catch`, and do not silently turn corrupt
  or unreadable input into an incomplete successful result.
- Catch narrowly enough to preserve the failing operation and original exception.
  Include actionable context without exposing secrets.
- Use `throw` or `$PSCmdlet.ThrowTerminatingError()` in reusable code. At a CLI
  entry point, map the failure to stderr/PowerShell error output and a documented
  nonzero exit code.
- If `$ErrorActionPreference = 'Stop'`, `Write-Error` itself can terminate. A
  sequence such as `Write-Error 'failed'; exit $code` may never reach `exit`.
  Either throw and let one outer boundary translate it, or make the write
  explicitly nonterminating with
  `Write-Error 'failed' -ErrorAction Continue; exit $code`. Do not change the
  global preference to repair one call site.
- Do not set a global preference merely to fix one command. Prefer that command's
  common parameter when the desired policy is local.

Save enough context before cleanup or logging can overwrite it. This includes the
native exit code, exception, failing path, and operation identity.

## Preserve dependency state

An optional tool or capability has more than two states. Model at least:

1. unavailable or intentionally disabled;
2. available and successful with data;
3. available and successful with a valid empty result;
4. available but exited nonzero or threw;
5. available but returned malformed or incomplete data.

Do not return the same `$null` sentinel for all five unless every caller applies
the same policy. In particular, a fallback justified when a tool is absent may be
unsafe after that tool was found but failed to read the input. Preserve a status
or reason alongside optional data, then make fallback, warning, command emission,
and exit behavior explicit for each state.

When changing one branch, inspect its neighboring states before moving on. A fix
for null input should prompt empty and unrecognized-value tests; a missing-tool
fallback should prompt present/success, present/nonzero, exception, malformed, and
incomplete-result tests.

## Paths and untrusted text

- Anchor repository or script assets at `$PSScriptRoot`, not the caller's current
  directory. If current directory is meaningful input, state that explicitly.
- Use `-LiteralPath` for user-provided paths unless wildcard expansion is part of
  the contract. A path containing `[` or `*` must not silently become a pattern.
- Use `Join-Path` and `[System.IO.Path]` APIs rather than concatenating separators.
  Canonicalize before containment checks, then verify a complete relative segment;
  a string merely starting with `..` or a sibling prefix is not enough.
- Rooted does not mean local. If network access is forbidden, reject UNC paths,
  network-style forward-slash paths, and URI schemes before opening the resource.
  For a security boundary, account for symbolic links and reparse points too.
- Use `.Replace()` for literal replacement. The `-replace` operator treats its
  pattern as regex and `$` sequences in the replacement specially; escape both
  sides only when regex semantics are intentional.
- Keep command, JSON, XML, CSV, URI, and Markdown data in their native structure.
  Do not validate one grammar and then splice the value into another unescaped.

Reject path traversal before reading or writing. Use same-directory temporary
files for atomic replacement and never let a generated relative path escape the
declared root.

## Text, JSON, and binary files

Encoding defaults differ sharply between Windows PowerShell 5.1 and PowerShell 7.
In particular, `Out-File`, redirection, and `-Encoding utf8` do not mean the same
bytes across those hosts.

- Choose an encoding as part of the file format. For deterministic UTF-8 without
  a BOM across both hosts, use `[System.Text.UTF8Encoding]::new($false)` with a
  .NET file API rather than relying on cmdlet defaults.
- When appending, match the existing file's encoding. Better, rewrite a structured
  artifact atomically instead of appending fragments.
- Keep cross-version source files ASCII where practical. Windows PowerShell 5.1
  can misread non-ASCII UTF-8 source without a BOM, while a BOM is undesirable to
  some Unix tools. If non-ASCII source is required, choose and test a policy.
- Do not send binary data through a text pipeline on a compatibility path.
  PowerShell 7.4 added byte-preserving native redirection, but earlier hosts decode
  and re-encode. Prefer a tool's file option or a byte-stream API.

Build JSON from `[ordered]` dictionaries or objects and `ConvertTo-Json` with an
explicit depth. Parse it back in a test. Measure budgets in encoded bytes, not
characters. Normalize missing or unknown enum-like input to an explicit safe state
instead of allowing `''`, `$null`, and an unrecognized value to acquire different
accidental meanings.

When the same operation emits multiple serialized shapes, enforce each budget on
the exact payload that leaves that boundary. Do not infer stdout size from an
under-budget manifest. If an oversized projection falls back to a compact envelope,
serialize and measure that fallback too; preserve the outcome, operation identity,
and a pointer or actionable route to the authoritative detail.

When partial files are harmful:

1. Serialize and validate the complete content in memory if it is bounded.
2. Enforce size and schema expectations before publication.
3. Write a unique temporary file in the destination directory with the chosen
   encoding.
4. Flush and atomically move or replace it using an API supported by the target
   filesystems.
5. Remove the temporary file in `finally` when publication fails.

## Resource lifetime and concurrency

Acquire resources as late as possible and release them in `finally`. This applies
to processes, streams, listeners, locks, temporary files, location changes, and
preference or environment changes. End-of-script cleanup is skipped by early
throws, and a script-scope `trap` is easy to break through scope mismatches.

- Use a GUID or an atomic create operation for unique names. `$PID` alone collides
  after crashes and across overlapping assumptions.
- A lock key must represent exactly the resource being protected. Overly broad
  keys block independent work; narrow keys permit corruption.
- Prefer ownership represented by an open handle over the mere existence of a lock
  file. Document behavior on network filesystems and release the handle in
  `finally`.
- Do not select "the newest" artifact from a shared historical tree. Isolate each
  run, reject or clean reused nonempty run directories, and pair outputs by a
  stable identity rather than timestamp or array index.
- Treat timeout as an outcome, not cleanup. Decide whether the operation may still
  be running, how it is cancelled, and what status the caller receives.

## Security boundaries

- Never evaluate data as code. Avoid `Invoke-Expression`, interpolated `-Command`
  payloads, dynamically generated script blocks, and user-controlled format or
  regex programs unless that language is the explicit input contract and is
  sandboxed appropriately.
- Treat process arguments, environment variables, temporary files, transcripts,
  verbose logs, and exception text as disclosure surfaces. Do not put secrets in
  command lines; use established credential, secret-store, stdin, or protected
  file mechanisms supported by the target tool.
- Execution policy is not a security boundary. Do not weaken it as a generic fix.
- Minimize elevated work. Build and restore without elevation, then elevate only
  the operation that requires it and prevent the elevated child from repeating
  unrelated writes.
- Verify downloaded content using an authenticated source and an expected hash or
  signature before use. Never execute a response merely because the download
  succeeded.
- Bind helper HTTP servers to loopback, validate ports, constrain paths and origins,
  handle preflight explicitly, stream large files, and dispose the listener. An
  HTTPS page fetching loopback HTTP may require Private Network Access headers;
  test the real browser flow rather than assuming CORS alone is enough.