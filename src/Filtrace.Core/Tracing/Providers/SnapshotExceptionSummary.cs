// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Exception activity in a timeline snapshot.
/// </summary>
/// <param name="ExceptionCount">Total exception throws in the window.</param>
/// <param name="TypeCount">
///  Distinct exception types retained for ranking; a lower bound when snapshot detail or names were truncated.
/// </param>
/// <param name="Types">Top retained exception types, bounded by the snapshot detail limit.</param>
public sealed record SnapshotExceptionSummary(
    long ExceptionCount,
    int TypeCount,
    IReadOnlyList<SnapshotCountRow> Types);
