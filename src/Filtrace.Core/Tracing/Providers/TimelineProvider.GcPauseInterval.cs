// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Describes one process GC pause interval in trace-relative milliseconds.
    /// </summary>
    /// <param name="ProcessInstanceIndex">The TraceEvent index that distinguishes reused process ids.</param>
    /// <param name="StartMs">The inclusive pause start in trace-relative milliseconds.</param>
    /// <param name="EndMs">The inclusive pause end in trace-relative milliseconds.</param>
    internal readonly record struct GcPauseInterval(int ProcessInstanceIndex, double StartMs, double EndMs)
    {
        /// <summary>
        ///  Tests whether a trace-relative timestamp lies within this interval's inclusive bounds.
        /// </summary>
        /// <param name="timestampMs">The timestamp to test, in milliseconds.</param>
        /// <returns><see langword="true"/> when the timestamp lies between the start and end.</returns>
        public bool Contains(double timestampMs) => timestampMs >= StartMs && timestampMs <= EndMs;

        /// <summary>
        ///  Computes this pause's duration within a requested window.
        /// </summary>
        /// <param name="windowStartMs">The inclusive window start in milliseconds.</param>
        /// <param name="windowEndMs">The inclusive window end in milliseconds.</param>
        /// <returns>The nonnegative overlap duration in milliseconds.</returns>
        public double OverlapMs(double windowStartMs, double windowEndMs) =>
            Math.Max(0.0, Math.Min(EndMs, windowEndMs) - Math.Max(StartMs, windowStartMs));
    }
}
