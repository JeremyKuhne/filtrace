// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Core.Tests;

[TestClass]
public sealed class FoldingAggregatorRecordCountTests
{
    [TestMethod]
    public void SelfTime_RootScope_ReportsContributingPeriodicRecords()
    {
        FoldingAggregator aggregator = Aggregator();

        RankingResult result = aggregator.SelfTime("Target", FrameNames.DefaultFoldPatterns, 25);

        result.ContributingRecordCount.Should().Be(2);
    }

    [TestMethod]
    public void CallersOf_FocusFrame_ReportsRecordsContainingFocus()
    {
        FoldingAggregator aggregator = Aggregator();

        CallersResult result = aggregator.CallersOf("Target", "", 25);

        result.ContributingRecordCount.Should().Be(2);
    }

    [TestMethod]
    public void HotLines_MethodFilter_ReportsAttributedAndUnattributedRecords()
    {
        FoldingAggregator aggregator = Aggregator();

        LineRankingResult result = aggregator.HotLines("Target", FrameNames.DefaultFoldPatterns, 25);

        result.AttributedRecordCount.Should().Be(1);
        result.UnattributedRecordCount.Should().Be(1);
    }

    [TestMethod]
    public void SourceHeatmap_FileFilter_ReportsAttributedAndUnattributedRecords()
    {
        FoldingAggregator aggregator = Aggregator();

        SourceHeatmapResult result = aggregator.SourceHeatmap("Target.cs", FrameNames.DefaultFoldPatterns);

        result.AttributedRecordCount.Should().Be(1);
        result.UnattributedRecordCount.Should().Be(1);
    }

    [TestMethod]
    public void SelfTime_UnknownRecordSemantics_ReportsCountUnavailable()
    {
        StackSampleSource source = new(
            MetricInfo.Cpu,
            [new SampleStack(["Root", "Target"], 1.0)]);

        RankingResult result = new FoldingAggregator(source).SelfTime("", FrameNames.DefaultFoldPatterns, 25);

        result.ContributingRecordCount.Should().BeNull();
    }

    private static FoldingAggregator Aggregator()
    {
        StackSampleSource source = new(
            MetricInfo.Cpu,
            [
                new SampleStack(["Root", "Target"], 1.0, frameLocations: ["Root.cs:1", "Target.cs:10"]),
                new SampleStack(["Root", "Target"], 1.0, frameLocations: ["Root.cs:1", ""]),
                new SampleStack(["Root", "Other"], 1.0, frameLocations: ["Root.cs:1", "Other.cs:20"])
            ],
            StackRecordSemantics.PeriodicCpuSamples);

        return new FoldingAggregator(source);
    }
}