// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  JIT activity in a timeline snapshot.
/// </summary>
/// <param name="CompilationCount">Total method-jitting-started events in the window.</param>
/// <param name="MethodCount">
///  Distinct method names retained for ranking; a lower bound when snapshot detail or names were truncated.
/// </param>
/// <param name="Methods">Top retained method names, bounded by the snapshot detail limit.</param>
public sealed record SnapshotJitSummary(
    long CompilationCount,
    int MethodCount,
    IReadOnlyList<SnapshotCountRow> Methods);
