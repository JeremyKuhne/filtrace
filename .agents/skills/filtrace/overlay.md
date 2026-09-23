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

When a launched command delegates sampled work to an already-running managed
server, add `--rundown`. The collector appends a separately buffered minimal CLR
naming rundown after the command exits, limits quiescence polling to 30 seconds,
and then merges it into the ETL; the merge can extend total duration beyond that
polling bound. Use this only when the server remains alive; it is machine-wide,
can add hundreds of megabytes, and is invalid with `--profile diskio` or
`--max-size-mb`. Require zero lost events before trusting the recovered names.
