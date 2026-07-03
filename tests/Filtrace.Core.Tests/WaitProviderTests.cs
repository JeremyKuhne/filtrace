// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class WaitProviderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static StackSampleSource LoadWait() =>
        new WaitProvider().Read(FixturePath("wait.nettrace"));

    [TestMethod]
    public void Read_WaitFixture_CarriesTheWaitMetric()
    {
        StackSampleSource source = LoadWait();

        source.Metric.Should().Be(MetricInfo.Wait);
        source.Metric.Unit.Should().Be("ms");
        source.Samples.Should().NotBeEmpty("the trace carries WaitHandleWait/Start+Stop stacks");
    }

    [TestMethod]
    public void Read_WaitFixture_WeightsEachWaitByBlockedMilliseconds()
    {
        StackSampleSource source = LoadWait();

        source.Samples.Should().OnlyContain(s => s.Weight > 0.0);
    }

    [TestMethod]
    public void Read_WaitFixture_DropsSyntheticAndPseudoFrames()
    {
        StackSampleSource source = LoadWait();

        source.Samples.SelectMany(s => s.Frames).Should().OnlyContain(f =>
            !f.StartsWith("EventData ", StringComparison.Ordinal)
            && !f.StartsWith("BROKEN", StringComparison.Ordinal)
            && !f.StartsWith("Process", StringComparison.Ordinal)
            && !f.StartsWith("Thread (", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InclusiveTime_WaitFixture_RanksTheBlockingSite()
    {
        FoldingAggregator aggregator = new(LoadWait());

        RankingResult result = aggregator.InclusiveTime("", FrameNames.DefaultFoldPatterns, 50);

        result.ScopeWeight.Should().BeGreaterThan(0);

        // The workload's single blocking site is BlockOnHandle, which calls WaitOne.
        result.Rows.Should().Contain(r => r.Frame.Contains("BlockOnHandle", StringComparison.Ordinal));
        result.Rows.Should().Contain(r => r.Frame.Contains("WaitOne", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SelfTime_WaitFixture_RanksByBlockedTimeDescending()
    {
        FoldingAggregator aggregator = new(LoadWait());

        RankingResult result = aggregator.SelfTime("", FrameNames.DefaultFoldPatterns, 25);

        result.Rows.Should().NotBeEmpty();
        result.Rows[0].Weight.Should().BeGreaterThan(0);
        for (int i = 1; i < result.Rows.Count; i++)
        {
            result.Rows[i].Weight.Should().BeLessThanOrEqualTo(result.Rows[i - 1].Weight);
        }
    }

    [TestMethod]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        WaitProvider provider = new();

        Action act = () => provider.Read(FixturePath("does-not-exist.nettrace"));

        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void Read_NullOrEmptyPath_ThrowsArgument(string? path)
    {
        WaitProvider provider = new();

        Action act = () => provider.Read(path!);

        act.Should().Throw<ArgumentException>();
    }
}
