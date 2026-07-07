// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The garbage-collection report for a trace: the per-collection records plus
///  the aggregate counts and pause-time summary an agent reads to judge GC
///  pressure.
/// </summary>
/// <remarks>
///  <para>
///   Unlike the stack-source families (CPU, allocation), GC behavior is a series
///   of structured per-collection records rather than weighted call stacks, so it
///   does not flow through the folding aggregator; this is its own result shape.
///  </para>
/// </remarks>
/// <param name="GcCount">The total number of collections.</param>
/// <param name="Gen0Count">The number of generation-0 collections.</param>
/// <param name="Gen1Count">The number of generation-1 collections.</param>
/// <param name="Gen2Count">The number of generation-2 collections.</param>
/// <param name="InducedCount">The number of collections triggered explicitly (an induced / <c>GC.Collect</c> reason) - a common anti-pattern worth flagging.</param>
/// <param name="TotalPauseMs">The summed pause time across all collections, in milliseconds.</param>
/// <param name="MaxPauseMs">The longest single pause, in milliseconds.</param>
/// <param name="MeanPauseMs">The mean pause time, in milliseconds.</param>
/// <param name="PercentTimeInGc">The percentage of the captured window spent in GC pauses (total pause time over the trace duration) - the headline "is GC the problem" number.</param>
/// <param name="PeakHeapSizeMB">The largest post-collection heap size observed, in megabytes.</param>
/// <param name="TotalPromotedMB">The summed promoted bytes across all collections, in megabytes.</param>
/// <param name="Gcs">The per-collection records, in trace order.</param>
public sealed record GcStatsResult(
    int GcCount,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    int InducedCount,
    double TotalPauseMs,
    double MaxPauseMs,
    double MeanPauseMs,
    double PercentTimeInGc,
    double PeakHeapSizeMB,
    double TotalPromotedMB,
    IReadOnlyList<GcRecord> Gcs);
