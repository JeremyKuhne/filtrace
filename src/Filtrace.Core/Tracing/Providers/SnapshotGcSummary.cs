// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Garbage-collection activity in a timeline snapshot.
/// </summary>
/// <param name="CollectionCount">Collections that started or had a managed-thread pause in the window.</param>
/// <param name="TotalPauseMs">Summed managed-thread pause overlap with the window, in milliseconds.</param>
/// <param name="MaxPauseMs">Longest merged per-process pause overlap with the window, in milliseconds.</param>
/// <param name="Collections">Longest collections, bounded by the snapshot detail limit.</param>
public sealed record SnapshotGcSummary(
    int CollectionCount,
    double TotalPauseMs,
    double MaxPauseMs,
    IReadOnlyList<SnapshotGcRecord> Collections);
