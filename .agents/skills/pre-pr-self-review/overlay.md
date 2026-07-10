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