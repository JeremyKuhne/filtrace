// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class ContentionProviderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static StackSampleSource LoadContention() =>
        new ContentionProvider().Read(FixturePath("contention.nettrace"));

    [TestMethod]
    public void Read_ContentionFixture_CarriesTheContentionMetric()
    {
        StackSampleSource source = LoadContention();

        source.Metric.Should().Be(MetricInfo.Contention);
        source.Metric.Unit.Should().Be("ms");
        source.Samples.Should().NotBeEmpty("the trace carries Contention/Start+Stop stacks");
    }

    [TestMethod]
    public void Read_ContentionFixture_WeightsEachContentionByBlockedMilliseconds()
    {
        StackSampleSource source = LoadContention();

        // Each sample is weighted by the blocked duration in milliseconds; a
        // non-positive weight is dropped by the provider, so every sample is positive.
        source.Samples.Should().OnlyContain(s => s.Weight > 0.0);
    }

    [TestMethod]
    public void Read_ContentionFixture_DropsSyntheticAndPseudoFrames()
    {
        StackSampleSource source = LoadContention();

        // The provider strips the computer's synthetic per-event leaves, TraceEvent's
        // BROKEN stack markers, and the process / thread roots so the ranking is over
        // real code only.
        source.Samples.SelectMany(s => s.Frames).Should().OnlyContain(f =>
            !f.StartsWith("EventData ", StringComparison.Ordinal)
            && !f.StartsWith("BROKEN", StringComparison.Ordinal)
            && !f.StartsWith("Process", StringComparison.Ordinal)
            && !f.StartsWith("Thread (", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InclusiveTime_ContentionFixture_RanksTheBlockingSite()
    {
        FoldingAggregator aggregator = new(LoadContention());

        RankingResult result = aggregator.InclusiveTime("", FrameNames.DefaultFoldPatterns, 50);

        result.ScopeWeight.Should().BeGreaterThan(0);

        // The workload's single blocking site is HoldLock, so it appears on the
        // contention stacks; the runtime's lock slow path is the blocking leaf.
        result.Rows.Should().Contain(r => r.Frame.Contains("HoldLock", StringComparison.Ordinal));
        result.Rows.Should().Contain(r => r.Frame.Contains("Monitor.Enter", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SelfTime_ContentionFixture_RanksByBlockedTimeDescending()
    {
        FoldingAggregator aggregator = new(LoadContention());

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
        ContentionProvider provider = new();

        Action act = () => provider.Read(FixturePath("does-not-exist.nettrace"));

        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void Read_NullOrEmptyPath_ThrowsArgument(string? path)
    {
        ContentionProvider provider = new();

        Action act = () => provider.Read(path!);

        act.Should().Throw<ArgumentException>();
    }
}
