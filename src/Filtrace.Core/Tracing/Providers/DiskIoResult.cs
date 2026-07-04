// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The disk I/O attributed to one file in a <see cref="DiskIoResult"/>: how many
///  bytes the process read and wrote to it, the operation counts, and the total time
///  the physical disk spent servicing them.
/// </summary>
/// <param name="FileName">The file path the disk I/O resolved to (or a device path when a name was unavailable).</param>
/// <param name="ReadBytes">Total bytes read from the file.</param>
/// <param name="WriteBytes">Total bytes written to the file.</param>
/// <param name="ReadCount">Number of physical read operations.</param>
/// <param name="WriteCount">Number of physical write operations.</param>
/// <param name="TotalDiskMs">Total disk service time for the file's operations, in milliseconds.</param>
public sealed record DiskIoFileRecord(
    string FileName,
    long ReadBytes,
    long WriteBytes,
    int ReadCount,
    int WriteCount,
    double TotalDiskMs);

/// <summary>
///  The disk I/O report for an ETW trace: the physical disk reads and writes the
///  captured process issued, aggregated by file - the answer to "is my code really
///  waiting on the disk, and which files does it hit?"
/// </summary>
/// <remarks>
///  <para>
///   The Windows kernel's <c>DiskIO</c> events record each physical disk read and write
///   with its byte count, service time, and file - the actual device traffic, after the
///   file-system cache, so it reflects real disk pressure rather than logical file calls
///   that may be served from memory. This is an ETW (<c>.etl</c>) capability: EventPipe
///   carries no kernel disk events. Like the GC and JIT reports this is structured data,
///   not weighted stacks, so it returns its own result rather than a
///   <see cref="StackSampleSource"/>.
///  </para>
/// </remarks>
/// <param name="ReadCount">The total number of physical read operations.</param>
/// <param name="WriteCount">The total number of physical write operations.</param>
/// <param name="TotalReadBytes">The total bytes read from disk.</param>
/// <param name="TotalWriteBytes">The total bytes written to disk.</param>
/// <param name="TotalDiskMs">The total physical-disk service time, in milliseconds.</param>
/// <param name="Files">The per-file breakdown, most disk time first.</param>
public sealed record DiskIoResult(
    int ReadCount,
    int WriteCount,
    long TotalReadBytes,
    long TotalWriteBytes,
    double TotalDiskMs,
    IReadOnlyList<DiskIoFileRecord> Files);
