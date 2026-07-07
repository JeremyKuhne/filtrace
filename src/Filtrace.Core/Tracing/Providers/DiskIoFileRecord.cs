// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The disk I/O attributed to one file in a <see cref="DiskIoResult"/>: how many
///  bytes were read from and written to it, the operation counts, and the total time
///  the physical disk spent servicing them.
/// </summary>
/// <param name="FileName">The file the disk I/O resolved to, or <c>(unknown)</c> when the event carried no file name.</param>
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
