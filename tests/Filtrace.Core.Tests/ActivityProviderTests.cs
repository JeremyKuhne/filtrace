// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class ActivityProviderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static StackSampleSource LoadActivity() =>
        new ActivityProvider().Read(FixturePath("activity.nettrace"));

    [TestMethod]
    public void Read_ActivityFixture_CarriesTheActivityMetric()
    {
        StackSampleSource source = LoadActivity();

        source.Metric.Should().Be(MetricInfo.Activity);
        source.Metric.Unit.Should().Be("ms");
        source.Samples.Should().NotBeEmpty("the trace carries start-stop activities");
    }

    [TestMethod]
    public void Read_ActivityFixture_WeightsEachActivityByPositiveDuration()
    {
        StackSampleSource source = LoadActivity();

        // Each sample is weighted by the activity's wall-clock duration in milliseconds;
        // a non-positive duration is dropped by the provider, so every sample is positive.
        source.Samples.Should().OnlyContain(s => s.Weight > 0.0);
    }

    [TestMethod]
    public void Read_ActivityFixture_NamesFramesByTaskNotInstance()
    {
        StackSampleSource source = LoadActivity();

        // Frames are the clean activity task name (Order / Query / Render), not the
        // per-instance activity Name (which embeds a unique activity path), so instances
        // fold together in the ranking.
        source.Samples.SelectMany(s => s.Frames).Should().Contain("Order");
        source.Samples.SelectMany(s => s.Frames).Should().OnlyContain(f => !f.Contains('('));
    }

    [TestMethod]
    public void SelfTime_ActivityFixture_RanksTheNamedActivitiesByDuration()
    {
        FoldingAggregator aggregator = new(LoadActivity());

        RankingResult result = aggregator.SelfTime("", FrameNames.DefaultFoldPatterns, 50);

        result.ScopeWeight.Should().BeGreaterThan(0);

        // The workload nests Order { Query, Render } with descending per-round durations,
        // so the three activity types appear and rank Order > Query > Render.
        double order = FrameWeight(result, "Order");
        double query = FrameWeight(result, "Query");
        double render = FrameWeight(result, "Render");

        order.Should().BeGreaterThan(query);
        query.Should().BeGreaterThan(render);
        render.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void SelfTime_ActivityFixture_RanksByDurationDescending()
    {
        FoldingAggregator aggregator = new(LoadActivity());

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
        ActivityProvider provider = new();

        Action act = () => provider.Read(FixturePath("does-not-exist.nettrace"));

        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void Read_NullOrEmptyPath_ThrowsArgument(string? path)
    {
        ActivityProvider provider = new();

        Action act = () => provider.Read(path!);

        act.Should().Throw<ArgumentException>();
    }

    private static double FrameWeight(RankingResult result, string frame) =>
        result.Rows.Where(r => r.Frame == frame).Sum(r => r.Weight);
}
