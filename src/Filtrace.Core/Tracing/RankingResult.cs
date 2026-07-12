// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A self-time or inclusive-time ranking over a scoped trace.
/// </summary>
/// <param name="ScopeWeight">Total scoped weight, in the metric's unit (milliseconds for CPU, bytes for allocations).</param>
/// <param name="RootFrame">The root frame the ranking was scoped to, or empty for the whole trace.</param>
/// <param name="Rows">The ranked frames, highest first.</param>
/// <param name="ContributingRecordCount">
///  Query records surviving all scopes, or <see langword="null"/> when the source
///  does not establish meaningful record semantics.
/// </param>
public sealed record RankingResult(
    double ScopeWeight,
    string RootFrame,
    IReadOnlyList<RankRow> Rows,
    int? ContributingRecordCount = null);
