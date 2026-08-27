// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  CPU activity in a timeline snapshot.
/// </summary>
/// <param name="SampleCount">Total stack-bearing CPU samples in the window.</param>
/// <param name="MethodCount">
///  Distinct resolved leaf methods retained for ranking; a lower bound when snapshot detail or names were truncated.
/// </param>
/// <param name="Methods">Top retained resolved leaf methods, bounded by the snapshot detail limit.</param>
public sealed record SnapshotCpuSummary(
    long SampleCount,
    int MethodCount,
    IReadOnlyList<SnapshotCpuMethod> Methods);
