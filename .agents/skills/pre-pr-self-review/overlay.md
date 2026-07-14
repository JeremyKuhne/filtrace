---
core: pre-pr-self-review
core-pin: v0.10.0
---

# Pre-PR self-review overlay

## Required filtrace gates

Run the read-only review pass with the host's code-review or exploration persona
(VS Code: `Explore`), then run:

```pwsh
dotnet test filtrace.slnx -c Release --no-restore
./tools/Test-CliHelp.ps1 -Configuration Release
./tools/Test-McpServer.ps1 -Configuration Release
./tools/Test-Docs.ps1
./eval/Invoke-Eval.ps1 -Configuration Release
./tools/Test-AgentSkills.ps1 -VerifyUpstream -ReferenceValidation
git diff --check
```

Filtrace itself targets net10.0 only. It reads net481 traces, but that does not make
normal production changes multi-targeted. Apply the core's Framework/polyfill items
only when changing `fixtures/HotLoopBench` net481 capture code or an actual
downlevel contract.

Changes to trace readers, event queries, regular expressions, output limits, or
other caller-supplied input also invoke the security-review skill. Changes under
`.agents/` additionally invoke agent-files-review.

When compacting MCP tool or parameter descriptions, compare every changed
description with its signature and XML docs. Defaults, optionality, supported
formats, and side effects must remain explicit; reclaiming schema tokens cannot
change the public contract implied to an agent.

Any changed `.ps1`, `.psm1`, or `.psd1` also invokes the
[powershell-scripting skill](../powershell-scripting/SKILL.md). Read its overlay
and the relevant review/testing pages before the agentic review pass. For every
changed native-tool or optional-capability boundary, pin the absent, success,
valid-empty, nonzero/exception, and malformed/incomplete states; do not approve a
fallback represented only by a shared `$null` sentinel. Put the applicable
boundary rows, expected policy, and test/manual-gap evidence in working notes
before the read-only review pass.