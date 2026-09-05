// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures the provider-agnostic ranking passes over a stable synthetic stack source.
/// </summary>
[MemoryDiagnoser]
public class FoldingAggregatorBenchmarks
{
    private const string FocusFrame = "Pipeline.Frame1.Run";
    private const string SourceFile = "Pipeline.cs";
    private FoldingAggregator _aggregator = null!;

    /// <summary>
    ///  The sample-count, stack-depth, and frame-cardinality scenario.
    /// </summary>
    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = null!;

    /// <summary>
    ///  The valid synthetic scenarios.
    /// </summary>
    public static IEnumerable<string> Scenarios => FoldingBenchmarkCorpus.ScenarioNames;

    /// <summary>
    ///  Builds and primes the immutable synthetic source outside the measured operations.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _aggregator = new FoldingAggregator(FoldingBenchmarkCorpus.Create(Scenario));
        if (SelfTime().Rows.Count == 0
            || InclusiveTime().Rows.Count == 0
            || CallersOf().Callers.Count == 0
            || HotLines().Rows.Count == 0
            || SourceHeatmap().Lines.Count == 0
            || CallTree().Root.Children.Count == 0
            || Classify().Categories.Count == 0)
        {
            throw new InvalidOperationException($"Scenario '{Scenario}' produced an empty analysis result.");
        }
    }

    /// <summary>
    ///  Ranks samples by folded leaf weight.
    /// </summary>
    /// <returns>The scoped ranking and its top 25 leaf rows.</returns>
    [Benchmark]
    public RankingResult SelfTime() =>
        _aggregator.SelfTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>
    ///  Ranks every distinct frame by inclusive weight.
    /// </summary>
    /// <returns>The scoped ranking and its top 25 inclusive rows.</returns>
    [Benchmark]
    public RankingResult InclusiveTime() =>
        _aggregator.InclusiveTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>
    ///  Finds immediate callers of a stable synthetic focus frame.
    /// </summary>
    /// <returns>The focus-frame weight and its top 25 caller rows.</returns>
    [Benchmark]
    public CallersResult CallersOf() =>
        _aggregator.CallersOf(FocusFrame, string.Empty, top: 25);

    /// <summary>
    ///  Ranks location-bearing synthetic leaf samples by source line.
    /// </summary>
    /// <returns>The method scope and its top 25 attributed source lines.</returns>
    [Benchmark]
    public LineRankingResult HotLines() =>
        _aggregator.HotLines("Pipeline.Frame", FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>
    ///  Builds the per-line heatmap for the synthetic source file.
    /// </summary>
    /// <returns>The source-file scope and every weighted line in it.</returns>
    [Benchmark]
    public SourceHeatmapResult SourceHeatmap() =>
        _aggregator.SourceHeatmap(SourceFile, FrameNames.DefaultFoldPatterns);

    /// <summary>
    ///  Builds a bounded top-down tree over the synthetic stacks.
    /// </summary>
    /// <returns>The complete-weight root and descendants through depth 20.</returns>
    [Benchmark]
    public CallTreeResult CallTree() =>
        _aggregator.CallTree(string.Empty, FrameNames.DefaultFoldPatterns, maxDepth: 20, minPercentOfScope: 0.0);

    /// <summary>
    ///  Classifies synthetic self-time by runtime work category.
    /// </summary>
    /// <returns>The scoped total partitioned into runtime work categories.</returns>
    [Benchmark]
    public ClassifyResult Classify() =>
        _aggregator.Classify(string.Empty);
}
