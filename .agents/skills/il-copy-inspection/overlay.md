---
core: il-copy-inspection
core-pin: v0.13.0
---

# IL copy inspection overlay

## Filtrace binding

- Build [filtrace.slnx](../../../filtrace.slnx) in Release before inspecting IL.
  Product assemblies and portable PDBs land under each project's
  `bin/Release/net10.0/` directory.
- Use this skill only to establish whether the C# compiler emitted a struct copy or
  boxing operation. Use [performance-testing](../performance-testing/SKILL.md) for
  timing/allocation cost and [filtrace](../filtrace/SKILL.md) for runtime hot-path
  attribution.
- Filtrace targets net10.0 only. The net481 HotLoopBench fixture is captured input,
  not a second product optimization target; do not apply .NET Framework JIT advice
  to product code.
