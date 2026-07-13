// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Output;

[TestClass]
public sealed class SteeringHintsTests
{
    [TestMethod]
    public void ForRanking_WithRows_NudgesToHotFrameCallers()
    {
        RankingResult ranking = new(
            25.0,
            "",
            [
                new RankRow("MyApp.Inner", 16.0, 64.0),
                new RankRow("MyApp.Work", 4.0, 16.0)
            ]);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking);

        // The nudge names the engine verb and the hottest frame, matching the
        // output-contract golden's pinned hint.
        hints.Should().ContainSingle().Which.Should().Be("drill into the hot frame with: callers MyApp.Inner");
    }

    [TestMethod]
    public void ForRanking_Empty_NudgesToWidenScope()
    {
        RankingResult ranking = new(0.0, "", []);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking);

        hints.Should().ContainSingle().Which.Should().Contain("widen the filter");
    }

    [TestMethod]
    public void ForRanking_NonCpu_StaysInTheRankedMetric()
    {
        RankingResult ranking = new(100.0, "", [new RankRow("Allocate", 75.0, 75.0)]);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking, MetricInfo.Allocations);

        hints.Should().ContainSingle().Which.Should().Contain("Allocations ranking");
        hints.Should().ContainSingle().Which.Should().Contain("analyze CPU only");
        hints.Should().NotContain(hint => hint.StartsWith("drill into", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForRanking_TimeScopedCpu_DoesNotDropTheWindow()
    {
        RankingResult ranking = new(25.0, "", [new RankRow("MyApp.Inner", 16.0, 64.0)]);
        ScopeRequest scope = ScopeRequest.Auto.WithTimeWindow(1000.0, 2000.0);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking, MetricInfo.Cpu, scope);

        hints.Should().ContainSingle().Which.Should().Contain("cannot preserve that slice");
        hints.Should().NotContain(hint => hint.StartsWith("drill into", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForRanking_RootAndProcessScopedCpu_PreservesBoth()
    {
        RankingResult ranking = new(25.0, "WorkloadAction", [new RankRow("MyApp.Inner", 16.0, 64.0)]);
        ScopeRequest scope = ScopeRequest.ForProcess("MyApp");

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking, MetricInfo.Cpu, scope);

        hints.Should().ContainSingle().Which.Should().Be(
            "drill into the hot frame with: callers MyApp.Inner --root 'WorkloadAction' --process 'MyApp'");
    }

    [TestMethod]
    public void ForRanking_QuotedScopeValues_ArePowerShellSafe()
    {
        RankingResult ranking = new(25.0, "Work\"load", [new RankRow("MyApp.Inner", 16.0, 64.0)]);
        ScopeRequest scope = ScopeRequest.ForProcess("Jeremy's App");

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking, MetricInfo.Cpu, scope);

        hints.Should().ContainSingle().Which.Should().Be(
            "drill into the hot frame with: callers MyApp.Inner --root 'Work\"load' --process 'Jeremy''s App'");
    }

    [TestMethod]
    public void ForTraceInfo_LegacyNetTrace_LabelsRoutesAsFormatSupported()
    {
        TraceInfo info = new(
            "/t.nettrace", TraceFormat.NetTrace, 100.0, 10, 1.0, [], [],
            TraceCapabilities.AnalysesFor(TraceFormat.NetTrace));

        IReadOnlyList<string> hints = SteeringHints.ForTraceInfo(info);

        hints.Should().Contain(h => h.Contains("format-supported symptom routes", StringComparison.Ordinal)
            && h.Contains("contention", StringComparison.Ordinal));
        hints.Should().NotContain(h => h.Contains("known-enabled symptom routes", StringComparison.Ordinal));
        hints.Should().Contain(h => h.Contains("frequent exceptions -> exceptions", StringComparison.Ordinal));
        hints.Should().Contain(h => h.Contains("slow but low CPU", StringComparison.Ordinal)
            && h.Contains("contention", StringComparison.Ordinal)
            && h.Contains("wait", StringComparison.Ordinal)
            && h.Contains("threadpool", StringComparison.Ordinal));
        hints.Should().Contain(h => h.Contains("high allocation rate or GC pauses", StringComparison.Ordinal)
            && h.Contains("alloc", StringComparison.Ordinal)
            && h.Contains("gcstats", StringComparison.Ordinal));
        hints.Should().NotContain(h => h.Contains("growing memory", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForTraceInfo_CaptureStates_RoutesOnlyKnownEnabledAnalyses()
    {
        IReadOnlyDictionary<string, AnalysisAvailability> analyses =
            TraceCapabilities.AvailabilityFor(
                TraceFormat.NetTrace,
                new Dictionary<string, int> { ["cpu"] = 100, ["exceptions"] = 2 },
                new Dictionary<string, CaptureStatus>
                {
                    ["alloc"] = CaptureStatus.Disabled,
                    ["wait"] = CaptureStatus.Unknown
                });
        TraceInfo info = new(
            "/t.nettrace", TraceFormat.NetTrace, 100.0, 100, 1.0, [], [],
            TraceCapabilities.AnalysesFor(TraceFormat.NetTrace), analyses);

        IReadOnlyList<string> hints = SteeringHints.ForTraceInfo(info);

        hints.Should().Contain(h => h.Contains("known-enabled symptom routes", StringComparison.Ordinal));
        hints.Should().Contain(h => h.Contains("frequent exceptions -> exceptions", StringComparison.Ordinal));
        hints.Should().NotContain(h => h.Contains("high allocation rate", StringComparison.Ordinal));
        hints.Should().NotContain(h => h.Contains("slow but low CPU", StringComparison.Ordinal));
        hints.Should().Contain(h => h.Contains("capture status unknown", StringComparison.Ordinal)
            && h.Contains("wait", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForTraceInfo_Etl_OmitsRoutesTheFormatCannotAnswer()
    {
        TraceInfo info = new(
            "/t.etl", TraceFormat.Etl, 100.0, 10, 1.0, [], [],
            TraceCapabilities.AnalysesFor(TraceFormat.Etl));

        IReadOnlyList<string> hints = SteeringHints.ForTraceInfo(info);

        // An .etl supports thread time, so the blocked route names it; but allocation,
        // the GC report, and exceptions are EventPipe-only, so those routes are omitted.
        hints.Should().Contain(h => h.Contains("threadtime", StringComparison.Ordinal));
        hints.Should().Contain(h => h.Contains("diskio", StringComparison.Ordinal));
        hints.Should().NotContain(h => h.Contains("gcstats", StringComparison.Ordinal));
        hints.Should().NotContain(h => h.Contains("frequent exceptions", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForTraceInfo_PoorSourceResolution_SeparatesMethodNamesFromPdbs()
    {
        TraceInfo info = new(
            "/t.nettrace", TraceFormat.NetTrace, 100.0, 10, 1.0, [], [],
            TraceCapabilities.AnalysesFor(TraceFormat.NetTrace))
        {
            SourceResolution = new SourceResolutionInfo(
                ["/outer"],
                100,
                0,
                [],
                ["GeneratedChild (0/75 mapped)", "MyApp (0/25 mapped)"])
        };

        IReadOnlyList<string> hints = SteeringHints.ForTraceInfo(info);

        hints.Should().Contain(hint =>
            hint.Contains("method-name resolution (100%) is separate from source mapping (0%)", StringComparison.Ordinal)
            && hint.Contains("GeneratedChild", StringComparison.Ordinal)
            && hint.Contains("generated child output", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForCallers_WithNamedCaller_NudgesUpTheStack()
    {
        CallersResult callers = new(
            "Inner",
            16.0,
            64.0,
            25.0,
            [
                new CallerRow("MyApp.Work", 12.0, 75.0),
                new CallerRow("MyApp.Other", 4.0, 25.0)
            ]);

        IReadOnlyList<string> hints = SteeringHints.ForCallers(callers);

        hints.Should().ContainSingle().Which.Should().Be("continue up the stack with: callers MyApp.Work");
    }

    [TestMethod]
    public void ForCallers_DominantCallerIsRoot_NudgesEntryPoint()
    {
        CallersResult callers = new(
            "Main",
            16.0,
            64.0,
            25.0,
            [new CallerRow("<root>", 16.0, 100.0)]);

        IReadOnlyList<string> hints = SteeringHints.ForCallers(callers);

        hints.Should().ContainSingle().Which.Should().Contain("top-level entry point");
    }

    [TestMethod]
    public void ForCallers_Empty_NudgesToWidenScope()
    {
        CallersResult callers = new("Nothing", 0.0, 0.0, 25.0, []);

        IReadOnlyList<string> hints = SteeringHints.ForCallers(callers);

        hints.Should().ContainSingle().Which.Should().Contain("widen the filter");
    }

    [TestMethod]
    public void ForCallers_WithCallees_NudgesUpAndDown()
    {
        CallersResult callers = new(
            "Work",
            20.0,
            80.0,
            25.0,
            [new CallerRow("Program.Main", 20.0, 100.0)],
            [
                new CalleeRow("MyApp.Inner", 16.0, 80.0),
                new CalleeRow("<self>", 4.0, 20.0)
            ]);

        IReadOnlyList<string> hints = SteeringHints.ForCallers(callers);

        // Both directions: up to the top caller and down into the heaviest real callee.
        hints.Should().HaveCount(2);
        hints[0].Should().Be("continue up the stack with: callers Program.Main");
        hints[1].Should().Be("continue down into the callee with: callers MyApp.Inner --callees");
    }

    [TestMethod]
    public void ForCallers_WithRootAndProcess_PreservesScopeInBothDirections()
    {
        CallersResult callers = new(
            "Work",
            20.0,
            80.0,
            25.0,
            [new CallerRow("Program.Main", 20.0, 100.0)],
            [new CalleeRow("MyApp.Inner", 16.0, 80.0)]);
        ScopeRequest scope = ScopeRequest.ForProcess("MyApp");

        IReadOnlyList<string> hints = SteeringHints.ForCallers(callers, "WorkloadAction", scope);

        hints.Should().HaveCount(2);
        hints[0].Should().Be(
            "continue up the stack with: callers Program.Main --root 'WorkloadAction' --process 'MyApp'");
        hints[1].Should().Be(
            "continue down into the callee with: callers MyApp.Inner --callees --root 'WorkloadAction' --process 'MyApp'");
    }

    [TestMethod]
    public void ForCallers_AllProcesses_PreservesWidenedScope()
    {
        CallersResult callers = new(
            "Inner",
            16.0,
            64.0,
            25.0,
            [new CallerRow("MyApp.Work", 16.0, 100.0)]);

        IReadOnlyList<string> hints = SteeringHints.ForCallers(callers, "", ScopeRequest.AllProcesses);

        hints.Should().ContainSingle().Which.Should().Be(
            "continue up the stack with: callers MyApp.Work --all-processes");
    }

    [TestMethod]
    public void ForCallers_CalleesAllSelf_OmitsDownNudge()
    {
        // A leaf focus whose only callee is <self> has nothing to drill down into, so only
        // the up-the-stack nudge is emitted.
        CallersResult callers = new(
            "Inner",
            16.0,
            64.0,
            25.0,
            [new CallerRow("MyApp.Work", 16.0, 100.0)],
            [new CalleeRow("<self>", 16.0, 100.0)]);

        IReadOnlyList<string> hints = SteeringHints.ForCallers(callers);

        hints.Should().ContainSingle().Which.Should().Be("continue up the stack with: callers MyApp.Work");
    }

    [TestMethod]
    public void ForDiff_WithChanges_NudgesToLargestChange()
    {
        RankingDiffResult diff = new(
            20.0,
            30.0,
            10.0,
            [
                new DiffRow("MyApp.Slow", 4.0, 12.0, 8.0),
                new DiffRow("MyApp.Fast", 6.0, 4.0, -2.0)
            ]);

        IReadOnlyList<string> hints = SteeringHints.ForDiff(diff);

        hints.Should().ContainSingle().Which.Should().Be("the largest change is MyApp.Slow; drill into it with: callers MyApp.Slow");
    }

    [TestMethod]
    public void ForDiff_NoChanges_NotesTheMatch()
    {
        RankingDiffResult diff = new(20.0, 20.0, 0.0, []);

        IReadOnlyList<string> hints = SteeringHints.ForDiff(diff);

        hints.Should().ContainSingle().Which.Should().Contain("no frames changed");
    }

    [TestMethod]
    public void ForTimeline_CpuLane_DrillsBusiestWindowWithScopedRanking()
    {
        TimelineResult timeline = new(
            0.0, 100.0, 20.0, 5, null,
            Gc: null,
            Cpu:
            [
                new CpuBucket(0, null),
                new CpuBucket(0, null),
                new CpuBucket(50, "MyApp.Hot"),
                new CpuBucket(1, null),
                new CpuBucket(0, null)
            ],
            Exceptions: null, Alloc: null, Jit: null);

        IReadOnlyList<string> hints = SteeringHints.ForTimeline(timeline);

        // The busiest CPU bucket names its window and the ranking scoped to it; an
        // unscoped timeline carries no --process on the drill.
        hints.Should().ContainSingle().Which.Should()
            .Be("busiest CPU window is bucket 2 (40-60 ms); scope a ranking with: rank --metric cpu --time 40,60");
    }

    [TestMethod]
    public void ForTimeline_ProcessScoped_CarriesProcessIntoDrillHint()
    {
        TimelineResult timeline = new(
            0.0, 100.0, 20.0, 5, "HotLoopBench",
            Gc: null,
            Cpu:
            [
                new CpuBucket(0, null),
                new CpuBucket(0, null),
                new CpuBucket(50, "MyApp.Hot"),
                new CpuBucket(1, null),
                new CpuBucket(0, null)
            ],
            Exceptions: null, Alloc: null, Jit: null);

        IReadOnlyList<string> hints = SteeringHints.ForTimeline(timeline);

        // A scoped timeline propagates its process into the drill so the follow-up
        // ranking stays on the same tree rather than re-auto-scoping.
        hints.Should().ContainSingle().Which.Should().EndWith("--time 40,60 --process HotLoopBench");
    }

    [TestMethod]
    public void ForTimeline_SubMillisecondBuckets_KeepsPreciseDrillWindow()
    {
        // A short capture divided into many buckets yields sub-millisecond bucket widths;
        // the drill window must keep its precision rather than rounding to a degenerate or
        // shifted whole-millisecond range that would select the wrong slice.
        TimelineResult timeline = new(
            0.0, 1.5, 0.3, 5, null,
            Gc: null,
            Cpu:
            [
                new CpuBucket(0, null),
                new CpuBucket(0, null),
                new CpuBucket(50, "MyApp.Hot"),
                new CpuBucket(1, null),
                new CpuBucket(0, null)
            ],
            Exceptions: null, Alloc: null, Jit: null);

        IReadOnlyList<string> hints = SteeringHints.ForTimeline(timeline);

        hints.Should().ContainSingle().Which.Should().Contain("--time 0.6,0.9");
    }

    [TestMethod]
    public void ForTimeline_Empty_NudgesToWiden()
    {
        TimelineResult timeline = new(
            0.0, 100.0, 20.0, 5, null,
            Gc: null, Cpu: null, Exceptions: null, Alloc: null, Jit: null);

        IReadOnlyList<string> hints = SteeringHints.ForTimeline(timeline);

        hints.Should().ContainSingle().Which.Should().Contain("widen the window");
    }

    [TestMethod]
    public void ForRanking_Null_ThrowsArgumentNull()
    {
        Action act = () => SteeringHints.ForRanking(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void ForCallers_Null_ThrowsArgumentNull()
    {
        Action act = () => SteeringHints.ForCallers(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void ForDiff_Null_ThrowsArgumentNull()
    {
        Action act = () => SteeringHints.ForDiff(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void ForTimeline_Null_ThrowsArgumentNull()
    {
        Action act = () => SteeringHints.ForTimeline(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
