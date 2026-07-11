# Filtrace agent skills

Filtrace carries one tool-shipped local skill and seven portable cores vendored
from the [agent-skills commons](https://github.com/JeremyKuhne/agent-skills).
Commons cores are immutable mirrors carrying provenance metadata; repository paths
and conventions belong in each sibling `overlay.md`. Three no-HTML-entity fixes
are recorded as pending-upstream divergences until the next reviewed commons
release.

| Skill | Source | Pin | Local binding |
| --- | --- | --- | --- |
| [filtrace](filtrace/SKILL.md) | this repository / MCP package | local | Canonical trace-analysis workflow and packaged scripts. |
| [manage-skills](manage-skills/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.10.0` | Distinguishes commons cores from the tool-shipped skill. |
| [agent-files-review](agent-files-review/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.10.0` | Runs filtrace's skill and documentation contracts. |
| [pre-pr-self-review](pre-pr-self-review/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.10.0` | Binds the repository's tests and all product/agent gates. |
| [create-pr](create-pr/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.10.0` | Uses filtrace's explicit publishing boundary. |
| [address-pr-feedback](address-pr-feedback/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.10.0` | Uses the same boundary for PR follow-up. |
| [security-review](security-review/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.10.0` | Focuses on untrusted trace and event input. |
| [performance-testing](performance-testing/SKILL.md) | `JeremyKuhne/agent-skills` | `v0.10.0` | Binds the manual HotLoopBench fixture generator and hands traces to filtrace. |

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