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
    public void ForTraceInfo_NetTrace_RoutesSymptomsToSupportedAnalyses()
    {
        TraceInfo info = new(
            "/t.nettrace", TraceFormat.NetTrace, 100.0, 10, 1.0, [], [],
            TraceCapabilities.AnalysesFor(TraceFormat.NetTrace));

        IReadOnlyList<string> hints = SteeringHints.ForTraceInfo(info);

        hints.Should().Contain(h => h.Contains("this trace can answer", StringComparison.Ordinal)
            && h.Contains("contention", StringComparison.Ordinal));
        hints.Should().Contain(h => h.Contains("frequent exceptions -> exceptions", StringComparison.Ordinal));
        hints.Should().Contain(h => h.Contains("slow but low CPU", StringComparison.Ordinal)
            && h.Contains("contention", StringComparison.Ordinal)
            && h.Contains("wait", StringComparison.Ordinal)
            && h.Contains("threadpool", StringComparison.Ordinal));
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
