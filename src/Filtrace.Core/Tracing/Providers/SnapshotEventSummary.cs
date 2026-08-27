// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Raw event activity in a timeline snapshot.
/// </summary>
/// <param name="EventCount">Total raw events in the window.</param>
/// <param name="TypeCount">
///  Distinct provider/event-name pairs retained for ranking; a lower bound when snapshot detail or names were truncated.
/// </param>
/// <param name="Types">Top retained event types, bounded by the snapshot detail limit.</param>
public sealed record SnapshotEventSummary(
    long EventCount,
    int TypeCount,
    IReadOnlyList<SnapshotEventType> Types);
