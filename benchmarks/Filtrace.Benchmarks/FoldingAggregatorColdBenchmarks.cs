// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures first-query aggregation with an empty short-frame cache.
/// </summary>
[MemoryDiagnoser]
public class FoldingAggregatorColdBenchmarks
{
    private const string FocusFrame = "Pipeline.Frame1.Run";
    private const string SourceFile = "Pipeline.cs";
    private StackSampleSource _source = null!;

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
    ///  Builds the immutable sample source outside the measured operation.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _source = FoldingBenchmarkCorpus.Create(Scenario);
        FoldingAggregator validation = new(_source);
        if (validation.SelfTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25).Rows.Count == 0
            || validation.InclusiveTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25).Rows.Count == 0
            || validation.CallersOf(FocusFrame, string.Empty, top: 25).Callers.Count == 0
            || validation.HotLines("Pipeline.Frame", FrameNames.DefaultFoldPatterns, top: 25).Rows.Count == 0
            || validation.SourceHeatmap(SourceFile, FrameNames.DefaultFoldPatterns).Lines.Count == 0
            || validation.CallTree(
                string.Empty,
                FrameNames.DefaultFoldPatterns,
                maxDepth: 20,
                minPercentOfScope: 0.0).Root.Children.Count == 0
            || validation.Classify(string.Empty).Categories.Count == 0)
        {
            throw new InvalidOperationException($"Scenario '{Scenario}' produced an empty analysis result.");
        }
    }

    /// <summary>
    ///  Constructs an aggregator and ranks folded leaf weight.
    /// </summary>
    /// <returns>The scoped ranking and its top 25 leaf rows.</returns>
    [Benchmark]
    public RankingResult SelfTime() =>
        new FoldingAggregator(_source).SelfTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>
    ///  Constructs an aggregator and ranks every frame by inclusive weight.
    /// </summary>
    /// <returns>The scoped ranking and its top 25 inclusive rows.</returns>
    [Benchmark]
    public RankingResult InclusiveTime() =>
        new FoldingAggregator(_source).InclusiveTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>
    ///  Constructs an aggregator and finds callers of a stable focus frame.
    /// </summary>
    /// <returns>The focus-frame weight and its top 25 caller rows.</returns>
    [Benchmark]
    public CallersResult CallersOf() =>
        new FoldingAggregator(_source).CallersOf(FocusFrame, string.Empty, top: 25);

    /// <summary>
    ///  Constructs an aggregator and ranks synthetic source lines.
    /// </summary>
    /// <returns>The method scope and its top 25 attributed source lines.</returns>
    [Benchmark]
    public LineRankingResult HotLines() =>
        new FoldingAggregator(_source).HotLines(
            "Pipeline.Frame",
            FrameNames.DefaultFoldPatterns,
            top: 25);

    /// <summary>
    ///  Constructs an aggregator and builds a synthetic source heatmap.
    /// </summary>
    /// <returns>The source-file scope and every weighted line in it.</returns>
    [Benchmark]
    public SourceHeatmapResult SourceHeatmap() =>
        new FoldingAggregator(_source).SourceHeatmap(SourceFile, FrameNames.DefaultFoldPatterns);

    /// <summary>
    ///  Constructs an aggregator and builds a bounded call tree.
    /// </summary>
    /// <returns>The complete-weight root and descendants through depth 20.</returns>
    [Benchmark]
    public CallTreeResult CallTree() =>
        new FoldingAggregator(_source).CallTree(
            string.Empty,
            FrameNames.DefaultFoldPatterns,
            maxDepth: 20,
            minPercentOfScope: 0.0);

    /// <summary>
    ///  Constructs an aggregator and classifies synthetic self-time.
    /// </summary>
    /// <returns>The scoped total partitioned into runtime work categories.</returns>
    [Benchmark]
    public ClassifyResult Classify() =>
        new FoldingAggregator(_source).Classify(string.Empty);
}
