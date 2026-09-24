---
core: security-review
core-pin: v0.14.0
---

# Security review overlay

## Repository threat model

Filtrace is a local developer investigation tool, typically driven by a trusted
developer or their agent. It is not a multi-tenant service or an isolation boundary.
Unless the user explicitly requests an adversarial security assessment, treat trace
contents, CLI/MCP arguments, symbol paths, and capture manifests as **trusted inputs**.

Trusted does not mean perfect. Real investigation artifacts can be multi-gigabyte,
truncated, incomplete, captured with missing providers, transferred incorrectly,
produced by another tool version, or damaged by ordinary filesystem corruption.
Agents and developers can also make accidental path or option mistakes. Review for
those plausible failures without redesigning the product around malicious input.

An explicit user request to review hostile traces, exposed remote MCP access, or
another expanded threat model overrides this local default for that task only.

## Risk triage

Before raising a security finding, answer these questions in order:

1. Is the input adversarial in the actual supported workflow, or only theoretically
   attacker-controlled because it crosses an API boundary?
2. Can the behavior occur with a valid large trace, ordinary corruption, event loss,
   version skew, or an accidental developer/agent mistake?
3. Could it cause unsafe memory access, integer overflow, a hang, an opaque crash,
   destructive overwrite, unintended network/command execution, leaked privileged
   resources, or silently wrong attribution?
4. Would the proposed fix add per-event, per-frame, per-sample, or cardinality-scaled
   cost to legitimate multi-gigabyte analysis?

If the scenario requires a deliberately crafted malicious trace, has no plausible
trusted-artifact failure mode, and does not expose a concrete unsafe-memory or
privilege-boundary flaw, classify it as outside the repository threat model. Do not
turn it into production code, arbitrary input caps, randomized comparers, defensive
copies, or exhaustive abuse tests. A generic reviewer's severity label does not
override this threat model.

## In-scope review priorities

Prioritize:

- memory-safety and caller-validated API preconditions;
- checked arithmetic where real trace counts, lengths, or byte totals can overflow;
- termination and clear errors for truncated or structurally corrupt captures;
- event-loss and incomplete-provider diagnostics before presenting exact conclusions;
- PID, process-instance, IRP, time-window, and cache identity correctness;
- accidental overwrite of traces, ETLX caches, symbols, executables, or evidence;
- command, native-argv, elevation, and unintended remote-access boundaries;
- bounded waits plus cleanup of ETW sessions, child processes, locks, and temp files;
- regex timeouts and output/token budgets as investigation reliability and agent UX;
- clean exception-to-CLI/MCP mapping for plausible capture corruption.

Examples include stale PID/IRP reuse that misattributes work, lost ETW events that
silently undercount, a truncated trace that hangs or throws an opaque exception, or a
telemetry output path that overwrites an input trace or its cache.

## Out-of-scope by default

Do not harden for:

- precomputed hash collisions in trusted ETL fields;
- maliciously chosen frame, IRP, event-name, or payload cardinality whose only goal
  is denial of service;
- sandbox/path-traversal defenses for a trusted local operator when no sandbox is
  promised;
- arbitrary trace-size or frame-count limits that reject legitimate large captures;
- speculative malformed-input branches already rejected safely by TraceEvent or the
  operating system;
- hostile remote MCP clients when the server remains a local stdio tool.

Concrete unsafe-memory and privilege-boundary flaws remain in scope even if
exploitation would require crafted input; report and remediate them. Otherwise
describe such a concern, when relevant, as an unsupported adversarial scenario
rather than a vulnerability requiring a fix.

## Performance gate

Large traces are the normal workload. Before proposing a production change in a
trace-processing hot path:

- identify a plausible valid/corrupt trusted-input failure, not only a malicious one;
- estimate the added work and retained memory per event/frame/sample;
- prefer one-time boundary validation and provenance warnings over repeated checks;
- avoid lowering legitimate trace cardinality to satisfy a speculative safety bound;
- use the performance-testing workflow when cost could be material.

Correctness work that necessarily handles every event, such as clearing stale IRP
ownership on reuse, is in scope. Extra hashing, copying, prevalidation, or allocation
solely for adversarial resistance is not.

## Validation

When a parser or codec needs coverage-guided exploration, hand the target design to
[fuzz-testing](../fuzz-testing/SKILL.md). Its overlay records the missing harness
as a project prerequisite; do not claim fuzz coverage until that project exists and
the target has a committed seed corpus.

Place focused regressions in the owning test project and run every applicable
OS-neutral test locally; ETW-only behavior remains covered by the Windows CI leg.
Validation includes the full Release suite plus
[Test-McpServer.ps1](../../../tools/Test-McpServer.ps1), which exercises schema and
response budgets and a real JSON-RPC round trip.
