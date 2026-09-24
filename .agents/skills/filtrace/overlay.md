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

The capture and unscoped report remain machine-wide. Write the ETL to a different
volume when recorder writes must not contend with the workload volume. Scope a
report to direct issuers with `--process` or `--pid`, and to completion time with
`--time`; read the direct-issuer warning before attributing totals. Use an
external recorder only when the question needs a broader provider set.

For any captured command that depends on relative paths, pass
`--working-directory <path>` instead of changing the agent shell's own directory.
The capture result records the resolved absolute directory.

When a launched command delegates sampled work to an already-running managed
server, add `--rundown`. The collector appends a separately buffered minimal CLR
naming rundown after the command exits, limits quiescence polling to 30 seconds,
and then merges it into the ETL; the merge can extend total duration beyond that
polling bound. Use this only when the server remains alive. Rundown is
machine-wide by default; when prior evidence identifies the exact servers, pass
`--rundown-pid <id>[,<id>]` (up to 8 ids) to filter the naming events and
retain those ids in capture provenance. It requests 512 MB of ETW buffers,
permits roughly 641 MiB through TraceEvent's derived maximum-buffer count, and
can add hundreds of megabytes. It is invalid with `--profile diskio` or
`--max-size-mb`. Require zero lost events before trusting the recovered names.
