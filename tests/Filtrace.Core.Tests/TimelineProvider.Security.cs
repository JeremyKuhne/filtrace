// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class TimelineProviderSecurityTests
{
    private static string Alloc => Path.Combine(AppContext.BaseDirectory, "Fixtures", "alloc.nettrace");

    [TestMethod]
    [DataRow(TimelineProvider.MaxSnapshotNameChars, false)]
    [DataRow(TimelineProvider.MaxSnapshotNameChars + 1, true)]
    public void BoundSnapshotName_AtAndAboveLimit_IsBounded(int length, bool expectedTruncated)
    {
        string result = TimelineProvider.BoundSnapshotName(new string('x', length), out bool truncated);

        truncated.Should().Be(expectedTruncated);
        result.Length.Should().BeLessThanOrEqualTo(TimelineProvider.MaxSnapshotNameChars);
        if (expectedTruncated)
        {
            result.Should().EndWith("...");
        }
    }

    [TestMethod]
    public void ReadSnapshot_HalfWindowAtLimit_Succeeds()
    {
        TimelineResult result = new TimelineProvider().ReadSnapshot(
            Alloc,
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs);

        result.Snapshot.Should().NotBeNull();
    }

    [TestMethod]
    public void ReadSnapshot_HalfWindowAtMinimum_PreservesFractionalGeometry()
    {
        TimelineResult result = new TimelineProvider().ReadSnapshot(
            Alloc,
            atMs: 10.0,
            halfWindowMs: TimelineProvider.MinSnapshotHalfWindowMs);

        result.FromMs.Should().Be(9.99);
        result.ToMs.Should().Be(10.01);
        result.BucketSizeMs.Should().BeApproximately(0.02, 0.0000001);
    }

    [TestMethod]
    public void ReadSnapshot_HalfWindowBelowMinimum_Throws()
    {
        Action act = () => new TimelineProvider().ReadSnapshot(
            Alloc,
            atMs: 10.0,
            halfWindowMs: 0.001);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ReadSnapshot_HalfWindowAboveLimit_Throws()
    {
        Action act = () => new TimelineProvider().ReadSnapshot(
            Alloc,
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs + 1.0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ReadSnapshot_CenterBeyondTraceDuration_Throws()
    {
        Action act = () => new TimelineProvider().ReadSnapshot(
            Alloc,
            atMs: 100_000.0,
            halfWindowMs: TimelineProvider.DefaultSnapshotHalfWindowMs);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void Serialize_MaximumSnapshotRowsAndNames_StaysUnderResponseCeiling()
    {
        string name = new('x', TimelineProvider.MaxSnapshotNameChars);
        SnapshotCountRow[] counts = [.. Enumerable.Range(0, TimelineProvider.SnapshotDetailLimit)
            .Select(_ => new SnapshotCountRow(name, long.MaxValue))];
        SnapshotGcRecord[] collections = [.. Enumerable.Range(0, TimelineProvider.SnapshotDetailLimit)
            .Select(index => new SnapshotGcRecord(index, double.MaxValue, 2, name, name, double.MaxValue))];
        SnapshotCpuMethod[] methods = [.. Enumerable.Range(0, TimelineProvider.SnapshotDetailLimit)
            .Select(_ => new SnapshotCpuMethod(name, long.MaxValue, 100.0))];
        SnapshotAllocationType[] allocations = [.. Enumerable.Range(0, TimelineProvider.SnapshotDetailLimit)
            .Select(_ => new SnapshotAllocationType(name, long.MaxValue, long.MaxValue))];
        SnapshotEventType[] events = [.. Enumerable.Range(0, TimelineProvider.SnapshotDetailLimit)
            .Select(_ => new SnapshotEventType(name, name, long.MaxValue))];
        TimelineSnapshot snapshot = new(
            double.MaxValue,
            new SnapshotGcSummary(int.MaxValue, double.MaxValue, double.MaxValue, collections),
            new SnapshotCpuSummary(long.MaxValue, int.MaxValue, methods),
            new SnapshotExceptionSummary(long.MaxValue, int.MaxValue, counts),
            new SnapshotAllocationSummary(long.MaxValue, long.MaxValue, int.MaxValue, allocations),
            new SnapshotJitSummary(long.MaxValue, int.MaxValue, counts),
            new SnapshotEventSummary(long.MaxValue, int.MaxValue, events),
            true);
        TimelineResult result = new(
            0.0,
            double.MaxValue,
            double.MaxValue,
            1,
            name,
            null,
            null,
            null,
            null,
            null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };

        string json = OutputJson.Serialize(new AnalysisResult<TimelineResult>(result));

        OutputBudget.EstimateTokens(json).Should().BeLessThan(OutputBudget.DefaultCeilingTokens);
    }
}