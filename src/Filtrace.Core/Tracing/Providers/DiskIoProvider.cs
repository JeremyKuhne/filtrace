// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The disk I/O provider: reads the Windows kernel's physical <c>DiskIO</c> read and
///  write events from an ETW trace into a <see cref="DiskIoResult"/>, aggregated by
///  file - so a data-heavy workload's real disk traffic (after the cache) is visible by
///  file and by bytes.
/// </summary>
/// <remarks>
///  <para>
///   Each <c>DiskIO/Read</c> and <c>DiskIO/Write</c> event carries the transfer size,
///   the disk service time, and the file the transfer hit. This provider tallies those
///   by file so the heaviest files rank first, answering "is my code really waiting on
///   the disk, and which files does it read or write?" - a question CPU sampling and the
///   logical file APIs cannot, since cached file access never reaches the disk.
///  </para>
///  <para>
///   Physical disk events are an ETW (kernel) capability, so this reads an <c>.etl</c>
///   captured with the <c>DiskIO</c> kernel keyword; the companion <c>DiskFileIO</c>
///   keyword supplies the file-name rundown that resolves each transfer to a file, and
///   without it the rows aggregate under <c>(unknown)</c>. An EventPipe <c>.nettrace</c>
///   carries no kernel disk events, so disk I/O is not available from it.
///  </para>
/// </remarks>
public sealed partial class DiskIoProvider
{
    // The JSON scaffolding around one file record - the property names, the punctuation,
    // and the numeric byte, count, and service-time fields - which the per-record
    // estimate adds to the file name.
    private const int RecordScaffoldTokens = 38;

    /// <summary>
    ///  Reads the disk I/O report from the ETW trace at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.etl</c> file path.</param>
    /// <returns>The disk I/O report, or an empty report when the trace carries no disk events.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public DiskIoResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        using EtlxTraceLog traceLog = TraceConverter.OpenTraceLog(fullPath, out _);

        Dictionary<string, FileTally> byFile = new(StringComparer.OrdinalIgnoreCase);
        int readCount = 0;
        int writeCount = 0;
        long totalReadBytes = 0;
        long totalWriteBytes = 0;
        double totalDiskMs = 0;

        foreach (TraceEvent data in traceLog.Events)
        {
            // DiskIOTraceData is the completion event for a physical read or write (the
            // separate *Init events, which carry no transfer size, are DiskIOInitTraceData
            // and are skipped here). The opcode name distinguishes the direction.
            if (data is not DiskIOTraceData disk)
            {
                continue;
            }

            bool isWrite = disk.OpcodeName.Equals("Write", StringComparison.OrdinalIgnoreCase);
            bool isRead = disk.OpcodeName.Equals("Read", StringComparison.OrdinalIgnoreCase);
            if (!isWrite && !isRead)
            {
                continue;
            }

            int size = disk.TransferSize;
            double ms = disk.ElapsedTimeMSec;
            string file = string.IsNullOrEmpty(disk.FileName) ? "(unknown)" : disk.FileName;

            ref FileTally tally = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                byFile, file, out _);

            tally.TotalDiskMs += ms;
            totalDiskMs += ms;

            if (isWrite)
            {
                tally.WriteBytes += size;
                tally.WriteCount++;
                totalWriteBytes += size;
                writeCount++;
            }
            else
            {
                tally.ReadBytes += size;
                tally.ReadCount++;
                totalReadBytes += size;
                readCount++;
            }
        }

        // Rank the files by disk service time, most first, with the file name as a stable
        // secondary order so the report is deterministic.
        List<DiskIoFileRecord> files =
        [
            .. byFile
                .Select(static pair => new DiskIoFileRecord(
                    pair.Key,
                    pair.Value.ReadBytes,
                    pair.Value.WriteBytes,
                    pair.Value.ReadCount,
                    pair.Value.WriteCount,
                    pair.Value.TotalDiskMs))
                .OrderByDescending(static record => record.TotalDiskMs)
                .ThenBy(static record => record.FileName, StringComparer.OrdinalIgnoreCase)
        ];

        return new DiskIoResult(
            readCount,
            writeCount,
            totalReadBytes,
            totalWriteBytes,
            totalDiskMs,
            files);
    }

    /// <summary>
    ///  Limits a report's per-file detail to the heaviest files that fit both
    ///  <paramref name="top"/> and <see cref="OutputBudget.DefaultRowBudgetTokens"/>,
    ///  leaving the aggregate summary untouched.
    /// </summary>
    /// <param name="report">The full report, as returned by <see cref="Read"/>.</param>
    /// <param name="top">
    ///  The caller's maximum detail row count. Must be non-negative; zero keeps the
    ///  aggregate summary and drops every file row.
    /// </param>
    /// <param name="warning">
    ///  The warning naming what was dropped, or <see langword="null"/> when the whole file
    ///  list was kept.
    /// </param>
    /// <returns>The limited report, or <paramref name="report"/> itself when every file fit.</returns>
    /// <remarks>
    ///  <para>
    ///   Shared by both heads so they bound and word the result identically. A file row
    ///   costs about 60 estimated tokens, so a caller-supplied row cap alone stops holding
    ///   the response under the ceiling at roughly 400 files - well within reach of a
    ///   machine-wide capture, though no committed fixture is broad enough to reach it.
    ///   <see cref="Read"/> already ranks the files by disk time, so the order is kept.
    ///  </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is negative.</exception>
    public static DiskIoResult LimitDetail(DiskIoResult report, int top, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfNegative(top);

        List<DiskIoFileRecord> kept = OutputBudget.TakeWithinBudget(
            report.Files.Take(top),
            EstimateRecordTokens,
            OutputBudget.DefaultRowBudgetTokens,
            out bool budgetTruncated);

        if (kept.Count == report.Files.Count)
        {
            warning = null;
            return report;
        }

        warning = budgetTruncated
            ? $"Showing {kept.Count} of {report.Files.Count} files by disk time; more would exceed the "
                + $"{OutputBudget.DefaultRowBudgetTokens}-token detail budget that holds the whole response under "
                + $"the {OutputBudget.DefaultCeilingTokens}-token ceiling. The aggregate summary still covers "
                + "every file."
            : top == 0
                ? $"Aggregate only: {report.Files.Count} files were not listed. Ask again with a positive top "
                    + "for the per-file detail."
                : $"Showing the top {top} of {report.Files.Count} files by disk time.";

        return report with { Files = kept };
    }

    private static int EstimateRecordTokens(DiskIoFileRecord file) =>
        RecordScaffoldTokens + OutputBudget.EstimateTokens(file.FileName);
}
