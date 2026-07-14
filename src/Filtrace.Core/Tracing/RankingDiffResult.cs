// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The change between two rankings of the same metric: the per-frame deltas
///  ordered by the size of the change, plus the scope totals on each side.
/// </summary>
/// <remarks>
///  <para>
///   This is the engine's <c>diff</c> verb. It is purely a comparison of two
///   rankings, so it is provider-agnostic - diff two CPU rankings to find a
///   time regression, or two allocation rankings to find an allocation growth -
///   and composes with scoping and filtering (diff two filtered, scoped
///   rankings). The two rankings must be of the same metric and kind (both
///   self-time or both inclusive); mixing them is a caller error the result
///   shape cannot guard against.
///  </para>
/// </remarks>
/// <param name="BeforeScopeWeight">The baseline ranking's scoped total, in the metric's unit.</param>
/// <param name="AfterScopeWeight">The current ranking's scoped total, in the metric's unit.</param>
/// <param name="ScopeDelta">The change in scoped total (<c>AfterScopeWeight - BeforeScopeWeight</c>).</param>
/// <param name="Rows">The per-frame changes, largest absolute change first.</param>
public sealed record RankingDiffResult(
    double BeforeScopeWeight,
    double AfterScopeWeight,
    double ScopeDelta,
    IReadOnlyList<DiffRow> Rows)
{
    /// <summary>
    ///  Case-keyed manifest diffs. Empty for a direct trace pair.
    /// </summary>
    public IReadOnlyList<RankingDiffCaseResult> Cases { get; init; } = [];

    /// <summary>Baseline records contributing to the ranking, or <see langword="null"/> when unavailable.</summary>
    public int? BeforeContributingRecordCount { get; init; }

    /// <summary>Current records contributing to the ranking, or <see langword="null"/> when unavailable.</summary>
    public int? AfterContributingRecordCount { get; init; }

    /// <summary>Unit named by complete per-operation metadata, or <see langword="null"/>.</summary>
    /// <remarks>Direct trace pairs have no operation metadata and leave this <see langword="null"/>.</remarks>
    public string? OperationUnit { get; init; }

    /// <summary>Baseline scope weight per operation, or <see langword="null"/>.</summary>
    public double? BeforeScopeWeightPerOperation { get; init; }

    /// <summary>Current scope weight per operation, or <see langword="null"/>.</summary>
    public double? AfterScopeWeightPerOperation { get; init; }

    /// <summary>Per-operation scope-weight change, or <see langword="null"/>.</summary>
    public double? ScopeWeightPerOperationDelta { get; init; }
}
