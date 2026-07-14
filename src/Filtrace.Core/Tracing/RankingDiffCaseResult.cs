// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>One benchmark case's normalized baseline/current ranking diff.</summary>
/// <param name="Benchmark">Exact benchmark name.</param>
/// <param name="Parameters">Stable parameter display, empty when unparameterized.</param>
/// <param name="BeforeScopeWeight">Baseline scoped weight.</param>
/// <param name="AfterScopeWeight">Current scoped weight.</param>
/// <param name="ScopeDelta">Current minus baseline scoped weight.</param>
/// <param name="Rows">Bounded changed-frame rows.</param>
/// <param name="Warnings">Case-specific quality, metadata, or load warnings.</param>
public sealed record RankingDiffCaseResult(
    string Benchmark,
    string Parameters,
    double BeforeScopeWeight,
    double AfterScopeWeight,
    double ScopeDelta,
    IReadOnlyList<DiffRow> Rows,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Baseline contributing records, or <see langword="null"/>.</summary>
    public int? BeforeContributingRecordCount { get; init; }

    /// <summary>Current contributing records, or <see langword="null"/>.</summary>
    public int? AfterContributingRecordCount { get; init; }

    /// <summary>Shared operation unit, or <see langword="null"/>.</summary>
    public string? OperationUnit { get; init; }

    /// <summary>Baseline scope weight per operation, or <see langword="null"/>.</summary>
    public double? BeforeScopeWeightPerOperation { get; init; }

    /// <summary>Current scope weight per operation, or <see langword="null"/>.</summary>
    public double? AfterScopeWeightPerOperation { get; init; }

    /// <summary>Per-operation scope change, or <see langword="null"/>.</summary>
    public double? ScopeWeightPerOperationDelta { get; init; }
}