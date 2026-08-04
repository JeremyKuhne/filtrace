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
    private const int DistinctStackCount = 64;
    private const int ThreadCount = 8;

    private FoldingAggregator _aggregator = null!;

    /// <summary>The number of normalized stack samples to aggregate.</summary>
    [Params(1_000, 10_000)]
    public int SampleCount { get; set; }

    /// <summary>Builds the immutable synthetic source outside the measured operations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        IReadOnlyList<string>[] frameSets = new IReadOnlyList<string>[DistinctStackCount];
        for (int stackIndex = 0; stackIndex < frameSets.Length; stackIndex++)
        {
            string leaf = stackIndex % 2 == 0
                ? "CPU_TIME"
                : $"filtrace!Worker.Leaf{stackIndex % 8}()";
            frameSets[stackIndex] =
            [
                "filtrace!Program.Main()",
                $"filtrace!Pipeline.Stage{stackIndex % 16}.Run()",
                $"filtrace!Worker.Execute{stackIndex % 8}()",
                leaf
            ];
        }

        string[] threads = [.. Enumerable.Range(0, ThreadCount).Select(static index => $"Thread {index}")];
        SampleStack[] samples = new SampleStack[SampleCount];
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            samples[sampleIndex] = new SampleStack(
                frameSets[sampleIndex % frameSets.Length],
                weight: 1.0,
                thread: threads[sampleIndex % threads.Length]);
        }

        StackSampleSource source = new(
            MetricInfo.Cpu,
            samples,
            StackRecordSemantics.PeriodicCpuSamples);
        _aggregator = new FoldingAggregator(source);
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
