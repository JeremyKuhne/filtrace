// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  One manifest case's compact ranking summary.
/// </summary>
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
    /// <summary>
    ///  The run-unique manifest case identifier, or <see langword="null"/> for legacy callers.
    /// </summary>
    public string? CaseId { get; init; }

    /// <summary>
    ///  Operation unit, or <see langword="null"/> when metadata is incomplete.
    /// </summary>
    public string? OperationUnit { get; init; }

    /// <summary>
    ///  Scope weight per operation, or <see langword="null"/>.
    /// </summary>
    public double? ScopeWeightPerOperation { get; init; }

    /// <summary>
    ///  Top-frame weight per operation, or <see langword="null"/>.
    /// </summary>
    public double? TopWeightPerOperation { get; init; }

    /// <summary>
    ///  Pre-root and retained coverage, or <see langword="null"/> without a root.
    /// </summary>
    public RootScopeCoverage? RootCoverage { get; init; }
}
