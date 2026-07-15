// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A single frame's change between a baseline and a current ranking.
/// </summary>
/// <param name="Frame">The shortened frame name.</param>
/// <param name="BeforeWeight">The frame's weight in the baseline ranking, in the metric's unit (0 if absent).</param>
/// <param name="AfterWeight">The frame's weight in the current ranking, in the metric's unit (0 if absent).</param>
/// <param name="Delta">The change in weight (<c>AfterWeight - BeforeWeight</c>); positive is a regression.</param>
public sealed record DiffRow(string Frame, double BeforeWeight, double AfterWeight, double Delta)
{
    /// <summary>The frame's share of baseline scope, in percent.</summary>
    public double BeforePercentOfScope { get; init; }

    /// <summary>The frame's share of current scope, in percent.</summary>
    public double AfterPercentOfScope { get; init; }

    /// <summary>
    ///  Change in share of scope, in percentage points
    ///  (<c>AfterPercentOfScope - BeforePercentOfScope</c>).
    /// </summary>
    public double PercentagePointChange { get; init; }

    /// <summary>
    ///  Current weight scaled to the baseline scope minus baseline weight, or
    ///  <see langword="null"/> when either scope has zero weight.
    /// </summary>
    public double? NormalizedWeightChange { get; init; }

    /// <summary><c>appeared</c>, <c>disappeared</c>, or <c>changed</c>.</summary>
    public string ChangeKind { get; init; } = "changed";

    /// <summary>Baseline weight per operation, or <see langword="null"/> when unavailable.</summary>
    public double? BeforeWeightPerOperation { get; init; }

    /// <summary>Current weight per operation, or <see langword="null"/> when unavailable.</summary>
    public double? AfterWeightPerOperation { get; init; }

    /// <summary>Per-operation change, or <see langword="null"/> when unavailable.</summary>
    public double? PerOperationDelta { get; init; }
}
