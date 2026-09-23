---
core: filtrace
core-pin: local
---

# Filtrace repository overlay

## Built-in physical-disk capture

The canonical guide documents the built-in minimal physical-disk recorder:

```pwsh
filtrace collect --launch <executable> --output <trace.etl> --profile diskio
filtrace report <trace.etl> --kind diskio --format json
```

The profile enables Process, Thread, DiskIO, DiskIOInit, and DiskFileIO only. It
omits sampled CPU, context switches, stacks, verbose FileIO, and CLR events.

The trace and report remain machine-wide. Write the ETL to a different volume
when recorder writes must not contend with the workload volume, read warnings
first, and separate workload-path rows from recorder and unrelated system I/O.
Use an external recorder only when the question needs a broader provider set.

For any captured command that depends on relative paths, pass
`--working-directory <path>` instead of changing the agent shell's own directory.
The capture result records the resolved absolute directory.
