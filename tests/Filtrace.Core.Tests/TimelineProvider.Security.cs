// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using Filtrace.Output;
using Microsoft.Diagnostics.Tracing.Parsers;

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
    public void BoundSnapshotName_ControlCharacters_AreEscapedWithDistinctIdentity()
    {
        const string unsafeName = "line\r\n\u001b[31m";
        const string literalLookalike = "line\\u000D\\u000A\\u001B[31m";

        string escaped = TimelineProvider.BoundSnapshotName(unsafeName, out bool escapedChanged);
        string literal = TimelineProvider.BoundSnapshotName(literalLookalike, out bool literalChanged);

        escapedChanged.Should().BeTrue();
        literalChanged.Should().BeFalse();
        escaped.Any(char.IsControl).Should().BeFalse();
        escaped.Should().Contain("\\u000D\\u000A\\u001B").And.NotBe(literal);
        escaped.Length.Should().BeLessThanOrEqualTo(TimelineProvider.MaxSnapshotNameChars);
    }

    [TestMethod]
    public void TryGetBoundedEventNames_RepeatedLongMetadata_CompletesPromptlyAndReusesNames()
    {
        Dictionary<int, (string Provider, string Name, bool Truncated)> cache = [];
        string provider = new('p', 1_000_000);
        string name = new('n', 1_000_000);

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimelineProvider.TryGetBoundedEventNames(
            cache,
            1,
            provider,
            name,
            out string firstProvider,
            out string firstName,
            out bool firstTruncated).Should().BeTrue();
        bool allSucceeded = true;
        string repeatedProvider = "";
        string repeatedName = "";
        bool repeatedTruncated = false;
        for (int i = 0; i < 10_000; i++)
        {
            allSucceeded &= TimelineProvider.TryGetBoundedEventNames(
                cache,
                1,
                provider,
                name,
                out repeatedProvider,
                out repeatedName,
                out repeatedTruncated);
        }

        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        allSucceeded.Should().BeTrue();
        cache.Should().ContainSingle();
        firstTruncated.Should().BeTrue();
        ReferenceEquals(repeatedProvider, firstProvider).Should().BeTrue();
        ReferenceEquals(repeatedName, firstName).Should().BeTrue();
        repeatedTruncated.Should().Be(firstTruncated);
    }

    [TestMethod]
    public void TryGetBoundedEventNames_AtCapacity_RejectsNewMetadata()
    {
        Dictionary<int, (string Provider, string Name, bool Truncated)> cache = [];
        for (int i = 0; i < TimelineProvider.MaxSnapshotRetainedKeysPerFamily; i++)
        {
            TimelineProvider.TryGetBoundedEventNames(
                cache,
                i,
                "provider",
                $"event-{i}",
                out _,
                out _,
                out _).Should().BeTrue();
        }

        TimelineProvider.TryGetBoundedEventNames(
            cache,
            TimelineProvider.MaxSnapshotRetainedKeysPerFamily,
            "overflow-provider",
            "overflow-event",
            out _,
            out _,
            out _).Should().BeFalse();
        cache.Should().HaveCount(TimelineProvider.MaxSnapshotRetainedKeysPerFamily);
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

        TimelineProvider.AddPauseStartBounded(starts, (1, 2), 15.0, 20.0)
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

        TimelineProvider.AddPauseStartBounded(starts, (2, 1), 10.0, 20.0)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.CapacityExceeded);
        starts.Should().HaveCount(TimelineProvider.MaxSnapshotRetainedKeysPerFamily);
    }

    [TestMethod]
    public void AddPauseStartBounded_AtWindowEnd_RetainsStart()
    {
        Dictionary<(int ProcessId, int ThreadId), double> starts = [];

        TimelineProvider.AddPauseStartBounded(starts, (1, 2), 10.0, 10.0)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.Added);
        starts.Should().ContainKey((1, 2)).WhoseValue.Should().Be(10.0);
    }

    [TestMethod]
    public void AddPauseStartBounded_AfterWindowEnd_DoesNotRetainStart()
    {
        Dictionary<(int ProcessId, int ThreadId), double> starts = [];

        TimelineProvider.AddPauseStartBounded(starts, (1, 2), 10.0001, 10.0)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.AfterWindow);
        starts.Should().BeEmpty();
    }

    [TestMethod]
    public void MatchGcPauseRestart_MissingStartInsideWindow_ReturnsMissingStart()
    {
        Dictionary<(int ProcessId, int ThreadId), double> starts = [];

        TimelineProvider.MatchGcPauseRestart(starts, (1, 2), 10.0, 5.0, 15.0, out _)
            .Should().Be(TimelineProvider.GcRestartResult.MissingStart);
    }

    [TestMethod]
    public void MatchGcPauseRestart_MissingStartAfterWindow_IsOutsideWindow()
    {
        Dictionary<(int ProcessId, int ThreadId), double> starts = [];

        TimelineProvider.MatchGcPauseRestart(starts, (1, 2), 20.0, 5.0, 15.0, out _)
            .Should().Be(TimelineProvider.GcRestartResult.OutsideWindow);
    }

    [TestMethod]
    public void MatchGcPauseRestart_NonMonotonicPair_IsInvalidAndPreservesStart()
    {
        Dictionary<(int ProcessId, int ThreadId), double> starts = new()
        {
            [(1, 2)] = 10.0
        };

        TimelineProvider.MatchGcPauseRestart(starts, (1, 2), 9.0, 5.0, 15.0, out _)
            .Should().Be(TimelineProvider.GcRestartResult.InvalidPair);
        starts.Should().ContainKey((1, 2)).WhoseValue.Should().Be(10.0);
    }

    [TestMethod]
    public void MatchGcPauseRestart_StartBeforeWindowAndRestartAfterWindow_Completes()
    {
        Dictionary<(int ProcessId, int ThreadId), double> starts = new()
        {
            [(1, 2)] = 4.0
        };

        TimelineProvider.MatchGcPauseRestart(starts, (1, 2), 16.0, 5.0, 15.0, out double pauseStart)
            .Should().Be(TimelineProvider.GcRestartResult.Completed);
        pauseStart.Should().Be(4.0);
        starts.Should().BeEmpty();
    }

    [TestMethod]
    public void MergeOverlapping_NestedInterval_KeepsEnclosingPauseDiscoverable()
    {
        TimelineProvider.GcPauseInterval[] intervals =
        [
            new(1, 0.0, 100.0),
            new(1, 50.0, 60.0)
        ];

        TimelineProvider.GcPauseInterval[] merged = TimelineProvider.MergeOverlapping(intervals);

        merged.Should().ContainSingle();
        merged[0].Contains(80.0).Should().BeTrue();
    }

    [TestMethod]
    public void MergeOverlapping_DisjointIntervals_AreRetainedInStartOrder()
    {
        TimelineProvider.GcPauseInterval[] intervals =
        [
            new(1, 30.0, 40.0),
            new(1, 10.0, 20.0)
        ];

        TimelineProvider.GcPauseInterval[] merged = TimelineProvider.MergeOverlapping(intervals);

        merged.Should().HaveCount(2);
        merged[0].StartMs.Should().Be(10.0);
        merged[1].StartMs.Should().Be(30.0);
        merged[0].Contains(25.0).Should().BeFalse();
    }

    [TestMethod]
    public void AggregateGcPauses_OverlappingSameProcess_CountsUnionOnce()
    {
        TimelineProvider.GcPauseInterval[] intervals =
        [
            new(1, 0.0, 60.0),
            new(1, 50.0, 100.0)
        ];

        TimelineProvider.GcPauseAggregate aggregate = TimelineProvider.AggregateGcPauses(intervals, 0.0, 100.0);

        aggregate.IntervalsByProcess.Should().ContainSingle();
        aggregate.IntervalsByProcess[1].Should().ContainSingle();
        aggregate.TotalPauseMs.Should().Be(100.0);
        aggregate.MaxPauseMs.Should().Be(100.0);
    }

    [TestMethod]
    public void AggregateGcPauses_OverlappingDifferentProcesses_SumsEachProcess()
    {
        TimelineProvider.GcPauseInterval[] intervals =
        [
            new(1, 0.0, 100.0),
            new(2, 0.0, 100.0)
        ];

        TimelineProvider.GcPauseAggregate aggregate = TimelineProvider.AggregateGcPauses(intervals, 0.0, 100.0);

        aggregate.IntervalsByProcess.Should().HaveCount(2);
        aggregate.TotalPauseMs.Should().Be(200.0);
        aggregate.MaxPauseMs.Should().Be(100.0);
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

        warning.Should().Contain("incomplete").And.Contain("may be inaccurate");
        AnalysisDiagnostic.FromWarning(warning).Severity.Should().Be("warning");
    }

    [TestMethod]
    public void IsGcRestartEventIdentity_RequiresTypeProviderAndExactName()
    {
        TimelineProvider.IsGcRestartEventIdentity(
            expectedType: true,
            ClrTraceEventParser.ProviderGuid,
            "GC/RestartEEStop").Should().BeTrue();
        TimelineProvider.IsGcRestartEventIdentity(
            expectedType: false,
            ClrTraceEventParser.ProviderGuid,
            "GC/RestartEEStop").Should().BeFalse();
        TimelineProvider.IsGcRestartEventIdentity(
            expectedType: true,
            Guid.Empty,
            "GC/RestartEEStop").Should().BeFalse();
        TimelineProvider.IsGcRestartEventIdentity(
            expectedType: true,
            ClrTraceEventParser.ProviderGuid,
            "Custom/RestartEEStop").Should().BeFalse();
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
