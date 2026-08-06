---
core: fuzz-testing
core-pin: v0.14.0
---

# Fuzz testing overlay

## Filtrace binding

- Filtrace does not yet have a fuzz project. Creating a standalone net10.0 harness
  is a prerequisite before this skill can run; normal `dotnet test` must not execute
  it.
- Initial target candidates are
  [CaptureManifestReader](../../../src/Filtrace.Core/Tracing/CaptureManifestReader.cs),
  [CaptureMetadataReader](../../../src/Filtrace.Core/Tracing/CaptureMetadataReader.cs),
  and the speedscope path through
  [TraceLoader](../../../src/Filtrace.Core/Tracing/TraceLoader.cs). Prefer public
  entry points; never widen production accessibility only for the harness.
- Seed corpora and minimized, reproduced crashes belong under the future harness.
  Promote every genuine crash into the owning MSTest project as a deterministic
  regression.
- Pair target selection and crash triage with
  [security-review](../security-review/SKILL.md). Binary TraceEvent parsing is
  dependency-owned; start with filtrace's own JSON validation and normalization.
