// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Core.Tests;

[TestClass]
public sealed class FrameMatchAnalyzerTests
{
    [TestMethod]
    public void Analyze_OutermostSelection_ReportsEveryDefinitionAndDepth()
    {
        StackSampleSource source = Source();

        FrameMatchReport report = FrameMatchAnalyzer.Analyze(
            source, "Deserialize", FrameMatchSelection.Outermost);

        report.MatchingStackCount.Should().Be(2);
        report.IsAmbiguous.Should().BeTrue();
        report.Matches.Should().HaveCount(2);

        FrameMatch wrapper = report.Matches.Single(
            static match => match.Frame.StartsWith("BenchmarkDotNet", StringComparison.Ordinal));
        wrapper.Depths.Should().Equal(1);
        wrapper.MatchingStackCount.Should().Be(2);
        wrapper.SelectedStackCount.Should().Be(2);

        FrameMatch benchmark = report.Matches.Single(
            static match => match.Frame.StartsWith("Touki.Perf", StringComparison.Ordinal));
        benchmark.Depths.Should().Equal(2, 3);
        benchmark.MatchingStackCount.Should().Be(2);
        benchmark.SelectedStackCount.Should().Be(0);
    }

    [TestMethod]
    public void Analyze_DeepestSelection_ReportsActualBenchmarkAsSelected()
    {
        StackSampleSource source = Source();

        FrameMatchReport report = FrameMatchAnalyzer.Analyze(
            source, "Deserialize", FrameMatchSelection.Deepest);

        FrameMatch wrapper = report.Matches.Single(
            static match => match.Frame.StartsWith("BenchmarkDotNet", StringComparison.Ordinal));
        wrapper.SelectedStackCount.Should().Be(0);

        FrameMatch benchmark = report.Matches.Single(
            static match => match.Frame.StartsWith("Touki.Perf", StringComparison.Ordinal));
        benchmark.SelectedStackCount.Should().Be(2);
    }

    private static StackSampleSource Source() => new(
        MetricInfo.Cpu,
        [
            new SampleStack(
                [
                    "dotnet!Program.Main()",
                    "BenchmarkDotNet!Runnable.Deserialize()",
                    "Touki.Perf!NrbfBenchmarks.Deserialize()",
                    "System.Private.CoreLib!CPU_TIME"
                ],
                10.0),
            new SampleStack(
                [
                    "dotnet!Program.Main()",
                    "BenchmarkDotNet!Runnable.Deserialize()",
                    "BenchmarkDotNet!Runnable.WorkloadAction()",
                    "Touki.Perf!NrbfBenchmarks.Deserialize()",
                    "System.Private.CoreLib!CPU_TIME"
                ],
                20.0)
        ]);
}