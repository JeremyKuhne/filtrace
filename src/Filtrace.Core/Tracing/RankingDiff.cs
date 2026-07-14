// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Computes the change between a baseline ranking and a current one, so an agent
///  can see what got slower or faster (or allocated more or less) between two
///  runs.
/// </summary>
public static class RankingDiff
{
    /// <summary>
    ///  Diffs <paramref name="before"/> against <paramref name="after"/>, matching
    ///  rows by frame name and ordering the result by the size of the change.
    /// </summary>
    /// <param name="before">The baseline ranking.</param>
    /// <param name="after">The current ranking.</param>
    /// <param name="top">The maximum number of changed rows to return.</param>
    /// <returns>The diff: per-frame deltas, largest absolute change first.</returns>
    public static RankingDiffResult Diff(RankingResult before, RankingResult after, int top)
        => DiffCore(before, after, top, null, null, null);

    /// <summary>
    ///  Diffs two rankings and includes per-operation values when both sides supply
    ///  positive operation counts in the same <paramref name="operationUnit"/>.
    /// </summary>
    /// <param name="before">The baseline ranking.</param>
    /// <param name="after">The current ranking.</param>
    /// <param name="top">The maximum number of changed rows to return.</param>
    /// <param name="beforeOperationCount">Operations represented by the baseline capture.</param>
    /// <param name="afterOperationCount">Operations represented by the current capture.</param>
    /// <param name="operationUnit">The operation unit shared by both captures.</param>
    /// <returns>The normalized diff with per-operation values.</returns>
    public static RankingDiffResult DiffPerOperation(
        RankingResult before,
        RankingResult after,
        int top,
        double beforeOperationCount,
        double afterOperationCount,
        string operationUnit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beforeOperationCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(afterOperationCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationUnit);
        if (!double.IsFinite(beforeOperationCount))
        {
            throw new ArgumentOutOfRangeException(nameof(beforeOperationCount));
        }

        if (!double.IsFinite(afterOperationCount))
        {
            throw new ArgumentOutOfRangeException(nameof(afterOperationCount));
        }

        return DiffCore(
            before,
            after,
            top,
            beforeOperationCount,
            afterOperationCount,
            operationUnit);
    }

    private static RankingDiffResult DiffCore(
        RankingResult before,
        RankingResult after,
        int top,
        double? beforeOperationCount,
        double? afterOperationCount,
        string? operationUnit)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentOutOfRangeException.ThrowIfNegative(top);

        Dictionary<string, (double Before, double After)> byFrame = new(StringComparer.Ordinal);

        foreach (RankRow row in before.Rows)
        {
            byFrame[row.Frame] = (row.Weight, 0.0);
        }

        foreach (RankRow row in after.Rows)
        {
            byFrame.TryGetValue(row.Frame, out (double Before, double After) current);
            byFrame[row.Frame] = (current.Before, row.Weight);
        }

        List<DiffRow> rows = new(byFrame.Count);
        foreach (KeyValuePair<string, (double Before, double After)> pair in byFrame)
        {
            double delta = pair.Value.After - pair.Value.Before;
            double beforePercent = Percent(pair.Value.Before, before.ScopeWeight);
            double afterPercent = Percent(pair.Value.After, after.ScopeWeight);
            double percentagePointChange = afterPercent - beforePercent;

            // Keep a frame when its absolute cost or share of scope moved. A stable
            // absolute weight can still be a meaningful normalized regression when
            // the total workload changed between captures.
            if (delta == 0.0 && percentagePointChange == 0.0)
            {
                continue;
            }

            double? normalizedWeightChange = before.ScopeWeight > 0.0 && after.ScopeWeight > 0.0
                ? pair.Value.After * before.ScopeWeight / after.ScopeWeight - pair.Value.Before
                : null;
            double? beforePerOperation = beforeOperationCount is double beforeCount
                ? pair.Value.Before / beforeCount
                : null;
            double? afterPerOperation = afterOperationCount is double afterCount
                ? pair.Value.After / afterCount
                : null;

            rows.Add(new DiffRow(pair.Key, pair.Value.Before, pair.Value.After, delta)
            {
                BeforePercentOfScope = beforePercent,
                AfterPercentOfScope = afterPercent,
                PercentagePointChange = percentagePointChange,
                NormalizedWeightChange = normalizedWeightChange,
                ChangeKind = pair.Value.Before == 0.0 && pair.Value.After != 0.0
                    ? "appeared"
                    : pair.Value.Before != 0.0 && pair.Value.After == 0.0
                        ? "disappeared"
                        : "changed",
                BeforeWeightPerOperation = beforePerOperation,
                AfterWeightPerOperation = afterPerOperation,
                PerOperationDelta = afterPerOperation - beforePerOperation
            });
        }

        // Largest absolute change first (regressions and improvements alike), with a
        // deterministic ordinal tiebreak.
        rows.Sort(static (a, b) =>
        {
            double aMagnitude = Math.Round(Math.Abs(a.Delta), 9);
            double bMagnitude = Math.Round(Math.Abs(b.Delta), 9);
            int byMagnitude = bMagnitude.CompareTo(aMagnitude);
            return byMagnitude != 0 ? byMagnitude : string.CompareOrdinal(a.Frame, b.Frame);
        });

        if (rows.Count > top)
        {
            rows.RemoveRange(top, rows.Count - top);
        }

        double? beforeScopePerOperation = beforeOperationCount is double beforeOperations
            ? before.ScopeWeight / beforeOperations
            : null;
        double? afterScopePerOperation = afterOperationCount is double afterOperations
            ? after.ScopeWeight / afterOperations
            : null;

        return new RankingDiffResult(
            before.ScopeWeight,
            after.ScopeWeight,
            after.ScopeWeight - before.ScopeWeight,
            rows)
        {
            BeforeContributingRecordCount = before.ContributingRecordCount,
            AfterContributingRecordCount = after.ContributingRecordCount,
            OperationUnit = operationUnit,
            BeforeScopeWeightPerOperation = beforeScopePerOperation,
            AfterScopeWeightPerOperation = afterScopePerOperation,
            ScopeWeightPerOperationDelta = afterScopePerOperation - beforeScopePerOperation
        };
    }

    private static double Percent(double weight, double scopeWeight) =>
        scopeWeight > 0.0 ? 100.0 * weight / scopeWeight : 0.0;
}
