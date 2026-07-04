// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

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
///   captured with the <c>DiskIO</c> kernel keyword. An EventPipe <c>.nettrace</c>
///   carries no kernel disk events, so disk I/O is not available from it.
///  </para>
/// </remarks>
public sealed class DiskIoProvider
{
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

        using TraceLog traceLog = TraceLog.OpenOrConvert(
            fullPath,
            new TraceLogOptions { ContinueOnError = true });

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

    // A mutable per-file accumulator used only while tallying; the immutable
    // DiskIoFileRecord is built from it at the end.
    private struct FileTally
    {
        public long ReadBytes;
        public long WriteBytes;
        public int ReadCount;
        public int WriteCount;
        public double TotalDiskMs;
    }
}
