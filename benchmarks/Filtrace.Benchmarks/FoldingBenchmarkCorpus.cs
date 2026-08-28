// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal static partial class FoldingBenchmarkCorpus
{
    private const int ThreadCount = 8;

    private static readonly FoldingScenario[] s_scenarios =
    [
        new("s100-d5-f64", 100, 5, 64),
        new("s100-d5-f256", 100, 5, 256),
        new("s100-d20-f64", 100, 20, 64),
        new("s100-d20-f1024", 100, 20, 1024),
        new("s1000-d5-f64", 1_000, 5, 64),
        new("s1000-d5-f2048", 1_000, 5, 2_048),
        new("s1000-d20-f64", 1_000, 20, 64),
        new("s1000-d20-f4096", 1_000, 20, 4_096),
        new("s5000-d5-f64", 5_000, 5, 64),
        new("s5000-d5-f4096", 5_000, 5, 4_096),
        new("s5000-d20-f64", 5_000, 20, 64),
        new("s5000-d20-f4096", 5_000, 20, 4_096),
        new("s10000-d5-f64", 10_000, 5, 64),
        new("s10000-d5-f4096", 10_000, 5, 4_096),
        new("s10000-d20-f64", 10_000, 20, 64),
        new("s10000-d20-f4096", 10_000, 20, 4_096),
        new("s100000-d20-f64", 100_000, 20, 64),
        new("s100000-d20-f4096", 100_000, 20, 4_096),
        new("s1000000-d20-f64", 1_000_000, 20, 64),
        new("s1000000-d20-f4096", 1_000_000, 20, 4_096)
    ];

    public static IEnumerable<string> ScenarioNames =>
        s_scenarios.Select(static scenario => scenario.Name);

    public static StackSampleSource Create(string scenarioName)
        => Create(scenarioName, MetricInfo.Cpu, StackRecordSemantics.PeriodicCpuSamples);

    public static StackSampleSource Create(
        string scenarioName,
        MetricInfo metric,
        StackRecordSemantics recordSemantics)
    {
        FoldingScenario scenario = s_scenarios.Single(
            scenario => string.Equals(scenario.Name, scenarioName, StringComparison.Ordinal));
        int occurrenceCount = checked(scenario.SampleCount * scenario.StackDepth);
        if (scenario.DistinctFrameCount > occurrenceCount)
        {
            throw new InvalidOperationException(
                $"Scenario '{scenario.Name}' requests {scenario.DistinctFrameCount} frames from {occurrenceCount} occurrences.");
        }

        int stackSetCount = (scenario.DistinctFrameCount + scenario.StackDepth - 1) / scenario.StackDepth;
        string[][] frameSets = new string[stackSetCount][];
        string[][] locationSets = new string[stackSetCount][];
        HashSet<string> distinctFrames = new(StringComparer.Ordinal);
        for (int stackIndex = 0; stackIndex < stackSetCount; stackIndex++)
        {
            string[] frames = new string[scenario.StackDepth];
            string[] locations = new string[scenario.StackDepth];
            for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                int distinctIndex = (stackIndex * scenario.StackDepth + frameIndex)
                    % scenario.DistinctFrameCount;
                string frame = $"filtrace!Pipeline.Frame{distinctIndex}.Run()";
                frames[frameIndex] = frame;
                locations[frameIndex] = $"Pipeline.cs:{distinctIndex + 1}";
                distinctFrames.Add(frame);
            }

            frameSets[stackIndex] = frames;
            locationSets[stackIndex] = locations;
        }

        if (distinctFrames.Count != scenario.DistinctFrameCount)
        {
            throw new InvalidOperationException(
                $"Scenario '{scenario.Name}' generated {distinctFrames.Count} of {scenario.DistinctFrameCount} requested frames.");
        }

        string[] threads = [.. Enumerable.Range(0, ThreadCount).Select(static index => $"Thread {index}")];
        double[] weights = [0.0, 0.25, 1.0, 3.5, 1024.0];
        SampleStack[] samples = new SampleStack[scenario.SampleCount];
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            int stackIndex = sampleIndex % frameSets.Length;
            samples[sampleIndex] = new SampleStack(
                frameSets[stackIndex],
                weights[sampleIndex % weights.Length],
                threads[sampleIndex % threads.Length],
                locationSets[stackIndex]);
        }

        return new StackSampleSource(
            metric,
            samples,
            recordSemantics);
    }

}
