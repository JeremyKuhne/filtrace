// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Trims a stack-sample source to the subtree under a root frame, for verbs -
///  such as <c>export</c> - that need real scoped <see cref="SampleStack"/>
///  instances rather than an aggregate ranking.
/// </summary>
/// <remarks>
///  <para>
///   The ranking verbs (<see cref="FoldingAggregator"/>) scope a query to a root
///   frame inline, per aggregation, without materializing a new sample list -
///   there is no output that needs the trimmed stacks themselves. A flame-graph
///   export is different: it serializes the actual call stacks, so scoping it to
///   a subtree (a BenchmarkDotNet capture's measured workload, say) means
///   building real trimmed samples. This type applies the identical root-frame
///   match (<see cref="FrameNames.TryFindRootStart"/>) that the ranking verbs use,
///   so <c>export --root</c> / <c>--benchmark</c> scopes a flame graph exactly the
///   way <c>cpu --root</c> / <c>--benchmark</c> scopes a ranking.
///  </para>
/// </remarks>
public static class RootScope
{
    /// <summary>
    ///  Applies the root-frame scope to a stack-sample source.
    /// </summary>
    /// <param name="source">The source to scope.</param>
    /// <param name="rootFrame">
    ///  Substring identifying the root frame, or empty/<see langword="null"/> for no
    ///  scoping.
    /// </param>
    /// <returns>
    ///  <paramref name="source"/> itself, unchanged, when <paramref name="rootFrame"/>
    ///  is empty. Otherwise a new <see cref="StackSampleSource"/> with the same
    ///  metric, keeping only the samples whose stack contains a frame matching
    ///  <paramref name="rootFrame"/>, each trimmed (together with its parallel
    ///  <see cref="SampleStack.FrameLocations"/>, when present) to start at that
    ///  frame - so the exported flame graph is rooted there instead of at the
    ///  process/thread root.
    /// </returns>
    public static StackSampleSource Apply(StackSampleSource source, string rootFrame)
    {
        if (string.IsNullOrEmpty(rootFrame))
        {
            return source;
        }

        List<SampleStack> scoped = new(source.Samples.Count);
        foreach (SampleStack sample in source.Samples)
        {
            IReadOnlyList<string> frames = sample.Frames;
            if (!FrameNames.TryFindRootStart(frames, rootFrame, out int start) || frames.Count == 0)
            {
                // No matching frame on this stack - it never entered the scoped
                // subtree, so drop it entirely rather than exporting it unscoped.
                continue;
            }

            scoped.Add(new SampleStack(
                Trim(frames, start),
                sample.Weight,
                sample.Thread,
                sample.FrameLocations is { } locations ? Trim(locations, start) : null,
                sample.Process));
        }

        return new StackSampleSource(source.Metric, scoped);
    }

    /// <summary>
    ///  Slices a frames or frame-locations list to start at <paramref name="start"/>,
    ///  avoiding an allocation when the slice is the whole list.
    /// </summary>
    private static IReadOnlyList<string> Trim(IReadOnlyList<string> values, int start)
    {
        if (start == 0)
        {
            return values;
        }

        string[] trimmed = new string[values.Count - start];
        for (int i = 0; i < trimmed.Length; i++)
        {
            trimmed[i] = values[start + i];
        }

        return trimmed;
    }
}
