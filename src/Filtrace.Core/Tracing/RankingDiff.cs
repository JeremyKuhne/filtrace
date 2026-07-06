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

            // A frame present on both sides with no change carries no information;
            // drop it so the diff shows only what actually moved.
            if (delta == 0.0)
            {
                continue;
            }

            rows.Add(new DiffRow(pair.Key, pair.Value.Before, pair.Value.After, delta));
        }

        // Largest absolute change first (regressions and improvements alike), with a
        // deterministic ordinal tiebreak.
        rows.Sort(static (a, b) =>
        {
            int byMagnitude = Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta));
            return byMagnitude != 0 ? byMagnitude : string.CompareOrdinal(a.Frame, b.Frame);
        });

        if (rows.Count > top)
        {
            rows.RemoveRange(top, rows.Count - top);
        }

        return new RankingDiffResult(
            before.ScopeWeight,
            after.ScopeWeight,
            after.ScopeWeight - before.ScopeWeight,
            rows);
    }
}
