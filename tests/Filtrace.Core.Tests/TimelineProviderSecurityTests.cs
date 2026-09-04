// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using Filtrace.Output;
using FastTrace.Parsers;
using FastTrace.Parsers.Clr;

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class TimelineProviderSecurityTests
{
    private static string Alloc => Path.Join(AppContext.BaseDirectory, "Fixtures", "alloc.nettrace");

    private static TimelineProvider.PauseIdentity Pause(int processInstanceIndex, int threadInstanceIndex) =>
        new(processInstanceIndex, threadInstanceIndex);

    [TestMethod]
    [DataRow(5.0, true)]
    [DataRow(10.0, true)]
    [DataRow(15.0, true)]
    [DataRow(4.99, false)]
    [DataRow(15.01, false)]
    [DataRow(double.NaN, false)]
    [DataRow(double.PositiveInfinity, false)]
    [DataRow(double.NegativeInfinity, false)]
    public void IsTimelineEventInWindow_RequiresFiniteInclusiveTimestamp(double timestamp, bool expected)
    {
        TimelineProvider.IsTimelineTimestampInWindow(timestamp, startMs: 5.0, endMs: 15.0)
            .Should().Be(expected);
    }

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
    public void TryGetBoundedEventNames_DistinctTemplateIdentities_KeepDistinctNames()
    {
        Dictionary<object, (string Provider, string Name, bool Truncated)> cache =
            new(ReferenceEqualityComparer.Instance);

        object firstTemplate = new();
        object secondTemplate = new();

        TimelineProvider.TryGetBoundedEventNames(
            cache,
            firstTemplate,
            "provider",
            "first",
            out _,
            out string firstName,
            out _).Should().BeTrue();

        TimelineProvider.TryGetBoundedEventNames(
            cache,
            secondTemplate,
            "provider",
            "second",
            out _,
            out string secondName,
            out _).Should().BeTrue();

        cache.Should().HaveCount(2);
        firstName.Should().Be("first");
        secondName.Should().Be("second");
    }

    [TestMethod]
    public void TryGetBoundedCpuMethod_RepeatedLongMethod_CompletesPromptlyAndResolvesOnce()
    {
        Dictionary<int, (string Name, bool Truncated)> cache = [];
        string method = new('m', 1_000_000);
        List<int> resolveCalls = [];
        (string Method, List<int> Calls) state = (method, resolveCalls);

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimelineProvider.TryGetBoundedCpuMethod(
            cache,
            1,
            state,
            static value =>
            {
                value.Calls.Add(1);
                return value.Method;
            },
            out string firstMethod,
            out bool firstTruncated).Should().BeTrue();

        bool allSucceeded = true;
        string repeatedMethod = "";
        bool repeatedTruncated = false;
        for (int i = 0; i < 10_000; i++)
        {
            allSucceeded &= TimelineProvider.TryGetBoundedCpuMethod(
                cache,
                1,
                state,
                static value =>
                {
                    value.Calls.Add(1);
                    return value.Method;
                },
                out repeatedMethod,
                out repeatedTruncated);
        }

        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        allSucceeded.Should().BeTrue();
        resolveCalls.Should().ContainSingle();
        cache.Should().ContainSingle();
        firstTruncated.Should().BeTrue();
        ReferenceEquals(repeatedMethod, firstMethod).Should().BeTrue();
        repeatedTruncated.Should().Be(firstTruncated);
    }

    [TestMethod]
    public void TryGetBoundedCpuMethod_AtCapacity_RejectsBeforeResolving()
    {
        Dictionary<int, (string Name, bool Truncated)> cache = [];
        for (int i = 0; i < TimelineProvider.MaxSnapshotRetainedKeysPerFamily; i++)
        {
            cache.Add(i, ($"method-{i}", false));
        }

        List<bool> resolutionCalls = [];
        TimelineProvider.TryGetBoundedCpuMethod(
            cache,
            TimelineProvider.MaxSnapshotRetainedKeysPerFamily,
            resolutionCalls,
            static calls =>
            {
                calls.Add(item: true);
                return "overflow-method";
            },
            out _,
            out _).Should().BeFalse();

        resolutionCalls.Should().BeEmpty();
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
    public void ReadSnapshot_FractionalCenterBeyondWirePrecision_Throws()
    {
        Action act = () => new TimelineProvider().ReadSnapshot(Alloc, atMs: 10.005, halfWindowMs: 2.0);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("atMs")
            .WithMessage("*0.01 millisecond increments*");
    }

    [TestMethod]
    public void ReadSnapshot_FractionalHalfWindowBeyondWirePrecision_Throws()
    {
        Action act = () => new TimelineProvider().ReadSnapshot(Alloc, atMs: 10.0, halfWindowMs: 0.015);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("halfWindowMs")
            .WithMessage("*0.01 millisecond increments*");
    }

    [TestMethod]
    public void ResolveSnapshotBounds_ClippedFractionalTraceEnd_RoundsUp()
    {
        (double startMs, double endMs) = TimelineProvider.ResolveSnapshotBounds(
            atMs: 10.0,
            halfWindowMs: 0.01,
            traceEndMs: 10.004);

        startMs.Should().Be(9.99);
        endMs.Should().Be(10.01).And.BeGreaterThan(10.004);
        TimelineProvider.IsSnapshotGeometryRepresentable(startMs).Should().BeTrue();
        TimelineProvider.IsSnapshotGeometryRepresentable(endMs).Should().BeTrue();
    }

    [TestMethod]
    public void ResolveSnapshotBounds_UnclippedWindow_PreservesRequestedGeometry()
    {
        (double startMs, double endMs) = TimelineProvider.ResolveSnapshotBounds(
            atMs: 10.0,
            halfWindowMs: 0.01,
            traceEndMs: 20.0);

        startMs.Should().Be(9.99);
        endMs.Should().Be(10.01);
    }

    [TestMethod]
    public void ResolveSnapshotBounds_ClippedRepresentableTraceEnd_PreservesEnd()
    {
        (_, double endMs) = TimelineProvider.ResolveSnapshotBounds(
            atMs: 10.0,
            halfWindowMs: 0.01,
            traceEndMs: 10.0);

        endMs.Should().Be(10.0);
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
    public void AddPauseStartBounded_DuplicateGcStateDoesNotOverwriteAndMarksIncomplete()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [Pause(1, 2)] = new(10.0, IsGc: true)
        };

        TimelineProvider.AddPauseStartBounded(
            starts,
            Pause(1, 2),
            15.0,
            20.0,
            GCSuspendEEReason.SuspendForShutdown,
            out bool gcStateIncomplete)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.Duplicate);

        starts[Pause(1, 2)].Should().Be(new TimelineProvider.PendingPauseStart(10.0, IsGc: true));
        gcStateIncomplete.Should().BeTrue();
    }

    [TestMethod]
    public void AddPauseStartBounded_DuplicateNonGcStateDoesNotMarkGcIncomplete()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [Pause(1, 2)] = new(10.0, IsGc: false)
        };

        TimelineProvider.AddPauseStartBounded(
            starts,
            Pause(1, 2),
            15.0,
            20.0,
            GCSuspendEEReason.SuspendForDebugger,
            out bool gcStateIncomplete)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.Duplicate);

        gcStateIncomplete.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void AddPauseStartBounded_AtCapacity_RejectsNewStartAndMarksOnlyGcIncomplete(
        bool isGc,
        bool expectedIncomplete)
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = [];
        for (int i = 0; i < TimelineProvider.MaxSnapshotRetainedKeysPerFamily; i++)
        {
            starts[Pause(1, i)] = new(i, IsGc: false);
        }

        TimelineProvider.AddPauseStartBounded(
            starts,
            Pause(2, 1),
            10.0,
            20.0,
            isGc ? GCSuspendEEReason.SuspendForGC : GCSuspendEEReason.SuspendForCodePitching,
            out bool gcStateIncomplete)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.CapacityExceeded);

        gcStateIncomplete.Should().Be(expectedIncomplete);
        starts.Should().HaveCount(TimelineProvider.MaxSnapshotRetainedKeysPerFamily);
    }

    [TestMethod]
    public void AddPauseStartBounded_AtWindowEnd_RetainsStart()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = [];

        TimelineProvider.AddPauseStartBounded(
            starts,
            Pause(1, 2),
            10.0,
            10.0,
            GCSuspendEEReason.SuspendForGCPrep,
            out _)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.Added);

        starts.Should().ContainKey(Pause(1, 2)).WhoseValue
            .Should().Be(new TimelineProvider.PendingPauseStart(10.0, IsGc: true));
    }

    [TestMethod]
    public void AddPauseStartBounded_AfterWindowEnd_DoesNotRetainStart()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = [];

        TimelineProvider.AddPauseStartBounded(
            starts,
            Pause(1, 2),
            10.0001,
            10.0,
            GCSuspendEEReason.SuspendForGC,
            out _)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.AfterWindow);

        starts.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void AddPauseStartBounded_NonFiniteTimestampRejectsAndMarksOnlyGcIncomplete(
        bool isGc,
        bool expectedIncomplete)
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = [];
        GCSuspendEEReason reason = isGc
            ? GCSuspendEEReason.SuspendForGC
            : GCSuspendEEReason.SuspendForShutdown;

        TimelineProvider.AddPauseStartBounded(
            starts,
            Pause(1, 2),
            double.NaN,
            10.0,
            reason,
            out bool gcStateIncomplete)
            .Should().Be(TimelineProvider.BoundedPauseStartResult.InvalidTimestamp);

        gcStateIncomplete.Should().Be(expectedIncomplete);
        starts.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(GCSuspendEEReason.SuspendForGC, 10.0, true)]
    [DataRow(GCSuspendEEReason.SuspendForGCPrep, 10.0, true)]
    [DataRow(GCSuspendEEReason.SuspendForGC, 10.0001, false)]
    [DataRow(GCSuspendEEReason.SuspendForGC, double.NaN, true)]
    [DataRow(GCSuspendEEReason.SuspendForGC, double.PositiveInfinity, true)]
    [DataRow(GCSuspendEEReason.SuspendForShutdown, 10.0, false)]
    public void IsMissingPauseIdentityGcIncomplete_AppliesWindowAndReasonGate(
        GCSuspendEEReason reason,
        double timestamp,
        bool expected)
    {
        TimelineProvider.IsMissingPauseIdentityGcIncomplete(reason, timestamp, windowEndMs: 10.0)
            .Should().Be(expected);
    }

    [TestMethod]
    public void MatchPauseRestart_MissingStartDoesNotClaimGcProvenance()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = [];

        TimelineProvider.MatchPauseRestart(starts, Pause(1, 2), 10.0, 5.0, 15.0, out TimelineProvider.PendingPauseStart start)
            .Should().Be(TimelineProvider.PauseRestartResult.MissingStart);

        start.IsGc.Should().BeFalse();
    }

    [TestMethod]
    public void MatchPauseRestart_DifferentProcessInstance_DoesNotConsumeStart()
    {
        TimelineProvider.PauseIdentity earlierProcess = new(1, 2);
        TimelineProvider.PauseIdentity laterProcess = new(3, 2);
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [earlierProcess] = new(5.0, IsGc: true)
        };

        TimelineProvider.MatchPauseRestart(
            starts,
            laterProcess,
            10.0,
            5.0,
            15.0,
            out _).Should().Be(TimelineProvider.PauseRestartResult.MissingStart);

        starts.Should().ContainKey(earlierProcess);
    }

    [TestMethod]
    public void MatchPauseRestart_DifferentThreadInstance_DoesNotConsumeStart()
    {
        TimelineProvider.PauseIdentity earlierThread = new(1, 2);
        TimelineProvider.PauseIdentity laterThread = new(1, 3);
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [earlierThread] = new(5.0, IsGc: true)
        };

        TimelineProvider.MatchPauseRestart(
            starts,
            laterThread,
            10.0,
            5.0,
            15.0,
            out _).Should().Be(TimelineProvider.PauseRestartResult.MissingStart);

        starts.Should().ContainKey(earlierThread);
    }

    [TestMethod]
    public void IsUnknownPauseEvidence_ClassifiesOnlyInWindowMissingStarts()
    {
        TimelineProvider.IsUnknownPauseEvidence(
            TimelineProvider.PauseRestartResult.MissingStart,
            5.0,
            5.0,
            15.0).Should().BeTrue();

        TimelineProvider.IsUnknownPauseEvidence(
            TimelineProvider.PauseRestartResult.MissingStart,
            15.0,
            5.0,
            15.0).Should().BeTrue();

        TimelineProvider.IsUnknownPauseEvidence(
            TimelineProvider.PauseRestartResult.MissingStart,
            4.99,
            5.0,
            15.0).Should().BeFalse();

        TimelineProvider.IsUnknownPauseEvidence(
            TimelineProvider.PauseRestartResult.MissingStart,
            15.01,
            5.0,
            15.0).Should().BeFalse();

        TimelineProvider.IsUnknownPauseEvidence(
            TimelineProvider.PauseRestartResult.CompletedNonGc,
            10.0,
            5.0,
            15.0).Should().BeFalse();
    }

    [TestMethod]
    public void GetSnapshotUnknownPauseWarning_ReasonlessRestart_IsExplicit()
    {
        TimelineSnapshot snapshot = new(
            0.0,
            new SnapshotGcSummary(0, 0.0, 0.0, []),
            new SnapshotCpuSummary(0, 0, []),
            new SnapshotExceptionSummary(0, 0, []),
            new SnapshotAllocationSummary(0, 0, 0, []),
            new SnapshotJitSummary(0, 0, []),
            new SnapshotEventSummary(0, 0, []),
                NamesTruncated: false)
        {
            UnknownPauseDataIncomplete = true
        };

        TimelineResult result = new(0.0, 1.0, 1.0, 1, Process: null, Gc: null, Cpu: null, Exceptions: null, Alloc: null, Jit: null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };

        string warning = TimelineProvider.GetSnapshotUnknownPauseWarning(result)!;

        warning.Should().Contain("incomplete").And.Contain("reason").And.Contain("unknown");
        AnalysisDiagnostic.FromWarning(warning).Severity.Should().Be("warning");
        OutputJson.Serialize(new AnalysisResult<TimelineResult>(result))
            .Should().Contain("\"unknownPauseDataIncomplete\":true");

        TimelineSnapshot completeSnapshot = snapshot with { UnknownPauseDataIncomplete = false };
        TimelineResult completeResult = result with { Snapshot = completeSnapshot };
        OutputJson.Serialize(new AnalysisResult<TimelineResult>(completeResult))
            .Should().NotContain("unknownPauseDataIncomplete");
    }

    [TestMethod]
    public void MatchPauseRestart_NonGcPair_IsConsumedWithoutGcEvidence()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [Pause(1, 2)] = new(8.0, IsGc: false)
        };

        TimelineProvider.MatchPauseRestart(starts, Pause(1, 2), 10.0, 5.0, 15.0, out TimelineProvider.PendingPauseStart start)
            .Should().Be(TimelineProvider.PauseRestartResult.CompletedNonGc);

        start.IsGc.Should().BeFalse();
        starts.Should().BeEmpty();
    }

    [TestMethod]
    public void MatchPauseRestart_NonMonotonicGcPair_IsInvalidAndPreservesStart()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [Pause(1, 2)] = new(10.0, IsGc: true)
        };

        TimelineProvider.MatchPauseRestart(starts, Pause(1, 2), 9.0, 5.0, 15.0, out TimelineProvider.PendingPauseStart start)
            .Should().Be(TimelineProvider.PauseRestartResult.InvalidPair);

        start.IsGc.Should().BeTrue();
        starts.Should().ContainKey(Pause(1, 2));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void MatchPauseRestart_NonFiniteRestartWithGcStart_IsInvalidAndPreservesStart(double timestamp)
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [Pause(1, 2)] = new(10.0, IsGc: true)
        };

        TimelineProvider.MatchPauseRestart(
            starts,
            Pause(1, 2),
            timestamp,
            5.0,
            15.0,
            out TimelineProvider.PendingPauseStart start)
            .Should().Be(TimelineProvider.PauseRestartResult.InvalidPair);

        start.IsGc.Should().BeTrue();
        starts.Should().ContainKey(Pause(1, 2));
    }

    [TestMethod]
    public void MatchPauseRestart_GcStartBeforeWindowAndRestartAfterWindow_Completes()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [Pause(1, 2)] = new(4.0, IsGc: true)
        };

        TimelineProvider.MatchPauseRestart(starts, Pause(1, 2), 16.0, 5.0, 15.0, out TimelineProvider.PendingPauseStart pauseStart)
            .Should().Be(TimelineProvider.PauseRestartResult.CompletedGc);

        pauseStart.TimestampMs.Should().Be(4.0);
        starts.Should().BeEmpty();
    }

    [TestMethod]
    public void MatchPauseRestart_GcPairBeforeWindow_IsOutsideWindow()
    {
        Dictionary<TimelineProvider.PauseIdentity, TimelineProvider.PendingPauseStart> starts = new()
        {
            [Pause(1, 2)] = new(3.0, IsGc: true)
        };

        TimelineProvider.MatchPauseRestart(
            starts,
            Pause(1, 2),
            4.0,
            5.0,
            15.0,
            out TimelineProvider.PendingPauseStart pauseStart)
            .Should().Be(TimelineProvider.PauseRestartResult.OutsideWindow);

        pauseStart.IsGc.Should().BeTrue();
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

        aggregate.IntervalsByProcessInstance.Should().ContainSingle();
        aggregate.IntervalsByProcessInstance[1].Should().ContainSingle();
        aggregate.TotalPauseMs.Should().Be(100.0);
        aggregate.MaxPauseMs.Should().Be(100.0);
    }

    [TestMethod]
    public void AggregateGcPauses_OverlappingDifferentProcessInstances_SumsEachInstance()
    {
        TimelineProvider.GcPauseInterval[] intervals =
        [
            new(1, 0.0, 100.0),
            new(2, 0.0, 100.0)
        ];

        TimelineProvider.GcPauseAggregate aggregate = TimelineProvider.AggregateGcPauses(intervals, 0.0, 100.0);

        aggregate.IntervalsByProcessInstance.Should().HaveCount(2);
        aggregate.TotalPauseMs.Should().Be(200.0);
        aggregate.MaxPauseMs.Should().Be(100.0);
    }

    [TestMethod]
    public void SnapshotGcCollector_ActiveCollectionsAtCapacity_RejectsAdditionalIdentity()
    {
        TimelineProvider.SnapshotGcCollector collector = new(startMs: 0.0, endMs: 1.0);
        for (int collectionNumber = 0;
            collectionNumber < TimelineProvider.MaxSnapshotRetainedKeysPerFamily;
            collectionNumber++)
        {
            collector.ObserveStart(
                1,
                9,
                collectionNumber,
                0.5,
                2,
                GCType.NonConcurrentGC,
                GCReason.Induced);
        }

        collector.DetailTruncated.Should().BeFalse();
        collector.ObserveStart(
            1,
            9,
            TimelineProvider.MaxSnapshotRetainedKeysPerFamily,
            0.5,
            2,
            GCType.NonConcurrentGC,
            GCReason.Induced);

        SnapshotGcSummary result = collector.Build(
            TimelineProvider.AggregateGcPauses([], 0.0, 1.0),
            out _);

        collector.DetailTruncated.Should().BeTrue();
        result.CollectionCount.Should().Be(TimelineProvider.MaxSnapshotRetainedKeysPerFamily);
    }

    [TestMethod]
    public void SnapshotGcCollector_PendingPausesAtCapacity_RejectsAdditionalIdentity()
    {
        TimelineProvider.SnapshotGcCollector collector = new(startMs: 0.0, endMs: 200.0);
        collector.ObserveStart(1, 9, 1, 100.0, 2, GCType.NonConcurrentGC, GCReason.Induced);
        collector.ObserveStart(2, 10, 2, 100.0, 2, GCType.NonConcurrentGC, GCReason.Induced);
        for (int threadInstanceIndex = 0;
            threadInstanceIndex < TimelineProvider.MaxSnapshotRetainedKeysPerFamily;
            threadInstanceIndex++)
        {
            collector.ObserveSuspend(Pause(1, threadInstanceIndex), 9, 99.0);
        }

        collector.DetailTruncated.Should().BeFalse();
        collector.ObserveSuspend(Pause(2, 1), 10, 99.0);
        collector.ObserveRestart(
            Pause(1, TimelineProvider.MaxSnapshotRetainedKeysPerFamily - 1),
            9,
            101.0);

        collector.ObserveRestart(Pause(2, 1), 10, 101.0);
        collector.ObserveEnd(1, 9, 1, 102.0);
        collector.ObserveEnd(2, 10, 2, 102.0);
        TimelineProvider.GcPauseInterval retainedPause = new(1, 99.0, 101.0);
        SnapshotGcSummary result = collector.Build(
            TimelineProvider.AggregateGcPauses([retainedPause], 0.0, 200.0),
            out _);

        collector.DetailTruncated.Should().BeTrue();
        result.CollectionCount.Should().Be(2);
        result.Collections.Single(collection => collection.Number == 1).PauseMs.Should().Be(2.0);
        result.Collections.Single(collection => collection.Number == 2).PauseMs.Should().Be(0.0);
    }

    [TestMethod]
    public void SnapshotGcCollector_BlockingPauseCrossesWindow_RetainsFullDetail()
    {
        TimelineProvider.SnapshotGcCollector collector = new(startMs: 20.63, endMs: 20.67);
        TimelineProvider.GcPauseInterval pause = new(1, 20.64, 21.06);

        collector.ObserveStart(1, 9, 1, 20.69, 2, GCType.NonConcurrentGC, GCReason.Induced);
        collector.ObserveEnd(1, 9, 1, 21.05);
        collector.ObservePause(9, pause);
        TimelineProvider.GcPauseAggregate aggregate = TimelineProvider.AggregateGcPauses([pause], 20.63, 20.67);

        SnapshotGcSummary result = collector.Build(aggregate, out bool namesTruncated);

        namesTruncated.Should().BeFalse();
        result.CollectionCount.Should().Be(1);
        result.TotalPauseMs.Should().Be(0.03);
        result.Collections.Should().ContainSingle();
        result.Collections[0].Should().Be(
            new SnapshotGcRecord(1, 20.69, 2, "NonConcurrentGC", "Induced", 0.42));
    }

    [TestMethod]
    public void SnapshotGcCollector_BackgroundFinalPauseBeforeEnd_AttributesBothCollections()
    {
        TimelineProvider.SnapshotGcCollector collector = new(startMs: 100.0, endMs: 200.0);
        TimelineProvider.GcPauseInterval initialBackgroundPause = new(1, 9.0, 11.0);
        TimelineProvider.GcPauseInterval foregroundPause = new(1, 119.0, 126.0);
        TimelineProvider.GcPauseInterval finalBackgroundPause = new(1, 190.0, 210.0);

        collector.ObserveStart(1, 9, 1, 10.0, 2, GCType.BackgroundGC, GCReason.Induced);
        collector.ObservePause(9, initialBackgroundPause);
        collector.ObserveStart(1, 9, 2, 120.0, 0, GCType.NonConcurrentGC, GCReason.AllocSmall);
        collector.ObserveEnd(1, 9, 2, 125.0);
        collector.ObservePause(9, foregroundPause);
        collector.ObservePause(9, finalBackgroundPause);
        collector.ObserveEnd(1, 9, 1, 195.0);
        TimelineProvider.GcPauseInterval[] pauses =
            [initialBackgroundPause, foregroundPause, finalBackgroundPause];

        TimelineProvider.GcPauseAggregate aggregate = TimelineProvider.AggregateGcPauses(pauses, 100.0, 200.0);

        SnapshotGcSummary result = collector.Build(aggregate, out bool namesTruncated);

        namesTruncated.Should().BeFalse();
        result.CollectionCount.Should().Be(2);
        result.TotalPauseMs.Should().Be(17.0);
        result.MaxPauseMs.Should().Be(10.0);
        result.Collections.Should().Equal(
            new SnapshotGcRecord(1, 10.0, 2, "BackgroundGC", "Induced", 22.0),
            new SnapshotGcRecord(2, 120.0, 0, "NonConcurrentGC", "AllocSmall", 7.0));
    }

    [TestMethod]
    public void SnapshotGcCollector_MultipleClrs_AttributesPausesByClrInstance()
    {
        TimelineProvider.SnapshotGcCollector collector = new(startMs: 0.0, endMs: 100.0);
        TimelineProvider.GcPauseInterval firstPause = new(1, 9.0, 11.0);
        TimelineProvider.GcPauseInterval secondPause = new(1, 19.0, 23.0);

        collector.ObserveStart(1, 9, 1, 10.0, 2, GCType.NonConcurrentGC, GCReason.Induced);
        collector.ObserveStart(1, 10, 1, 20.0, 2, GCType.NonConcurrentGC, GCReason.Induced);
        collector.ObservePause(9, firstPause);
        collector.ObserveEnd(1, 9, 1, 11.0);
        collector.ObservePause(10, secondPause);
        collector.ObserveEnd(1, 10, 1, 23.0);
        TimelineProvider.GcPauseAggregate aggregate = TimelineProvider.AggregateGcPauses(
            [firstPause, secondPause],
            0.0,
            100.0);

        SnapshotGcSummary result = collector.Build(aggregate, out bool namesTruncated);

        namesTruncated.Should().BeFalse();
        result.CollectionCount.Should().Be(2);
        result.Collections.Should().Equal(
            new SnapshotGcRecord(1, 20.0, 2, "NonConcurrentGC", "Induced", 4.0),
            new SnapshotGcRecord(1, 10.0, 2, "NonConcurrentGC", "Induced", 2.0));
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
                NamesTruncated: false)
        {
            GcPauseDataIncomplete = true
        };

        TimelineResult result = new(0.0, 1.0, 1.0, 1, Process: null, Gc: null, Cpu: null, Exceptions: null, Alloc: null, Jit: null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };

        string warning = TimelineProvider.GetSnapshotGcPauseWarning(result)!;

        warning.Should().Contain("incomplete").And.Contain("malformed").And.Contain("may be inaccurate");
        AnalysisDiagnostic.FromWarning(warning).Severity.Should().Be("warning");
    }

    [TestMethod]
    public void IsEeRestartEventIdentity_RequiresTypeProviderAndExactName()
    {
        TimelineProvider.IsEeRestartEventIdentity(
            expectedType: true,
            ClrTraceEventParser.ProviderGuid,
            "GC/RestartEEStop").Should().BeTrue();

        TimelineProvider.IsEeRestartEventIdentity(
            expectedType: false,
            ClrTraceEventParser.ProviderGuid,
            "GC/RestartEEStop").Should().BeFalse();

        TimelineProvider.IsEeRestartEventIdentity(
            expectedType: true,
            Guid.Empty,
            "GC/RestartEEStop").Should().BeFalse();

        TimelineProvider.IsEeRestartEventIdentity(
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
                NamesTruncated: true);

        TimelineResult result = new(
            0.0,
            double.MaxValue,
            double.MaxValue,
            1,
            name,
                Gc: null,
                Cpu: null,
                Exceptions: null,
                Alloc: null,
                Jit: null)
        {
            Mode = "snapshot",
            Snapshot = snapshot with { DetailTruncated = true }
        };

        string json = OutputJson.Serialize(new AnalysisResult<TimelineResult>(result));
        string warning = TimelineProvider.GetSnapshotDetailWarning(result)!;

        OutputBudget.EstimateTokens(json).Should().BeLessThan(OutputBudget.DefaultCeilingTokens);
        json.Should().Contain("\"detailTruncated\":true");
        warning.Should().Contain("1024-key-per-family")
            .And.Contain("Aggregate event, CPU-sample, exception, allocation-tick/byte, and JIT-compilation totals remain complete")
            .And.Contain("CPU-method and raw-event-type row counts and percentages may be undercounted");

        AnalysisDiagnostic.FromWarning(warning).Code.Should().Be(AnalysisDiagnosticCodes.TruncatedOutput);
    }
}
