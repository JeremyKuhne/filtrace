// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Describes one process GC pause interval in trace-relative milliseconds.
    /// </summary>
    internal readonly record struct GcPauseInterval(int ProcessInstanceIndex, double StartMs, double EndMs)
    {
        public bool Contains(double timestampMs) => timestampMs >= StartMs && timestampMs <= EndMs;

        public double OverlapMs(double windowStartMs, double windowEndMs) =>
            Math.Max(0.0, Math.Min(EndMs, windowEndMs) - Math.Max(StartMs, windowStartMs));
    }
}