// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal abstract partial class TraceLogReader
{
    private const int MaximumPreallocatedSamples = 2_000_000;
    private static readonly Guid s_sampleProfilerProviderGuid = new(
        unchecked((int)0x3c530d44), unchecked((short)0x97ae), unchecked((short)0x513a),
        0x1e, 0x6d, 0x78, 0x3e, 0x8f, 0x8e, 0x03, 0xa9);
    private static readonly Guid s_perfInfoTaskGuid = new(
        unchecked((int)0xce1dbfb4), unchecked((short)0x137e), unchecked((short)0x4da6),
        0x87, 0xb0, 0x3f, 0x59, 0xaa, 0x10, 0x2c, 0xbc);

    /// <summary>
    ///  Gets a bounded exact-stack-count capacity for an unfiltered CPU read.
    /// </summary>
    /// <param name="traceLog">The trace whose persisted event statistics provide the capacity.</param>
    /// <param name="format">The raw trace format that selects EventPipe or ETW sample identity.</param>
    /// <param name="includesAllProcesses">Whether process scoping includes every trace process.</param>
    /// <param name="activityScoped">Whether an activity filter can drop samples.</param>
    /// <param name="timeScoped">Whether a time window can drop samples.</param>
    /// <returns>
    ///  The bounded initial capacity, or zero when a scope can make the persisted count an overestimate.
    /// </returns>
    internal static int GetInitialSampleCapacity(
        EtlxTraceLog traceLog,
        TraceFormat format,
        bool includesAllProcesses,
        bool activityScoped,
        bool timeScoped)
    {
        if (!includesAllProcesses || activityScoped || timeScoped)
        {
            return 0;
        }

        foreach (TraceEventCounts counts in traceLog.Stats)
        {
            bool isCpuSample = format switch
            {
                TraceFormat.NetTrace =>
                    !counts.IsClassic
                        && counts.ProviderGuid == s_sampleProfilerProviderGuid
                        && counts.EventID == 0,
                TraceFormat.Etl =>
                    counts.IsClassic
                        && counts.TaskGuid == s_perfInfoTaskGuid
                        && counts.Opcode == (TraceEventOpcode)46,
                _ => false
            };

            if (isCpuSample)
            {
                return BoundInitialSampleCapacity(
                    counts.StackCount,
                    counts.Count,
                    traceLog.EventCount);
            }
        }

        return 0;
    }

    /// <summary>
    ///  Bounds an ETLX-derived sample count before allocating the retained sample list.
    /// </summary>
    /// <param name="sampleCount">The persisted count of sample events with stacks.</param>
    /// <param name="eventTypeCount">The persisted total count for the sample event type.</param>
    /// <param name="eventCount">The total number of persisted events in the trace.</param>
    /// <returns>The count clamped to the retained-list preallocation limit.</returns>
    internal static int BoundInitialSampleCapacity(
        int sampleCount,
        int eventTypeCount,
        int eventCount)
    {
        if (sampleCount < 0 || sampleCount > eventTypeCount || sampleCount > eventCount)
        {
            return 0;
        }

        return Math.Min(sampleCount, MaximumPreallocatedSamples);
    }
}