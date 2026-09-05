// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Bounded cross-lane evidence around one trace-relative timestamp.
/// </summary>
/// <param name="AtMs">The requested center timestamp, in milliseconds from trace start.</param>
/// <param name="Gc">Garbage collections in the resolved window.</param>
/// <param name="Cpu">Top CPU leaf methods in the resolved window.</param>
/// <param name="Exceptions">Top exception types in the resolved window.</param>
/// <param name="Alloc">Top allocation types in the resolved window.</param>
/// <param name="Jit">Top jitted methods in the resolved window.</param>
/// <param name="Events">Top raw event types in the resolved window.</param>
/// <param name="NamesTruncated">
///  Whether any emitted trace-derived name was length-bounded or escaped for
///  terminal-safe output.
/// </param>
public sealed record TimelineSnapshot(
    double AtMs,
    SnapshotGcSummary Gc,
    SnapshotCpuSummary Cpu,
    SnapshotExceptionSummary Exceptions,
    SnapshotAllocationSummary Alloc,
    SnapshotJitSummary Jit,
    SnapshotEventSummary Events,
    bool NamesTruncated)
{
    /// <summary>
    ///  Whether a bounded aggregation budget dropped named detail or GC interval
    ///  state. Aggregate event-family totals remain complete.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DetailTruncated { get; init; }

    /// <summary>
    ///  Whether GC suspend/restart evidence was incomplete or malformed, so GC pause
    ///  totals and overlap-based collection inclusion may be incomplete.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool GcPauseDataIncomplete { get; init; }

    /// <summary>
    ///  Whether an in-window EE restart lacked a retained suspension start, so its
    ///  reason and any associated pause contribution are unknown.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UnknownPauseDataIncomplete { get; init; }
}
