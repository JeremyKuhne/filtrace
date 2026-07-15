---
core: powershell-scripting
core-pin: local
---

# PowerShell scripting overlay

## Filtrace binding

- Support both Windows PowerShell 5.1 and PowerShell 7 syntax for Windows-facing
  capture and elevation helpers. Keep cross-platform scripts valid under
  PowerShell 7 on Windows and Linux; isolate ETW and elevation behavior to Windows.
- Preserve `$ErrorActionPreference = 'Stop'` and explicit native exit-code checks.
  An intended `exit <code>` after `Write-Error` must keep the write nonterminating,
  because the repository preference otherwise makes the `exit` unreachable.
- Use UTF-8 without a BOM for deterministic JSON and metadata files. Prefer
  `[System.IO.File]::WriteAllText()` with an explicit `UTF8Encoding($false)` where
  the Windows PowerShell 5.1 and PowerShell 7 cmdlet defaults differ.
- Keep user-visible progress in the active console while retaining diagnostic
  logs. Do not redirect an elevated capture into an apparently idle window.

## Script ownership

| Surface | Role |
| --- | --- |
| [Capture-BenchmarkTrace.ps1](../filtrace/scripts/Capture-BenchmarkTrace.ps1) | BenchmarkDotNet EventPipe/ETW capture, isolated run artifacts, manifest, and analysis handoff |
| [Capture-ProjectTrace.ps1](../filtrace/scripts/Capture-ProjectTrace.ps1) | Whole-project EventPipe/ETW capture and metadata sidecar |
| [Open-SpeedscopeTrace.ps1](../filtrace/scripts/Open-SpeedscopeTrace.ps1) | Loopback handoff to speedscope.app |
| [Open-PerfettoTrace.ps1](../filtrace/scripts/Open-PerfettoTrace.ps1) | Loopback handoff to the Perfetto UI |
| [Invoke-ElevatedTests.ps1](../../../tools/Invoke-ElevatedTests.ps1) | Developer-local elevated test runner |
| [Test-CaptureBenchmarkTrace.ps1](../../../tools/Test-CaptureBenchmarkTrace.ps1) | Fake-driven CI contract for benchmark capture |
| [make-fixtures.ps1](../../../fixtures/make-fixtures.ps1) | EventPipe fixture generation |
| [capture-etw.ps1](../../../fixtures/capture-etw.ps1) | Elevated ETW fixture capture |
| [capture-diskio.ps1](../../../fixtures/capture-diskio.ps1) | Elevated disk-I/O fixture capture |

Repository scripts also live under `eval/` and `tools/`. Review them with the
same core rules even when they are not shipped in the filtrace skill.

## Local invariants

- Benchmark runs belong under
  `BenchmarkDotNet.Artifacts/filtrace-runs/<RunId>/`. Reject a reused nonempty run
  directory so stale captures cannot enter the current manifest.
- The capture lock key is `(project, target framework)`, with lock files under the
  project `obj/filtrace-capture-locks/` directory. Hold the file handle for the
  protected lifetime; sibling projects in one directory must remain independent.
- Pair raw traces, speedscope files, benchmark identities, and sidecars by a stable
  shared identity. Do not pair independently sorted arrays or select the globally
  newest historical file. Prefer a paired `.nettrace` over its speedscope export
  because the raw trace retains allocation, exception, and line events.
- Metadata sidecars use `<trace>.filtrace.json`. Missing, unsupported, or
  provider-dependent capture capability is `unknown`, not `disabled`.
- Keep filtrace dependency states distinct: when filtrace is absent, recorder facts
  may supply the documented fallback; when `filtrace info` is present but exits
  nonzero, throws, or returns no analyses, mark every selector unknown, suppress
  commands, and retain an explicit warning in the manifest and handoff.
- Durable manifest JSON has a 16 MiB safety ceiling so the complete bounded case set
  remains available to `batch` / `diff`; only the stdout JSON handoff owns the 20 KiB
  agent-context budget, measured as exact UTF-8 bytes. Test a manifest above 20 KiB
  while the measured handoff fallback stays below its budget and routes the caller
  to that complete manifest.
- Reject remote/UNC symbol candidates before passing them to analysis commands.
  Resolve accepted candidates through `[System.IO.Path]::GetFullPath()`.
