// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    internal sealed partial class SnapshotGcCollector
    {
        /// <summary>
        ///  Identifies one pending GC pause within a process, thread, and CLR instance.
        /// </summary>
        /// <param name="ProcessInstanceIndex">The TraceEvent process-instance index.</param>
        /// <param name="ThreadInstanceIndex">The TraceEvent thread-instance index.</param>
        /// <param name="ClrInstanceId">The CLR instance id.</param>
        private readonly record struct GcPauseIdentity(
            int ProcessInstanceIndex,
            int ThreadInstanceIndex,
            int ClrInstanceId);
    }
}
