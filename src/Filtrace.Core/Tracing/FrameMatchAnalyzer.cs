// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Diagnoses substring frame matching before a stack query is aggregated.
/// </summary>
public static class FrameMatchAnalyzer
{
    /// <summary>
    ///  Reports every distinct full frame definition matching
    ///  <paramref name="selector"/>, where it occurs, and which definition the
    ///  query's selection rule chooses on each stack.
    /// </summary>
    /// <param name="source">The normalized stack source to inspect.</param>
    /// <param name="selector">The substring matched against full frame names.</param>
    /// <param name="selection">Whether the query selects the outermost or deepest match.</param>
    /// <returns>The match report.</returns>
    public static FrameMatchReport Analyze(
        StackSampleSource source,
        string selector,
        FrameMatchSelection selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(selector);

        Dictionary<string, MatchAccumulator> matches = new(StringComparer.Ordinal);
        int matchingStackCount = 0;

        foreach (SampleStack sample in source.Samples)
        {
            HashSet<string> definitionsInStack = new(StringComparer.Ordinal);
            string? selectedFrame = null;

            for (int depth = 0; depth < sample.Frames.Count; depth++)
            {
                string frame = sample.Frames[depth];
                if (!frame.Contains(selector, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!matches.TryGetValue(frame, out MatchAccumulator? accumulator))
                {
                    accumulator = new MatchAccumulator(frame);
                    matches.Add(frame, accumulator);
                }

                accumulator.Depths.Add(depth);
                if (definitionsInStack.Add(frame))
                {
                    accumulator.MatchingStackCount++;
                }

                if (selectedFrame is null || selection == FrameMatchSelection.Deepest)
                {
                    selectedFrame = frame;
                }
            }

            if (selectedFrame is not null)
            {
                matchingStackCount++;
                matches[selectedFrame].SelectedStackCount++;
            }
        }

        List<FrameMatch> results = new(matches.Count);
        foreach (MatchAccumulator accumulator in matches.Values)
        {
            results.Add(new FrameMatch(
                accumulator.Frame,
                [.. accumulator.Depths.Order()],
                accumulator.MatchingStackCount,
                accumulator.SelectedStackCount));
        }

        results.Sort(static (left, right) =>
        {
            int bySelected = right.SelectedStackCount.CompareTo(left.SelectedStackCount);
            return bySelected != 0 ? bySelected : string.CompareOrdinal(left.Frame, right.Frame);
        });

        return new FrameMatchReport(selector, selection, matchingStackCount, results);
    }

    private sealed class MatchAccumulator(string frame)
    {
        public string Frame { get; } = frame;

        public HashSet<int> Depths { get; } = [];

        public int MatchingStackCount { get; set; }

        public int SelectedStackCount { get; set; }
    }
}