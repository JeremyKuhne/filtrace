// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class DiskIoProvider
{
    /// <summary>
    ///  Accumulates read, write, and service-time totals for one file before the immutable report is built.
    /// </summary>
    private struct FileTally
    {
        /// <summary>
        ///  The total bytes read from the file.
        /// </summary>
        public long ReadBytes;

        /// <summary>
        ///  The total bytes written to the file.
        /// </summary>
        public long WriteBytes;

        /// <summary>
        ///  The number of completed read operations.
        /// </summary>
        public int ReadCount;

        /// <summary>
        ///  The number of completed write operations.
        /// </summary>
        public int WriteCount;

        /// <summary>
        ///  The aggregate disk service time in milliseconds.
        /// </summary>
        public double TotalDiskMs;
    }
}
