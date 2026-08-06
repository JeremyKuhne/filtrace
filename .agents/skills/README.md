# Filtrace agent skills

Filtrace carries one tool-shipped local skill, one locally authored portable core,
and twelve portable cores vendored from the
[agent-skills commons](https://github.com/JeremyKuhne/agent-skills). Commons cores
are immutable mirrors carrying provenance metadata; repository paths and
conventions belong in each sibling `overlay.md`.

| Skill | Source | Pin | Local binding |
| --- | --- | --- | --- |
| [filtrace](filtrace/SKILL.md) | this repository / MCP package | local | Canonical trace-analysis workflow and packaged scripts; consumers may add an optional overlay. |
| [powershell-scripting](powershell-scripting/SKILL.md) | this repository / portable core | local | Binds cross-version guidance to filtrace's scripts and contract checks. |
| [manage-skills](manage-skills/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Finds, builds, semantically reviews, updates, and safely retires skills; delegates agent-file correctness to `agent-files-review`. |
| [agent-files-review](agent-files-review/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Runs filtrace's skill and documentation contracts. |
| [pre-pr-self-review](pre-pr-self-review/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Binds the repository's tests and all product/agent gates. |
| [create-pr](create-pr/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Uses filtrace's explicit publishing boundary. |
| [address-pr-feedback](address-pr-feedback/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Uses the same boundary for PR follow-up. |
| [engineering-baseline](engineering-baseline/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Audits filtrace's .NET repository baseline without replacing its existing scaffold. |
| [fuzz-testing](fuzz-testing/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Project-gated guidance for the untrusted manifest, metadata, and speedscope parsers. |
| [security-review](security-review/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Focuses on untrusted trace and event input. |
| [performance-testing](performance-testing/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Binds the product benchmark project, hands profiles to filtrace, and applies staged fail-fast investigation budgets. |
| [il-copy-inspection](il-copy-inspection/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Audits emitted struct copies in Release assemblies before runtime measurement. |
| [code-comprehension](code-comprehension/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Defers to filtrace's style rules and the analysis vocabulary. |
| [github-actions-cost-optimization](github-actions-cost-optimization/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.14.0` | Protects the required `ci` check and the Windows-only ETW leg. |

Use `powershell-scripting` for PowerShell implementation and behavior review.
Use `security-review` for the wider product threat model, and
`agent-files-review` for skill/frontmatter/link validation even when its validator
happens to be implemented in PowerShell.
Use `code-comprehension` for readability and cognitive load, and
`performance-testing` for runtime cost; use `github-actions-cost-optimization` for
CI spend, never for filtrace's own runtime performance. Use
`il-copy-inspection` for emitted struct copies and `engineering-baseline` only for
repository-wide engineering audits or scaffolding decisions.

## Applicability audit

The complete `agent-skills` v0.14.0 portfolio was reviewed on 2026-08-06. The
project-gated `fuzz-testing` core is vendored even though its harness is not built
yet: filtrace owns JSON manifest, metadata, and speedscope parsers over untrusted
input, so the project prerequisite is applicable future work rather than an
unrelated domain. `il-copy-inspection` supplies the compiler-emitted-copy layer
between the product benchmark project and runtime traces, while
`engineering-baseline` supplies the brownfield repository audit used to keep those
build, test, performance, and agent surfaces coherent.

The remaining commons cores are intentionally not vendored:

- `cswin32-com` and `cswin32-interop`: filtrace has no CsWin32 or COM surface;
- `dotnet-polyfills`, `framework-jit-optimization`, and
  `scratch-buffer-strategy`: the analyzer product targets net10.0 only, while the
  net481 benchmark fixture is captured input rather than a product target;
- `roslyn-analyzers`: this repository does not author diagnostic analyzers or code
  fixes.

The filtrace skill is complete as shipped. A consuming repository may add an
`overlay.md` beside `SKILL.md` for project paths, capture defaults, symbol locations,
or local safety policy without editing the packaged core. The overlay is optional
and is not shipped by this repository.

## Updating

Reinstall a commons core at a reviewed immutable release, preserving its overlay:

```pwsh
gh skill install JeremyKuhne/agent-skills skills/<name> --pin vX.Y.Z --agent github-copilot --scope project --force
```

Then update `core-pin` in its overlay, review the normal dependency diff, and run:

```pwsh
./tools/Test-AgentSkills.ps1 -VerifyUpstream -ReferenceValidation
./tools/Test-Docs.ps1
```

Pinned cores are deliberately skipped by `gh skill update --all`.
