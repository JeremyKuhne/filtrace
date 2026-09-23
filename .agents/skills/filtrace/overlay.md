---
core: filtrace
core-pin: local
---

# Filtrace repository overlay

## Current source-build capture extension

This checkout adds a minimal built-in physical-disk recorder:

```pwsh
filtrace collect --launch <executable> --output <trace.etl> --profile diskio
filtrace report <trace.etl> --kind diskio --format json
```

This supersedes the shared guide's statement that an external recorder is always
required for disk I/O. The profile enables Process, Thread, DiskIO, DiskIOInit,
and DiskFileIO only. It omits sampled CPU, context switches, stacks, verbose
FileIO, and CLR events.

The trace and report remain machine-wide. Write the ETL to a different volume
when recorder writes must not contend with the workload volume, read warnings
first, and separate workload-path rows from recorder and unrelated system I/O.
Use an external recorder only when the question needs a broader provider set.
