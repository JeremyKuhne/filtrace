// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Contains merged pause intervals and aggregate pause durations for a snapshot.
    /// </summary>
    internal sealed record GcPauseAggregate(
        IReadOnlyDictionary<int, GcPauseInterval[]> IntervalsByProcessInstance,
        double TotalPauseMs,
        double MaxPauseMs);
}