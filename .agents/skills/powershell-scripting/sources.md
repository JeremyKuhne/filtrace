# Public guidance reviewed

This skill distills the sources below; it does not replace their version-specific
details. Microsoft Learn pages are versioned, so select the oldest and newest
PowerShell views in the supported matrix before relying on a parameter or behavior.

## PowerShell language and runtime

- [about Parsing](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_parsing) and
  [about Quoting Rules](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_quoting_rules)
  explain PowerShell tokenization and quoting. They support keeping executable and
  argument values separate instead of assembling an expression string.
- [about Splatting](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_splatting)
  documents named and positional splatting, forwarding `$PSBoundParameters`, and
  the different treatment of nested arrays for PowerShell and native commands.
- [about Automatic Variables](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_automatic_variables)
  and [about Preference Variables](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_preference_variables)
  define `$LASTEXITCODE`, `$?`, `$ErrorActionPreference`, and newer native-command
  preferences. Their version differences justify explicit native status handling.
- [about Output Streams](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_output_streams)
  and [about Redirection](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_redirection)
  define the success, error, warning, verbose, debug, and information streams.
  Redirection of native bytes changed in PowerShell 7.4, and merged stderr/stdout
  remains text-oriented.
- [about Character Encoding](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_character_encoding)
  documents the inconsistent Windows PowerShell 5.1 defaults, PowerShell 7's
  UTF-8-no-BOM defaults, source-file BOM tradeoffs, and append behavior.
- [about Try, Catch, and Finally](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_try_catch_finally)
  and [about Trap](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_trap)
  define terminating-error handling and cleanup control flow. Lexical `finally`
  makes resource ownership easier to audit than broad script-scope traps.
- [about Requires](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_requires)
  defines pre-execution version, edition, module, and elevation requirements.

## Command interfaces and help

- [about Functions Advanced Parameters](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_functions_advanced_parameters)
  covers parameter sets, validation attributes, pipeline binding, and aliases.
- [about CmdletBinding](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_functions_cmdletbindingattribute)
  documents common parameters, positional binding, `SupportsShouldProcess`, and
  `ConfirmImpact`.
- [about Comment Based Help](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_comment_based_help)
  defines recognized placement and keywords for script and function help.
- [Everything about ShouldProcess](https://learn.microsoft.com/powershell/scripting/learn/deep-dives/everything-about-shouldprocess)
  emphasizes that support requires both the attribute and calls around the actual
  state-changing operations.
- [Approved verbs](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/approved-verbs-for-windows-powershell-commands)
  defines discoverable verb semantics for exported commands.

## Native processes and security

- [`Start-Process`](https://learn.microsoft.com/powershell/module/microsoft.powershell.management/start-process)
  documents `ArgumentList`, `PassThru`, `Wait`, `WorkingDirectory`, redirection,
  credentials, and `Verb`. Its argument-list guidance confirms that values are
  joined into one string rather than retained as an argv vector.
- [.NET ProcessStartInfo.ArgumentList](https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.argumentlist)
  provides a structural argument API on supported modern runtimes; it is not
  available in Windows PowerShell 5.1's .NET Framework runtime.
- [.NET Process.WaitForExit](https://learn.microsoft.com/dotnet/api/system.diagnostics.process.waitforexit)
  defines bounded and unbounded waits and the Boolean timeout result.
- [Avoiding script injection attacks](https://learn.microsoft.com/powershell/scripting/security/preventing-script-injection)
  recommends typed inputs, validation, and avoiding dynamic evaluation of
  user-provided strings.
- [about Execution Policies](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_execution_policies)
  states that execution policy is a safety feature rather than a security system.

## Analysis and tests

- [PSScriptAnalyzer overview](https://learn.microsoft.com/powershell/utility-modules/psscriptanalyzer/overview)
  describes configurable static analysis and custom settings.
- PSScriptAnalyzer rules for
  [approved verbs](https://github.com/PowerShell/PSScriptAnalyzer/blob/master/docs/Rules/UseApprovedVerbs.md),
  [`Write-Host`](https://github.com/PowerShell/PSScriptAnalyzer/blob/master/docs/Rules/AvoidUsingWriteHost.md),
  [`Invoke-Expression`](https://github.com/PowerShell/PSScriptAnalyzer/blob/master/docs/Rules/AvoidUsingInvokeExpression.md),
  [`ShouldProcess`](https://github.com/PowerShell/PSScriptAnalyzer/blob/master/docs/Rules/UseShouldProcessForStateChangingFunctions.md), and
  [plaintext password parameters](https://github.com/PowerShell/PSScriptAnalyzer/blob/master/docs/Rules/AvoidUsingPlainTextForPassword.md)
  supply useful heuristics. Analyzer compliance is not proof of runtime behavior,
  and `SecureString` alone is not a complete secret-management design.
- [Pester quick start](https://pester.dev/docs/quick-start),
  [mocking](https://pester.dev/docs/usage/mocking), and
  [`TestDrive`](https://pester.dev/docs/usage/testdrive)
  cover behavior tests, boundary substitution, and isolated temporary files.
- The community-maintained
  [PowerShell Practice and Style guide](https://poshcode.gitbook.io/powershell-practice-and-style/)
  is a useful readability supplement. Repository conventions and executable
  behavior take precedence where style guidance differs.

## How to use these sources in review

Use documentation to verify semantics, then reproduce the boundary in the actual
supported host. Prefer Microsoft and .NET documentation for runtime contracts,
PSScriptAnalyzer and Pester documentation for their tools, and community guidance
for nonbinding style choices. Record the PowerShell view/version when behavior has
changed across releases.