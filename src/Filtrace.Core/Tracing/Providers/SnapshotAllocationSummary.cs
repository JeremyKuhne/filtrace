// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Allocation activity in a timeline snapshot.
/// </summary>
/// <param name="TickCount">Total positive allocation ticks in the window.</param>
/// <param name="Bytes">Sampled allocation bytes represented by those ticks.</param>
/// <param name="TypeCount">
///  Distinct allocation types retained for ranking; a lower bound when snapshot detail or names were truncated.
/// </param>
/// <param name="Types">Top retained allocation types by bytes, bounded by the snapshot detail limit.</param>
public sealed record SnapshotAllocationSummary(
    long TickCount,
    long Bytes,
    int TypeCount,
    IReadOnlyList<SnapshotAllocationType> Types);
