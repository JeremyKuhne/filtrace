// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>Measures aggregation-family parity across non-CPU metric units.</summary>
[MemoryDiagnoser]
public class FoldingAggregatorMetricBenchmarks
{
    private const string FocusFrame = "Pipeline.Frame1.Run";
    private const string SourceFile = "Pipeline.cs";
    private FoldingAggregator _aggregator = null!;

    /// <summary>The representative metric family.</summary>
    [Params("thread-time", "allocations", "count")]
    public string Metric { get; set; } = null!;

    /// <summary>Builds and validates the representative metric source.</summary>
    [GlobalSetup]
    public void Setup()
    {
        MetricInfo metric = Metric switch
        {
            "thread-time" => MetricInfo.ThreadTime,
            "allocations" => MetricInfo.Allocations,
            "count" => MetricInfo.Exceptions,
            _ => throw new ArgumentOutOfRangeException(nameof(Metric), Metric, "Unknown metric scenario.")
        };
        StackSampleSource source = FoldingBenchmarkCorpus.Create(
            "s10000-d20-f4096",
            metric,
            StackRecordSemantics.Unavailable);
        _aggregator = new FoldingAggregator(source);

        if (SelfTime().Rows.Count == 0
            || InclusiveTime().Rows.Count == 0
            || CallersOf().Callers.Count == 0
            || HotLines().Rows.Count == 0
            || SourceHeatmap().Lines.Count == 0
            || CallTree().Root.Children.Count == 0
            || Classify().Categories.Count == 0)
        {
            throw new InvalidOperationException(
                $"Metric scenario '{Metric}' produced an empty analysis result.");
        }
    }

    /// <summary>Ranks folded leaf weight.</summary>
    [Benchmark]
    public RankingResult SelfTime() =>
        _aggregator.SelfTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>Ranks every frame by inclusive weight.</summary>
    [Benchmark]
    public RankingResult InclusiveTime() =>
        _aggregator.InclusiveTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>Finds immediate callers of a stable focus frame.</summary>
    [Benchmark]
    public CallersResult CallersOf() =>
        _aggregator.CallersOf(FocusFrame, string.Empty, top: 25);

    /// <summary>Ranks the representative metric by synthetic source line.</summary>
    [Benchmark]
    public LineRankingResult HotLines() =>
        _aggregator.HotLines("Pipeline.Frame", FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>Builds the representative metric's source heatmap.</summary>
    [Benchmark]
    public SourceHeatmapResult SourceHeatmap() =>
        _aggregator.SourceHeatmap(SourceFile, FrameNames.DefaultFoldPatterns);

    /// <summary>Builds the representative metric's bounded call tree.</summary>
    [Benchmark]
    public CallTreeResult CallTree() =>
        _aggregator.CallTree(
            string.Empty,
            FrameNames.DefaultFoldPatterns,
            maxDepth: 20,
            minPercentOfScope: 0.0);

    /// <summary>Classifies the representative metric's leaf weights.</summary>
    [Benchmark]
    public ClassifyResult Classify() =>
        _aggregator.Classify(string.Empty);
}