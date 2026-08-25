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
            result.Should().Contain("...#");
        }
    }

    [TestMethod]
    public void BoundSnapshotName_SharedPrefix_UsesDistinctStableSuffixes()
    {
        string prefix = new('x', TimelineProvider.MaxSnapshotNameChars + 20);

        string first = TimelineProvider.BoundSnapshotName($"{prefix}a", out bool firstTruncated);
        string second = TimelineProvider.BoundSnapshotName($"{prefix}b", out bool secondTruncated);

        firstTruncated.Should().BeTrue();
        secondTruncated.Should().BeTrue();
        first.Should().HaveLength(TimelineProvider.MaxSnapshotNameChars).And.NotBe(second);
        TimelineProvider.BoundSnapshotName($"{prefix}a", out _).Should().Be(first);
    }

    [TestMethod]
    public void BoundSnapshotName_CutAtSurrogatePair_PreservesValidUtf16()
    {
        string value = $"{new string('x', 219)}\U0001F600{new string('y', 100)}";

        string result = TimelineProvider.BoundSnapshotName(value, out bool truncated);

        truncated.Should().BeTrue();
        result.Any(char.IsSurrogate).Should().BeFalse();
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
    public void TallyBounded_AtLimitRetainsExistingAndRejectsNewKey()
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        for (int i = 0; i < TimelineProvider.MaxSnapshotRetainedKeysPerFamily; i++)
        {
            TimelineProvider.TallyBounded(counts, $"key-{i}").Should().BeTrue();
        }

        TimelineProvider.TallyBounded(counts, "key-0").Should().BeTrue();
        TimelineProvider.TallyBounded(counts, "overflow").Should().BeFalse();
        counts.Should().HaveCount(TimelineProvider.MaxSnapshotRetainedKeysPerFamily);
        counts["key-0"].Should().Be(2);
        counts.Should().NotContainKey("overflow");
    }

    [TestMethod]
    public void TallyAllocationBounded_AtLimitRetainsExistingAndRejectsNewType()
    {
        Dictionary<string, (long Count, long Bytes)> allocations = new(StringComparer.Ordinal);
        for (int i = 0; i < TimelineProvider.MaxSnapshotRetainedKeysPerFamily; i++)
        {
            TimelineProvider.TallyAllocationBounded(allocations, $"type-{i}", 10).Should().BeTrue();
        }

        TimelineProvider.TallyAllocationBounded(allocations, "type-0", 5).Should().BeTrue();
        TimelineProvider.TallyAllocationBounded(allocations, "overflow", 20).Should().BeFalse();
        allocations.Should().HaveCount(TimelineProvider.MaxSnapshotRetainedKeysPerFamily);
        allocations["type-0"].Should().Be((2, 15));
        allocations.Should().NotContainKey("overflow");
    }

    [TestMethod]
    public void AddAllocationBytes_AtLimit_Succeeds()
    {
        TimelineProvider.AddAllocationBytes(long.MaxValue - 1, 1).Should().Be(long.MaxValue);
    }

    [TestMethod]
    public void AddAllocationBytes_AboveLimit_ThrowsInvalidData()
    {
        Action act = () => TimelineProvider.AddAllocationBytes(long.MaxValue, 1);

        act.Should().Throw<InvalidDataException>().WithMessage("*64-bit total*");
    }

    [TestMethod]
    public void TallyAllocationBounded_PerTypeBytesAboveLimit_ThrowsInvalidData()
    {
        Dictionary<string, (long Count, long Bytes)> allocations = new(StringComparer.Ordinal)
        {
            ["type"] = (1, long.MaxValue)
        };

        Action act = () => TimelineProvider.TallyAllocationBounded(allocations, "type", 1);

        act.Should().Throw<InvalidDataException>().WithMessage("*64-bit total*");
    }

    [TestMethod]
    public void AddPauseStartBounded_DuplicateDoesNotOverwrite()
    {
        Dictionary<(int ProcessId, int ThreadId), double> starts = new()
        {
            [(1, 2)] = 10.0
        };

        TimelineProvider.AddPauseStartBounded(starts, (1, 2), 15.0)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.Duplicate);
        starts[(1, 2)].Should().Be(10.0);
    }

    [TestMethod]
    public void AddPauseStartBounded_AtCapacity_RejectsNewStart()
    {
        Dictionary<(int ProcessId, int ThreadId), double> starts = [];
        for (int i = 0; i < TimelineProvider.MaxSnapshotRetainedKeysPerFamily; i++)
        {
            starts[(1, i)] = i;
        }

        TimelineProvider.AddPauseStartBounded(starts, (2, 1), 10.0)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.CapacityExceeded);
        starts.Should().HaveCount(TimelineProvider.MaxSnapshotRetainedKeysPerFamily);
    }

    [TestMethod]
    public void GetSnapshotGcPauseWarning_IncompleteEvidence_IsExplicit()
    {
        TimelineSnapshot snapshot = new(
            0.0,
            new SnapshotGcSummary(0, 0.0, 0.0, []),
            new SnapshotCpuSummary(0, 0, []),
            new SnapshotExceptionSummary(0, 0, []),
            new SnapshotAllocationSummary(0, 0, 0, []),
            new SnapshotJitSummary(0, 0, []),
            new SnapshotEventSummary(0, 0, []),
            false)
        {
            GcPauseDataIncomplete = true
        };
        TimelineResult result = new(0.0, 1.0, 1.0, 1, null, null, null, null, null, null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };

        string warning = TimelineProvider.GetSnapshotGcPauseWarning(result)!;

        warning.Should().Contain("incomplete").And.Contain("may be understated");
        AnalysisDiagnostic.FromWarning(warning).Severity.Should().Be("warning");
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
            Snapshot = snapshot with { DetailTruncated = true }
        };

        string json = OutputJson.Serialize(new AnalysisResult<TimelineResult>(result));
        string warning = TimelineProvider.GetSnapshotDetailWarning(result)!;

        OutputBudget.EstimateTokens(json).Should().BeLessThan(OutputBudget.DefaultCeilingTokens);
        json.Should().Contain("\"detailTruncated\":true");
        warning.Should().Contain("1024-key-per-family")
            .And.Contain("Aggregate event, CPU-sample, exception, allocation-tick/byte, and JIT-compilation totals remain complete");
        AnalysisDiagnostic.FromWarning(warning).Code.Should().Be(AnalysisDiagnosticCodes.TruncatedOutput);
    }
}