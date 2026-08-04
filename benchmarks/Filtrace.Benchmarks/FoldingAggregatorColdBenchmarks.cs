// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>Measures first-query aggregation with an empty short-frame cache.</summary>
[MemoryDiagnoser]
public class FoldingAggregatorColdBenchmarks
{
    private StackSampleSource _source = null!;

    /// <summary>The sample-count, stack-depth, and frame-cardinality scenario.</summary>
    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = null!;

    /// <summary>The valid synthetic scenarios.</summary>
    public static IEnumerable<string> Scenarios => FoldingBenchmarkCorpus.ScenarioNames;

    /// <summary>Builds the immutable sample source outside the measured operation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _source = FoldingBenchmarkCorpus.Create(Scenario);
        FoldingAggregator validation = new(_source);
        if (validation.SelfTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25).Rows.Count == 0
            || validation.InclusiveTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25).Rows.Count == 0)
        {
            throw new InvalidOperationException($"Scenario '{Scenario}' produced no ranking rows.");
        }
    }

    /// <summary>Constructs an aggregator and ranks folded leaf weight.</summary>
    [Benchmark]
    public RankingResult SelfTime() =>
        new FoldingAggregator(_source).SelfTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>Constructs an aggregator and ranks every frame by inclusive weight.</summary>
    [Benchmark]
    public RankingResult InclusiveTime() =>
        new FoldingAggregator(_source).InclusiveTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);
}