- ETW self-elevation uses the current PowerShell host, an explicit repository
  working directory, a positive bounded timeout, and no `-Wait`. Build/restore
  remains non-elevated; elevated test commands use both `--no-build` and
  `--no-restore`.
- Treat elevation arguments as an injection boundary. Values forwarded through
  `Start-Process -ArgumentList` must use the reviewed encoder or reject quotes,
  line breaks, and ambiguous trailing backslashes; spaces alone are not the full
  adversarial case.
- A machine-readable capture mode emits valid JSON for every zero-exit outcome,
  including timeout or partial status. Exercise success and timeout across Text,
  Json, and Quiet modes; human progress stays off stdout.
- Loopback viewers stream captures rather than loading them fully, validate port
  and timeout ranges, preserve bind exceptions, and handle browser OPTIONS/CORS/
  Private Network Access behavior.

## Review history distilled

These repository reviews are the evidence base for the local binding:

| Reviews | Lessons retained |
| --- | --- |
| [#4](https://github.com/JeremyKuhne/filtrace/pull/4), [#9](https://github.com/JeremyKuhne/filtrace/pull/9), [#11](https://github.com/JeremyKuhne/filtrace/pull/11) | Keep prose aligned with enforced scope; preserve baselines on unsupported platforms; make JSON encoding explicit; test path-containment edge cases. |
| [#15](https://github.com/JeremyKuhne/filtrace/pull/15), [#16](https://github.com/JeremyKuhne/filtrace/pull/16), [#17](https://github.com/JeremyKuhne/filtrace/pull/17) | Match per-command contracts; use literal masking; count exact clipped payloads; make temp names unique; clean in `finally`; pair comparable runs by complete identity; fail closed on corrupt or missing inputs. |
| [#18](https://github.com/JeremyKuhne/filtrace/pull/18) | Preserve argv across elevation, check native exit before JSON parse, support both hosts, reject unsupported OS early, preserve explicit exit codes, working directory, and non-elevated artifact ownership. |
| [#22](https://github.com/JeremyKuhne/filtrace/pull/22), [#23](https://github.com/JeremyKuhne/filtrace/pull/23) | Exclude stale traces, bound elevated waits, handle inaccessible process objects, validate numeric inputs, stream large files, and implement the real loopback browser protocol. |
| [#40](https://github.com/JeremyKuhne/filtrace/pull/40), [#46](https://github.com/JeremyKuhne/filtrace/pull/46) | Prevent repository-link escape and represent provider-dependent capture capability as unknown. |
| [#48](https://github.com/JeremyKuhne/filtrace/pull/48), [#49](https://github.com/JeremyKuhne/filtrace/pull/49) | Use exact lock and pairing identities; isolate runs; document internal switches; normalize missing, empty, and unexpected status; distinguish dependency absence from attempted failure; keep timeout output structured in every mode; and budget the manifest and derived JSON handoff independently. |

## Repository checks

Run the narrow contract for the script first. The benchmark capture harness injects
fake `dotnet` and `filtrace` commands; extend it for stale selection, run-ID reuse,
overlap, sibling projects, identity pairing, remote-path rejection, exact symbol
candidates, independent manifest/handoff budgets, bounded fallback shape,
mode-by-outcome behavior, dependency absent/success/nonzero/malformed/incomplete
states, and speedscope-only fallback.

Keep the applicable boundary matrix in transient session/task notes rather than a
new repository file unless requested. Before proposing a push, summarize the
boundary rows, expected policy, and evidence or manual gap for each recorded cell
in the user update.

Then run the applicable repository gates:

```pwsh
./tools/Test-CaptureBenchmarkTrace.ps1
./tools/Test-CliHelp.ps1 -Configuration Release
./tools/Test-McpServer.ps1 -Configuration Release
./tools/Test-Docs.ps1
./tools/Test-AgentSkills.ps1 -ReferenceValidation
```

Parse every changed script under both installed Windows hosts. On non-Windows,
run the PowerShell 7 parser and the platform-relevant behavior checks. Elevated
ETW checks are manual Windows gates; do not imply CI covered them when it only ran
the fake-driven contract. In particular, a fake `Start-Process` timeout pins the
parent's JSON/text contract, not UAC, elevated-handle access, or child cleanup.