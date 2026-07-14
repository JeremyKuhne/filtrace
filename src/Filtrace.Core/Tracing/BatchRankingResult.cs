// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>One ranking query summarized across every case in a capture manifest.</summary>
/// <param name="ManifestPath">Canonical manifest path.</param>
/// <param name="Metric">Requested metric selector.</param>
/// <param name="Measure"><c>self</c> or <c>inclusive</c>.</param>
/// <param name="RootFrame">Optional root selector applied to every case.</param>
/// <param name="Cases">Case-keyed ranking summaries.</param>
public sealed record BatchRankingResult(
    string ManifestPath,
    string Metric,
    string Measure,
    string RootFrame,
    IReadOnlyList<BatchRankingCaseResult> Cases);

/// <summary>One manifest case's compact ranking summary.</summary>
/// <param name="Benchmark">Exact benchmark name, or the case id when unresolved.</param>
/// <param name="Parameters">Stable parameter display.</param>
/// <param name="TracePath">Trace path for a detailed follow-up query.</param>
/// <param name="ScopeWeight">Total scoped metric weight.</param>
/// <param name="Unit">Metric weight unit.</param>
/// <param name="TopFrame">Hottest frame, or <see langword="null"/> when empty.</param>
/// <param name="TopWeight">Hottest-frame weight.</param>
/// <param name="TopPercentOfScope">Hottest-frame share of scope.</param>
/// <param name="ContributingRecordCount">Contributing records, or <see langword="null"/>.</param>
/// <param name="Warnings">Case-specific load and quality diagnostics.</param>
public sealed record BatchRankingCaseResult(
    string Benchmark,
    string Parameters,
    string TracePath,
    double ScopeWeight,
    string Unit,
    string? TopFrame,
    double TopWeight,
    double TopPercentOfScope,
    int? ContributingRecordCount,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Operation unit, or <see langword="null"/> when metadata is incomplete.</summary>
    public string? OperationUnit { get; init; }

    /// <summary>Scope weight per operation, or <see langword="null"/>.</summary>
    public double? ScopeWeightPerOperation { get; init; }

    /// <summary>Top-frame weight per operation, or <see langword="null"/>.</summary>
    public double? TopWeightPerOperation { get; init; }
}