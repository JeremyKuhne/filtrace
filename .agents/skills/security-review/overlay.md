---
core: security-review
core-pin: v0.13.0
---

# Security review overlay

## Untrusted-input surface

Treat these filtrace inputs as attacker-controlled:

- `.speedscope.json`, `.nettrace`, `.etl`, and generated ETLX cache contents;
- raw event names, payload filters, process/thread identifiers, roots, and regex
  fold patterns;
- MCP and CLI paths, output paths, time windows, row/page limits, and symbol paths.

Prioritize malformed-structure behavior, checked length/count arithmetic,
path/output safety, regex timeouts, bounded allocations, response token budgets,
and clean exception-to-CLI/MCP error mapping. Place focused regressions in the
owning test project and run every applicable OS-neutral test locally; ETW-only
behavior remains covered by the Windows CI leg.

When a parser or codec needs coverage-guided exploration, hand the target design to
[fuzz-testing](../fuzz-testing/SKILL.md). Its overlay records the missing harness
as a project prerequisite; do not claim fuzz coverage until that project exists and
the target has a committed seed corpus.

Validation includes the full Release suite plus
[Test-McpServer.ps1](../../../tools/Test-McpServer.ps1), which exercises schema and
response budgets and a real JSON-RPC round trip.