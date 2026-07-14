---
compatibility: Guidance covers Windows PowerShell 5.1 on Windows and PowerShell 7 on Windows, Linux, and macOS; validation tools are optional unless the consuming repository requires them.
description: Create, revise, debug, test, and review production PowerShell scripts and modules. Use when writing or reviewing .ps1, .psm1, or .psd1 files; investigating PowerShell PR feedback; designing parameters, output streams, native-command calls, Start-Process or elevation flows, cleanup, locking, file and JSON output, Pester tests, PSScriptAnalyzer checks, or Windows PowerShell 5.1 versus PowerShell 7 compatibility. Also use for PowerShell security and reliability review.
license: MIT
metadata:
    applicability: universal
    binding: optional-overlay
    maturity: experimental
    portability: portable
    related: security-review
    requires: none
    risk: local-write
name: powershell-scripting
---
# PowerShell scripting

If `overlay.md` exists beside this file, read it before acting; it contains
repository-specific bindings. This core remains usable without it.

Treat a PowerShell script as a program with several observable contracts, not as
a sequence of interactive shell commands. Its parameter binding, object output,
diagnostic streams, process exit status, filesystem effects, cleanup, and behavior
across supported hosts are all part of the interface.

## Establish the execution contract

Before editing, identify:

1. The supported PowerShell editions and versions, operating systems, and whether
   the script can relaunch under another host or integrity level.
2. Which inputs are trusted, which can contain spaces or metacharacters, and which
   can name local, remote, or attacker-controlled resources.
3. Which output is pipeline data, which is human-facing status, which is machine
   readable, and which exit codes callers depend on.
4. Which external capabilities are absent, available and successful, available
   but failed, or available but returned empty, malformed, or incomplete data.
   Keep these states distinct whenever their fallback policy differs.
5. The side effects, ownership of temporary resources, concurrency expectations,
   cancellation behavior, and cleanup obligations.
6. The narrowest executable check that can disprove the proposed behavior.

Read nearby scripts, tests, CI invocations, and call sites before choosing a new
pattern. Preserve an existing public parameter or output contract unless the task
explicitly changes it.

## Authoring workflow

1. Define an advanced-script parameter contract with explicit types, validation,
   parameter sets where useful, and comment-based help for user-facing entry points.
2. Keep pipeline data separate from status and diagnostics. Return objects rather
   than preformatted text when another command may consume the result.
3. Invoke commands structurally. Use PowerShell parameter splatting for cmdlets
   and argument arrays for native commands; do not turn data into executable code.
4. Handle both PowerShell failures and native process exit codes. Decide at each
   boundary whether to retry, translate, propagate, or terminate.
5. Make state changes bounded and reversible where possible. Pair acquisition with
   cleanup in `try`/`finally`, and make repeated or concurrent execution explicit.
6. Write structured files through structured serializers, choose encoding
   deliberately, and use atomic replacement when partial output would be harmful.
7. Validate the smallest supported host and platform matrix that exercises the
   changed behavior, then inspect the diff as a reviewer.

## Review priority

Review behavior before style. Lead with concrete findings in this order:

1. Injection, secret exposure, unsafe remote access, or privilege-boundary flaws.
2. Hangs, unbounded waits, leaked processes or handles, and cleanup failures.
3. Wrong exit status, swallowed errors, polluted pipeline output, or corrupt files.
4. Host, version, operating-system, path, locale, and encoding incompatibilities.
5. Race conditions, stale-file selection, non-idempotent behavior, and weak tests.
6. Readability and analyzer findings that do not already imply a behavioral defect.

Do not accept a parser pass or a clean PSScriptAnalyzer run as proof of runtime
correctness. Exercise native-command failure, unusual path values, cleanup paths,
and every claimed compatibility boundary.

## Baseline rules

- Prefer `[CmdletBinding()]`, explicit parameters, approved verbs, splatting, and
  implicit object output over ad hoc `$args`, positional ambiguity, or formatted
  strings in reusable code.
- Never use `Invoke-Expression` to assemble a command. The call operator does not
  parse a command line stored in one string; keep the executable and arguments as
  separate values.
- Treat success output, errors, warnings, verbose, debug, information, progress,
  and native stdout/stderr as distinct channels with different consumers.
- Check `$LASTEXITCODE` immediately after a native command when its status matters.
  `$ErrorActionPreference = 'Stop'` does not make nonzero native exits terminating
  in Windows PowerShell 5.1.
- Do not rely on ambient working directory, profile state, locale, encoding,
  module imports, or preference variables unless the contract declares them.
- Avoid textual rewrites of JSON, XML, CSV, command lines, and paths when a parser,
  serializer, argument array, or path API can preserve the structure.
- Budget every serialized surface independently. A bounded file does not prove a
   derived stdout or sidecar projection fits; measure the exact encoded payload and
   its fallback after wrappers, repeated identifiers, and diagnostics are added.
- Preserve why data is unavailable. Do not collapse dependency absence, nonzero
   exit, exception, malformed output, and valid empty output into one `$null` if
   downstream code must choose different fallbacks or warnings.
- Put cleanup in `finally`; make timeouts bounded; make lock ownership and stale
  artifact selection specific to the current operation.
- Never put secrets in source, logs, process arguments, manifests, or plaintext
  parameters. Prefer established credential and secret-store abstractions.

## Detail pages

Load only the page needed for the current task:

- [authoring.md](authoring.md) - script shape, parameters, help, streams, paths,
   structured files, cleanup, concurrency, and security boundaries.
- [native-processes.md](native-processes.md) - direct native invocation,
   `Start-Process`, argument boundaries, elevation, timeouts, and output capture.
- [review.md](review.md) - a behavior-first review procedure, high-signal search
   targets, and plausible patterns that commonly fail.
- [testing.md](testing.md) - parser, analyzer, Pester, subprocess, failure-path,
   compatibility, and security test matrices.
- [sources.md](sources.md) - the Microsoft, PSScriptAnalyzer, Pester, .NET, and
   community guidance distilled into this core.