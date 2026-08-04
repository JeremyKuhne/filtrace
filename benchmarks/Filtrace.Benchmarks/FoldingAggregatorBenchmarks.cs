// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures the provider-agnostic ranking passes over a stable synthetic stack source.
/// </summary>
[MemoryDiagnoser]
public class FoldingAggregatorBenchmarks
{
    private FoldingAggregator _aggregator = null!;

    /// <summary>The sample-count, stack-depth, and frame-cardinality scenario.</summary>
    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = null!;

    /// <summary>The valid synthetic scenarios.</summary>
    public static IEnumerable<string> Scenarios => FoldingBenchmarkCorpus.ScenarioNames;

    /// <summary>Builds and primes the immutable synthetic source outside the measured operations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _aggregator = new FoldingAggregator(FoldingBenchmarkCorpus.Create(Scenario));
        if (SelfTime().Rows.Count == 0 || InclusiveTime().Rows.Count == 0)
        {
            throw new InvalidOperationException($"Scenario '{Scenario}' produced no ranking rows.");
        }
    }

    /// <summary>Ranks samples by folded leaf weight.</summary>
    [Benchmark]
    public RankingResult SelfTime() =>
        _aggregator.SelfTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);

    /// <summary>Ranks every distinct frame by inclusive weight.</summary>
    [Benchmark]
    public RankingResult InclusiveTime() =>
        _aggregator.InclusiveTime(string.Empty, FrameNames.DefaultFoldPatterns, top: 25);
}
