# Native processes and elevation

Native programs do not follow PowerShell cmdlet error, parameter-binding, stream,
or cancellation semantics. Treat each invocation as an explicit adapter between
two interfaces.

## Prefer direct invocation

Use the call operator with the executable path and a real argument array when the
child can share the current console:

```powershell
[string[]] $arguments = @('build', '--project', $projectPath)
& $executablePath @arguments
[int] $nativeExitCode = $LASTEXITCODE

if ($nativeExitCode -ne 0) {
    throw "The build process exited with code $nativeExitCode."
}
```

This preserves PowerShell-side argument boundaries better than constructing one
command string and keeps output visible. Never write `& "$executable $arguments"`;
the call operator treats that as an executable name, not a command line. Avoid
`Invoke-Expression` and the stop-parsing token as general quoting solutions.

Check and save `$LASTEXITCODE` immediately after every native command whose status
matters. `$ErrorActionPreference = 'Stop'` does not turn a nonzero native exit into
a terminating error in Windows PowerShell 5.1. Newer PowerShell preferences can
change native-error behavior, so explicit checks remain the portable contract.

Validate output before parsing it, but validate the native exit first. A failed
command followed by `ConvertFrom-Json` otherwise reports a misleading JSON error
and loses the process failure.

Keep command discovery separate from command execution. "Executable not found"
may permit a documented fallback; "executable found but exited nonzero," threw,
or returned malformed/incomplete output is evidence that the attempted operation
failed. Do not map both to `$null` and accidentally apply the missing-tool fallback.
Return or retain enough state for the caller to distinguish absent, succeeded,
failed, and invalid-result outcomes.

## Know when `Start-Process` changes the contract

Use `Start-Process` only when the task needs a separate window, credentials,
elevation, shell association, detached lifetime, explicit working directory, or
file-based stream redirection. It is not a more reliable form of `&`.

Important differences:

- `Start-Process -ArgumentList` accepts an array but joins it into one command-line
  string. The target runtime reparses that string. Array elements are not a secure
  argv boundary.
- Quoting rules depend on the target executable and, on Windows, backslashes before
  a closing quote are significant. Test spaces, quotes, empty values, trailing
  backslashes, newlines, and metacharacters.
- Prefer `System.Diagnostics.ProcessStartInfo.ArgumentList` when the minimum .NET
  runtime supports it and shell execution is not required. It is unavailable in
  the .NET Framework runtime used by Windows PowerShell 5.1. For that host or
  `-Verb RunAs`, use a reviewed target-specific encoder or reject values the
  handoff cannot represent safely. Do not invent quoting with a simple `"$value"`.
- Always set `-WorkingDirectory` when relative paths or child output locations
  matter. An elevated process commonly starts somewhere other than the parent
  repository.
- Request `-PassThru` when the caller owns completion or exit status. A process
  object can still deny access to `HasExited`, `WaitForExit`, or `ExitCode` across
  an integrity boundary, so handle those failures explicitly.

Do not pass a secret in `ArgumentList`; process command lines are observable by
other tooling and often logged.

## Bound waits

Never let a user-facing helper wait forever without an explicit contract. Validate
the timeout as a positive bounded number and clamp milliseconds before calling the
`int` overload:

```powershell
[long] $requestedMilliseconds = [long]$timeoutSeconds * 1000
[int] $timeoutMilliseconds = [int][Math]::Min(
    $requestedMilliseconds,
    [int]::MaxValue)

[bool] $exited = $false
try {
    $exited = $process.WaitForExit($timeoutMilliseconds)
}
catch {
    Write-Warning "Unable to wait on the child process: $($_.Exception.Message)"
}
```

If the wait returns false or throws:

- Do not immediately call the parameterless `WaitForExit()` and become unbounded.
- Do not report success merely because the parent remained healthy.
- State whether the child may still be running and whether the parent can safely
  terminate it. Killing a process tree is platform-specific and can itself fail.
- Preserve the output contract. A JSON mode should emit its documented timeout
  object or return a documented nonzero status, not successful empty stdout.
- Preserve a diagnostic breadcrumb without corrupting machine stdout: a log path,
  warning/error stream message, or bounded diagnostic field. Treat paths that may
  not exist yet as informational rather than confirmed artifacts.
