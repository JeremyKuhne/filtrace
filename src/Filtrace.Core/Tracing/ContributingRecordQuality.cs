// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Quality guidance for query-specific contributing-record counts.
/// </summary>
public static class ContributingRecordQuality
{
    /// <summary>The default directional minimum for method-level CPU analysis.</summary>
    public const int DefaultMinimumMethodRecords = 200;

    /// <summary>The default directional minimum for source-line CPU analysis.</summary>
    public const int DefaultMinimumLineRecords = 1000;

    /// <summary>
    ///  Produces a thin-scope warning for a method-level periodic CPU result.
    /// </summary>
    public static bool TryGetMethodWarning(
        StackRecordSemantics semantics,
        int? contributingRecordCount,
        out string? warning,
        int minimumRecords = DefaultMinimumMethodRecords) =>
        TryGetWarning(semantics, contributingRecordCount, minimumRecords, "method-level", out warning);

    /// <summary>
    ///  Produces a thin-scope warning for a source-line periodic CPU result.
    /// </summary>
    public static bool TryGetLineWarning(
        StackRecordSemantics semantics,
        int? attributedRecordCount,
        out string? warning,
        int minimumRecords = DefaultMinimumLineRecords) =>
        TryGetWarning(semantics, attributedRecordCount, minimumRecords, "line-level", out warning);

    private static bool TryGetWarning(
        StackRecordSemantics semantics,
        int? recordCount,
        int minimumRecords,
        string level,
        out string? warning)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumRecords);
        if (semantics != StackRecordSemantics.PeriodicCpuSamples
            || recordCount is null
            || recordCount <= 0
            || recordCount >= minimumRecords)
        {
            warning = null;
            return false;
        }

        warning =
            $"Only {recordCount.Value} periodic CPU records contribute to this {level} result; "
            + $"use at least {minimumRecords} for directional confidence or capture longer.";
        return true;
    }
}