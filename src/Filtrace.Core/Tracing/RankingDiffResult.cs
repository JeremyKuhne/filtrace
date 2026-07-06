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
    IReadOnlyList<DiffRow> Rows);