- Dispose the process handle in `finally` after all permitted status reads.

`Start-Process -Verb RunAs -Wait` has exhibited hangs in real automation. Prefer
`-PassThru` plus a bounded `WaitForExit` path when an elevated parent must monitor
the child. Test UAC cancellation, an inaccessible process handle, timeout, and an
exit code that cannot be read.

## Elevation is a protocol

A self-elevating script has a parent/child protocol. Design it explicitly:

1. Reject unsupported operating systems before calling Windows-only APIs.
2. Detect elevation without assuming PowerShell 7 automatic variables exist in
   Windows PowerShell 5.1.
3. Relaunch the current host executable rather than hardcoding `pwsh` or
   `powershell.exe`.
4. Add an internal child marker to prevent recursive relaunch; document the marker
   if users can discover it through `Get-Help`.
5. Forward only an allowlisted set of parameters, preserving which switches were
   actually bound. Validate values before crossing the command-line boundary.
6. Pass `-NoProfile`, an explicit script path, an explicit working directory, and
   a unique operation identity. Do not use policy bypass as a routine handoff.
7. Keep build, restore, downloads, and output preparation in the unprivileged
   parent. The child should perform only the privileged operation.
8. Define status transport: visible console output, a unique bounded log, an
   atomic result file, process exit code, or a combination. Never tail a shared
   stale log.
9. Handle cancellation, timeout, access denied, child failure, and parent cleanup.

On Windows, this administrator check works in both Windows PowerShell 5.1 and
PowerShell 7 and avoids newer automatic variables:

```powershell
function Test-IsAdministrator {
  if ([System.Environment]::OSVersion.Platform -ne
    [System.PlatformID]::Win32NT) {
    return $false
  }

  [System.Security.Principal.WindowsIdentity] $identity =
    [System.Security.Principal.WindowsIdentity]::GetCurrent()
  try {
    [System.Security.Principal.WindowsPrincipal] $principal =
      [System.Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
      [System.Security.Principal.WindowsBuiltInRole]::Administrator)
  }
  finally {
    $identity.Dispose()
  }
}
```

For a supported console host, discover rather than guess its executable:

```powershell
[string] $currentHostPath = (Get-Process -Id $PID).Path
[string] $currentHostName =
  [System.IO.Path]::GetFileNameWithoutExtension($currentHostPath)

if ($currentHostName -notin @('powershell', 'pwsh')) {
  throw "The current host '$currentHostName' cannot perform this relaunch."
}
```

An embedded host or the Windows PowerShell ISE may not accept console-host
arguments such as `-NoProfile` and `-File`; reject it or require an explicit
supported host instead of relaunching an arbitrary process image.

An `-ElevatedChild` switch is not protection by itself. Treat values in its branch
as untrusted unless the parent-child handoff is authenticated by ownership and a
unique operation-specific resource.

## Streams and logging

Native stdout and stderr are not the same as PowerShell success and error records.
Choose among these behaviors deliberately:

- Leave streams attached for live console output and inspect `$LASTEXITCODE`.
- Merge `2>&1` only when loss of stream identity is acceptable. Once merged, binary
  fidelity and error classification may change.
- Use `Tee-Object` only when its host-specific text encoding and object formatting
  are acceptable. A visible elevated workflow often needs live output plus a log,
  so test both rather than redirecting to an apparently idle window.
- For deterministic text logs, define the encoding and line representation. For
  binary output, use a byte API or the native tool's output-file option.
- With `ProcessStartInfo` redirected streams, consume stdout and stderr
  concurrently. Reading one to completion while the child fills the other can
  deadlock on full buffers.

Do not parse formatted display text when the tool offers JSON or another stable
machine format. If the tool's schema varies by version, normalize unknown or
missing values conservatively and retain the original diagnostic context.

## Cross-version checks

At minimum, verify:

- Direct argument boundaries under every supported PowerShell host.
- The native argument-passing preference modes that supported PowerShell 7
  versions expose, when the repository changes them.
- Windows PowerShell 5.1 behavior without `$IsWindows`, modern encoding defaults,
  or `ProcessStartInfo.ArgumentList`.
- Windows elevation and working directory, plus the non-Windows rejection path.
- Native success, nonzero exit, launch failure, malformed output, timeout, and
  cleanup after each failure.