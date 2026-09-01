// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Contains merged pause intervals and aggregate pause durations for a snapshot.
    /// </summary>
    /// <param name="IntervalsByProcessInstance">Merged intervals keyed by TraceEvent process-instance index.</param>
    /// <param name="TotalPauseMs">The sum of each process instance's pause union within the window.</param>
    /// <param name="MaxPauseMs">The longest merged pause interval in the window.</param>
    internal sealed record GcPauseAggregate(
        IReadOnlyDictionary<int, GcPauseInterval[]> IntervalsByProcessInstance,
        double TotalPauseMs,
        double MaxPauseMs);
}
