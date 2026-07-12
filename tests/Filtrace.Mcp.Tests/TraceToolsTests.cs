// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Nodes;
using ModelContextProtocol;
using Filtrace.Output;
using Filtrace.Server;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Mcp;

[TestClass]
public sealed class TraceToolsTests
{
    private const string Speedscope = "folding.speedscope.json";
    private const string Activity = "activity.nettrace";
    private const string Alloc = "alloc.nettrace";
    private const string Exceptions = "exceptions.nettrace";
    private const string Jit = "jit.nettrace";
    private const string ThreadPoolTrace = "threadpool.nettrace";
    private const string DiskIoTrace = "diskio.etl";
    private const string Etw = "etw.etl";
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromSeconds(10);

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string CopyToTemp(string fixture, out string tempDirectory)
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"filtrace-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string destination = Path.Combine(tempDirectory, fixture);
        File.Copy(FixturePath(fixture), destination);
        return destination;
    }

    // Every tool returns the typed AnalysisResult envelope; the SDK serializes it (with
    // an output schema and structured content) from the typed shape, so the unit tests
    // assert on the object directly rather than re-parsing JSON.
    private static void AssertEnvelope<T>(AnalysisResult<T> envelope)
    {
        envelope.SchemaVersion.Should().Be(4);
        envelope.Warnings.Should().NotBeNull();
        envelope.Hints.Should().NotBeNull();
        envelope.Result.Should().NotBeNull();
    }

    [TestMethod]
    public void Info_Speedscope_ReturnsFormatSampleCountAndThreads()
    {
        TraceStore store = new();

        AnalysisResult<TraceInfoView> envelope = TraceTools.Info(store, FixturePath(Speedscope));

        AssertEnvelope(envelope);
        TraceInfoView result = envelope.Result;
        result.Path.Should().EndWith(Speedscope);
        result.Format.Should().Be("Speedscope");
        result.SampleCount.Should().Be(4);
        result.SymbolResolutionRate.Should().BeInRange(0.0, 1.0);
        result.Threads.Should().NotBeEmpty();
        result.EtlxCacheState.Should().BeNull();
    }

    [TestMethod]
    public async Task ConcurrentSameTrace_InfoTwoRanksAndLines_AllSucceedWithOneCache()
    {
        TraceStore store = new();
        string path = CopyToTemp(Activity, out string tempDirectory);
        using Barrier startBarrier = new(participantCount: 5);
        try
        {
            Task<AnalysisResult<TraceInfoView>> infoTask = Task.Run(async () =>
            {
                startBarrier.SignalAndWait(SynchronizationTimeout).Should().BeTrue();
                return await TraceTools.InfoAsync(store, path);
            });
            Task<AnalysisResult<RankingResult>> firstRankTask = Task.Run(async () =>
            {
                startBarrier.SignalAndWait(SynchronizationTimeout).Should().BeTrue();
                return await TraceTools.RankAsync(store, path);
            });
            Task<AnalysisResult<RankingResult>> secondRankTask = Task.Run(async () =>
            {
                startBarrier.SignalAndWait(SynchronizationTimeout).Should().BeTrue();
                return await TraceTools.RankAsync(store, path, measure: "inclusive");
            });
            Task<AnalysisResult<LineRankingResult>> linesTask = Task.Run(async () =>
            {
                startBarrier.SignalAndWait(SynchronizationTimeout).Should().BeTrue();
                return await TraceTools.LinesAsync(store, path);
            });
            startBarrier.SignalAndWait(SynchronizationTimeout).Should().BeTrue();

            await Task.WhenAll(infoTask, firstRankTask, secondRankTask, linesTask);

            AnalysisResult<TraceInfoView> info = await infoTask;
            info.Result.EtlxCacheState.Should().BeOneOf("converted", "waited", "hit");
            (await firstRankTask).Result.Rows.Should().NotBeEmpty();
            (await secondRankTask).Result.Rows.Should().NotBeEmpty();
            (await linesTask).Result.Should().NotBeNull();
            File.Exists(TraceConverter.EtlxPathFor(path)).Should().BeTrue();
            Directory.EnumerateFiles(tempDirectory, "*.new").Should().BeEmpty();
            Directory.EnumerateFiles(tempDirectory, ".filtrace-etlx-*").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Info_Payload_HasNoWarningsMember()
    {
        // The quality warnings travel only in the envelope's warnings channel; the typed
        // trace_info payload has no warnings member, so the duplication the old string
        // contract risked is now impossible by construction.
        typeof(TraceInfoView).GetProperty("Warnings").Should().BeNull();
    }

    [TestMethod]
    public void Rank_SpeedscopeSelf_RanksFramesAndEmitsHint()
    {
        TraceStore store = new();

        AnalysisResult<RankingResult> envelope = TraceTools.Rank(store, FixturePath(Speedscope));

        AssertEnvelope(envelope);
        envelope.Hints.Should().NotBeEmpty();
        envelope.Result.Rows.Should().NotBeEmpty();
        envelope.Result.ContributingRecordCount.Should().Be(4);
        envelope.Warnings.Should().NotContain(
            warning => warning.Contains("periodic CPU records", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Rank_SpeedscopeInclusive_RanksFrames()
    {
        TraceStore store = new();

        AnalysisResult<RankingResult> envelope = TraceTools.Rank(store, FixturePath(Speedscope), measure: "inclusive");

        envelope.Result.Rows.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Rank_ThinPeriodicCpuRoot_WarnsUsingContributingRecords()
    {
        TraceStore store = new();

        AnalysisResult<RankingResult> whole = TraceTools.Rank(store, FixturePath(Activity));
        AnalysisResult<RankingResult> scoped = TraceTools.Rank(
            store,
            FixturePath(Activity),
            root: "ActivityLoop.EmitActivities");

        whole.Result.ContributingRecordCount.Should().BeGreaterThanOrEqualTo(
            ContributingRecordQuality.DefaultMinimumMethodRecords);
        scoped.Result.ContributingRecordCount.Should().Be(179);
        scoped.Warnings.Should().Contain(
            warning => warning.Contains("179 periodic CPU records", StringComparison.Ordinal)
                && warning.Contains("200", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Rank_BenchmarkPresetWithoutWorkloadFrame_DropsAllSamples()
    {
        TraceStore store = new();

        AnalysisResult<RankingResult> envelope =
            TraceTools.Rank(store, FixturePath(Speedscope), benchmark: true);

        envelope.Result.ScopeWeight.Should().Be(0.0);
        envelope.Result.Rows.Should().BeEmpty();
    }

    [TestMethod]
    public void Rank_RootAndBenchmarkBothSet_Throws()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Rank(
            store, FixturePath(Speedscope), root: "MyApp.Work", benchmark: true);

        act.Should().Throw<McpException>().WithMessage("*only one of 'root' and 'benchmark'*");
    }

    [TestMethod]
    public void Rank_AmbiguousRoot_ReportsDefinitionsDepthsAndSelection()
    {
        TraceStore store = new();

        AnalysisResult<RankingResult> envelope =
            TraceTools.Rank(store, FixturePath(Speedscope), root: "MyApp");

        envelope.Warnings.Should().Contain(
            warning => warning.Contains("root 'MyApp'", StringComparison.Ordinal)
                && warning.Contains("outermost", StringComparison.Ordinal)
                && warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        envelope.Warnings.Should().Contain(
            warning => warning.Contains("MyApp.Work", StringComparison.Ordinal)
                && warning.Contains("depth", StringComparison.Ordinal));
        envelope.Warnings.Should().Contain(
            warning => warning.Contains("MyApp.Inner", StringComparison.Ordinal)
                && warning.Contains("depth", StringComparison.Ordinal));
        envelope.Warnings.Should().Contain(
            warning => warning.Contains("MyApp.Other", StringComparison.Ordinal)
                && warning.Contains("depth", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Rank_AmbiguousRoot_CapsReportedDepths()
    {
        const int recursiveDepth = 12;
        string tracePath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.speedscope.json");
        JsonArray events = [];
        for (int depth = 0; depth < recursiveDepth; depth++)
        {
            events.Add(new JsonObject { ["type"] = "O", ["frame"] = 0, ["at"] = 0 });
        }

        events.Add(new JsonObject { ["type"] = "O", ["frame"] = 1, ["at"] = 0 });
        events.Add(new JsonObject { ["type"] = "C", ["frame"] = 1, ["at"] = 1 });
        for (int depth = 0; depth < recursiveDepth; depth++)
        {
            events.Add(new JsonObject { ["type"] = "C", ["frame"] = 0, ["at"] = 1 });
        }

        JsonObject speedscope = new()
        {
            ["$schema"] = "https://www.speedscope.app/file-format-schema.json",
            ["shared"] = new JsonObject
            {
                ["frames"] = new JsonArray
                {
                    new JsonObject { ["name"] = "Recursive.Frame" },
                    new JsonObject { ["name"] = "CPU_TIME" }
                }
            },
            ["profiles"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "evented",
                    ["name"] = "Worker",
                    ["unit"] = "milliseconds",
                    ["startValue"] = 0,
                    ["endValue"] = 1,
                    ["events"] = events
                }
            }
        };

        try
        {
            File.WriteAllText(tracePath, speedscope.ToJsonString());
            TraceStore store = new();

            AnalysisResult<RankingResult> envelope =
                TraceTools.Rank(store, tracePath, root: "Recursive.Frame");

            envelope.Warnings.Should().Contain(
                warning => warning.Contains(
                    "zero-based depths [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, ... (2 more)]",
                    StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }
        }
    }

    [TestMethod]
    public void Rank_AllocMetric_ReadsTheAllocationView()
    {
        TraceStore store = new();

        AnalysisResult<RankingResult> envelope = TraceTools.Rank(store, FixturePath(Alloc), metric: "alloc");

        AssertEnvelope(envelope);
        envelope.Result.Rows.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Rank_ExceptionsMetric_ReadsTheExceptionView()
    {
        TraceStore store = new();

        AnalysisResult<RankingResult> envelope = TraceTools.Rank(store, FixturePath(Exceptions), metric: "exceptions");

        AssertEnvelope(envelope);
        envelope.Result.Rows.Should().NotBeEmpty();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Rank_ThreadTimeMetric_ReadsTheEtlView()
    {
        TraceStore store = new();

        AnalysisResult<RankingResult> envelope = TraceTools.Rank(store, FixturePath(Etw), metric: "threadtime");

        AssertEnvelope(envelope);
        envelope.Result.Rows.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Rank_UnknownMetric_ThrowsWithSelectorList()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Rank(store, FixturePath(Speedscope), metric: "ipc");

        act.Should().Throw<McpException>().WithMessage("*Unknown metric 'ipc'*cpu*");
    }

    [TestMethod]
    public void Rank_UnknownMeasure_Throws()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Rank(store, FixturePath(Speedscope), measure: "average");

        act.Should().Throw<McpException>().WithMessage("*Unknown measure 'average'*");
    }

    [TestMethod]
    public void Rank_NonPositiveTop_Throws()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Rank(store, FixturePath(Speedscope), top: 0);

        act.Should().Throw<McpException>().WithMessage("*top must be 1 or greater*");
    }

    [TestMethod]
    public void Rank_InvalidFoldPattern_ThrowsMcpExceptionNamingThePattern()
    {
        TraceStore store = new();

        // A malformed user-supplied fold regex is a usage error, not an internal failure:
        // it must surface as a clean tool error that names the offending pattern.
        Action act = () => TraceTools.Rank(store, FixturePath(Speedscope), fold: ["("]);

        act.Should().Throw<McpException>().WithMessage("*Invalid fold pattern*");
    }

    [TestMethod]
    public void Rank_TimeWindow_ScopesTheRankingAndNotesTheWindow()
    {
        TraceStore store = new();

        // A window spanning the whole capture keeps every sample and notes the scope, so
        // the assertion does not depend on the fixture's exact timing.
        AnalysisResult<RankingResult> envelope = TraceTools.Rank(
            store, FixturePath(Alloc), metric: "alloc", time: "0,100000");

        AssertEnvelope(envelope);
        envelope.Result.Rows.Should().NotBeEmpty();
        envelope.Warnings.Should().Contain(w =>
            w.Contains("Scoped to the [0, 100000] ms window", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Rank_TimeWindow_IsUniversalUnlikeTheActivityScope()
    {
        TraceStore store = new();

        // The activity scope is cpu-only and throws with a non-cpu metric; the time window
        // applies to every metric, so the same alloc+scope combination must succeed.
        Action activityOnAlloc = () => TraceTools.Rank(store, FixturePath(Alloc), metric: "alloc", activity: "Order");
        activityOnAlloc.Should().Throw<McpException>();

        Action timeOnAlloc = () => TraceTools.Rank(store, FixturePath(Alloc), metric: "alloc", time: "0,100000");
        timeOnAlloc.Should().NotThrow();
    }

    [TestMethod]
    public void Rank_MalformedTimeWindow_ThrowsMcpException()
    {
        TraceStore store = new();

        // The window is parsed before any read, so a malformed value is a clean tool error
        // regardless of the fixture.
        Action act = () => TraceTools.Rank(store, FixturePath(Speedscope), time: "500");

        act.Should().Throw<McpException>().WithMessage("*time window must be 'start,end'*");
    }

    [TestMethod]
    public void Rank_TimeWindow_IgnoredForSpeedscopeWithAWarning()
    {
        TraceStore store = new();

        // A speedscope timeline is not in milliseconds, so the window is ignored - but with
        // a warning rather than silently returning the whole trace.
        AnalysisResult<RankingResult> envelope = TraceTools.Rank(
            store, FixturePath(Speedscope), time: "0,100");

        AssertEnvelope(envelope);
        envelope.Warnings.Should().Contain(w =>
            w.Contains("not applied to a speedscope", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Callers_Speedscope_ReturnsCallerBreakdown()
    {
        TraceStore store = new();

        AnalysisResult<CallersResult> envelope = TraceTools.Callers(store, FixturePath(Speedscope), frame: "");

        AssertEnvelope(envelope);
        envelope.Result.Callers.Should().NotBeNull();
    }


    [TestMethod]
    public void Callers_BenchmarkPresetWithoutWorkloadFrame_DropsAllSamples()
    {
        TraceStore store = new();

        AnalysisResult<CallersResult> envelope =
            TraceTools.Callers(store, FixturePath(Speedscope), frame: "MyApp.Work", benchmark: true);

        envelope.Result.ScopeWeight.Should().Be(0.0);
        envelope.Result.Callers.Should().BeEmpty();
    }

    [TestMethod]
    public void Callers_AmbiguousFocus_ReportsDefinitionsAndDeepestSelection()
    {
        TraceStore store = new();

        AnalysisResult<CallersResult> envelope =
            TraceTools.Callers(store, FixturePath(Speedscope), frame: "MyApp");

        envelope.Warnings.Should().Contain(
            warning => warning.Contains("frame 'MyApp'", StringComparison.Ordinal)
                && warning.Contains("deepest", StringComparison.Ordinal)
                && warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        envelope.Warnings.Should().Contain(
            warning => warning.Contains("MyApp.Work", StringComparison.Ordinal));
        envelope.Warnings.Should().Contain(
            warning => warning.Contains("MyApp.Inner", StringComparison.Ordinal));
        envelope.Warnings.Should().Contain(
            warning => warning.Contains("MyApp.Other", StringComparison.Ordinal));
    }
    [TestMethod]
    public void Callers_WithoutCallees_LeavesCalleesNull()
    {
        TraceStore store = new();

        AnalysisResult<CallersResult> envelope =
            TraceTools.Callers(store, FixturePath(Speedscope), frame: "MyApp.Work");

        AssertEnvelope(envelope);
        envelope.Result.Callees.Should().BeNull("callees are only computed when requested");
    }

    [TestMethod]
    public void Callers_WithCallees_ReturnsBothDirections()
    {
        TraceStore store = new();

        AnalysisResult<CallersResult> envelope =
            TraceTools.Callers(store, FixturePath(Speedscope), frame: "MyApp.Work", callees: true);

        AssertEnvelope(envelope);
        envelope.Result.Callees.Should().NotBeNull();
        envelope.Result.Callees!.Should().Contain(c => c.Callee == "MyApp.Inner");
    }

    [TestMethod]
    public void Lines_SpeedscopeWithoutLineData_ReturnsEmptyRanking()
    {
        TraceStore store = new();

        // Speedscope carries no per-frame source locations, so the line ranking is empty.
        AnalysisResult<LineRankingResult> envelope = TraceTools.Lines(store, FixturePath(Speedscope));

        AssertEnvelope(envelope);
        envelope.Result.Rows.Should().BeEmpty();
        envelope.Result.AttributedRecordCount.Should().Be(0);
        envelope.Result.UnattributedRecordCount.Should().Be(4);
        envelope.Warnings.Should().NotContain(
            warning => warning.Contains("periodic CPU records", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Lines_InvalidFoldPattern_ThrowsMcpException()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Lines(store, FixturePath(Speedscope), fold: ["("]);

        act.Should().Throw<McpException>().WithMessage("*Invalid fold pattern*");
    }

    [TestMethod]
    public void Heatmap_SpeedscopeWithoutLineData_ReturnsEmptyMap()
    {
        TraceStore store = new();

        AnalysisResult<SourceHeatmapResult> envelope =
            TraceTools.Heatmap(store, FixturePath(Speedscope), file: "ExtGlob.cs");

        AssertEnvelope(envelope);
        envelope.Result.Lines.Should().BeEmpty();
    }

    [TestMethod]
    public void Heatmap_InvalidFoldPattern_ThrowsMcpException()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Heatmap(store, FixturePath(Speedscope), file: "ExtGlob.cs", fold: ["("]);

        act.Should().Throw<McpException>().WithMessage("*Invalid fold pattern*");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Lines_ProcessScope_OnMachineWideCapture_Warns()
    {
        TraceStore store = new();

        // The lines tool now scopes a multi-process ETW capture to a named process
        // tree; the scope notice surfaces in the envelope warnings. Reading an .etl is
        // Windows-only, so this is guarded.
        AnalysisResult<LineRankingResult> envelope =
            TraceTools.Lines(store, FixturePath(Etw), process: "HotLoopBench-Job");

        AssertEnvelope(envelope);
        envelope.Warnings.Should().Contain(w => w.Contains("Scoped to the"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Callers_ProcessScope_OnMachineWideCapture_Warns()
    {
        TraceStore store = new();

        AnalysisResult<CallersResult> envelope =
            TraceTools.Callers(store, FixturePath(Etw), frame: "", process: "HotLoopBench-Job");

        AssertEnvelope(envelope);
        envelope.Warnings.Should().Contain(w => w.Contains("Scoped to the"));
    }

    [TestMethod]
    public void Lines_ProcessScopeOnSpeedscope_IsHarmlessNoOp()
    {
        TraceStore store = new();

        // Speedscope is single-process, so a process selector is a no-op: the tool
        // still succeeds and returns the (empty, no line data) ranking.
        AnalysisResult<LineRankingResult> envelope =
            TraceTools.Lines(store, FixturePath(Speedscope), process: "anything");

        AssertEnvelope(envelope);
        envelope.Result.Rows.Should().BeEmpty();
    }

    [TestMethod]
    public void Info_MissingFile_ThrowsMcpException()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Info(store, FixturePath("does-not-exist.nettrace"));

        act.Should().Throw<McpException>();
    }

    [TestMethod]
    public void Diff_SameTraceTwice_ReturnsZeroScopeDelta()
    {
        TraceStore store = new();
        string path = FixturePath(Speedscope);

        // Diffing a trace against itself: every frame matches, so the scope total is
        // unchanged and no frame shows a delta.
        AnalysisResult<RankingDiffResult> envelope = TraceTools.Diff(store, path, path);

        AssertEnvelope(envelope);
        envelope.Result.ScopeDelta.Should().Be(0.0);
        envelope.Result.Rows.Should().OnlyContain(row => row.Delta == 0.0);
    }

    [TestMethod]
    public void Diff_UnknownMeasure_Throws()
    {
        TraceStore store = new();
        string path = FixturePath(Speedscope);

        Action act = () => TraceTools.Diff(store, path, path, measure: "average");

        act.Should().Throw<McpException>().WithMessage("*Unknown measure 'average'*");
    }

    [TestMethod]
    public void Diff_InclusiveMeasure_SameTraceTwice_ReturnsZeroScopeDelta()
    {
        TraceStore store = new();
        string path = FixturePath(Speedscope);

        // The inclusive branch ranks both sides with InclusiveTime; diffing a trace
        // against itself still yields no change.
        AnalysisResult<RankingDiffResult> envelope = TraceTools.Diff(store, path, path, measure: "inclusive");

        AssertEnvelope(envelope);
        envelope.Result.ScopeDelta.Should().Be(0.0);
    }

    [TestMethod]
    public void Gc_NetTrace_ReturnsAggregateSummary()
    {
        AnalysisResult<GcStatsResult> envelope = TraceTools.Gc(FixturePath(Alloc));

        AssertEnvelope(envelope);
        envelope.Result.GcCount.Should().BeGreaterThan(0);
        envelope.Result.Gcs.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Gc_NonNetTraceInput_ThrowsMcpException()
    {
        // The GC report parses the EventPipe format; an .etl or speedscope is rejected
        // up front by the extension guardrail.
        Action act = () => TraceTools.Gc(FixturePath(Speedscope));

        act.Should().Throw<McpException>().WithMessage("*requires a .nettrace*");
    }

    [TestMethod]
    public void Gc_NonPositiveTop_Throws()
    {
        Action act = () => TraceTools.Gc(FixturePath(Alloc), top: 0);

        act.Should().Throw<McpException>().WithMessage("*top must be 1 or greater*");
    }

    [TestMethod]
    public void Gc_Top_CapsPerCollectionDetail()
    {
        // The aggregate summary always reflects every collection, but the per-collection
        // detail list is capped to 'top' so a long trace cannot blow the output budget.
        AnalysisResult<GcStatsResult> envelope = TraceTools.Gc(FixturePath(Alloc), top: 1);

        envelope.Result.Gcs.Count.Should().BeLessThanOrEqualTo(1);
    }

    [TestMethod]
    public void Timeline_NetTrace_ReturnsAlignedLanesAndHint()
    {
        AnalysisResult<TimelineResult> envelope = TraceTools.Timeline(FixturePath(Alloc), buckets: 20);

        AssertEnvelope(envelope);
        envelope.Hints.Should().NotBeEmpty();
        envelope.Result.BucketCount.Should().Be(20);
        envelope.Result.Gc.Should().NotBeNull().And.HaveCount(20);
        envelope.Result.Gc!.Sum(static b => b.Count).Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Timeline_LanesSelector_LimitsLanes()
    {
        AnalysisResult<TimelineResult> envelope = TraceTools.Timeline(FixturePath(Alloc), lanes: "gc");

        envelope.Result.Gc.Should().NotBeNull();
        envelope.Result.Cpu.Should().BeNull();
        envelope.Result.Alloc.Should().BeNull();
    }

    [TestMethod]
    public void Timeline_UnknownLane_ThrowsMcpException()
    {
        Action act = () => TraceTools.Timeline(FixturePath(Alloc), lanes: "bogus");

        act.Should().Throw<McpException>().WithMessage("*Unknown lane 'bogus'*");
    }

    [TestMethod]
    public void Timeline_BucketsBelowMinimum_ClampsAndWarns()
    {
        AnalysisResult<TimelineResult> envelope = TraceTools.Timeline(FixturePath(Alloc), buckets: 1);

        envelope.Result.BucketCount.Should().Be(5);
        envelope.Warnings.Should().Contain(w => w.Contains("below the minimum"));
    }

    [TestMethod]
    public void Timeline_BadTimeWindow_ThrowsMcpException()
    {
        Action act = () => TraceTools.Timeline(FixturePath(Alloc), time: "not-a-window");

        act.Should().Throw<McpException>();
    }

    [TestMethod]
    public void Timeline_Speedscope_ThrowsMcpException()
    {
        // A speedscope export carries only CPU stacks, not the event stream the timeline
        // reads; the dual-format guardrail rejects it up front.
        Action act = () => TraceTools.Timeline(FixturePath(Speedscope));

        act.Should().Throw<McpException>().WithMessage("*requires a .nettrace*");
    }

    [TestMethod]
    public void ThreadPool_NetTrace_ReturnsAdjustmentSummary()
    {
        AnalysisResult<ThreadPoolResult> envelope = TraceTools.ThreadPool(FixturePath(ThreadPoolTrace));

        AssertEnvelope(envelope);
        envelope.Result.AdjustmentCount.Should().BeGreaterThan(0);
        envelope.Result.StarvationCount.Should().BeGreaterThan(0);
        envelope.Result.AdjustmentsByReason.Should().NotBeEmpty();
    }

    [TestMethod]
    public void ThreadPool_NonNetTraceInput_ThrowsMcpException()
    {
        // The thread-pool report parses the EventPipe format; an .etl or speedscope is
        // rejected up front by the extension guardrail.
        Action act = () => TraceTools.ThreadPool(FixturePath(Speedscope));

        act.Should().Throw<McpException>().WithMessage("*requires a .nettrace*");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void DiskIo_EtlTrace_ReturnsByFileReport()
    {
        AnalysisResult<DiskIoResult> envelope = TraceTools.DiskIo(FixturePath(DiskIoTrace));

        AssertEnvelope(envelope);
        envelope.Result.WriteCount.Should().BeGreaterThan(0);
        envelope.Result.Files.Should().NotBeEmpty();
    }

    [TestMethod]
    public void DiskIo_NonEtlInput_ThrowsMcpException()
    {
        // The disk I/O report reads kernel ETW events; a .nettrace or speedscope is
        // rejected up front by the format guardrail.
        Action act = () => TraceTools.DiskIo(FixturePath(Alloc));

        act.Should().Throw<McpException>().WithMessage("*requires a Windows ETW .etl*");
    }

    [TestMethod]
    public void DiskIo_NonPositiveTop_Throws()
    {
        Action act = () => TraceTools.DiskIo(FixturePath(DiskIoTrace), top: 0);

        act.Should().Throw<McpException>().WithMessage("*top must be 1 or greater*");
    }

    [TestMethod]
    public void Processes_Speedscope_ListsTheSingleProcess()
    {
        TraceStore store = new();

        AnalysisResult<ProcessListResult> envelope = TraceTools.Processes(store, FixturePath(Speedscope));

        AssertEnvelope(envelope);
        envelope.Result.Processes.Should().NotBeEmpty();
        envelope.Result.TotalSamples.Should().BeGreaterThan(0);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Processes_Etw_RanksEveryProcessByWeight()
    {
        TraceStore store = new();

        AnalysisResult<ProcessListResult> envelope = TraceTools.Processes(store, FixturePath(Etw));

        AssertEnvelope(envelope);
        // The inventory reads every process rather than auto-scoping to the busiest,
        // and reports them highest weight first.
        envelope.Result.Processes.Should().NotBeEmpty();
        envelope.Result.Processes.Should().BeInDescendingOrder(static p => p.Weight);
    }

    [TestMethod]
    public void Tree_Speedscope_ReturnsRootedCallTree()
    {
        TraceStore store = new();

        AnalysisResult<CallTreeResult> envelope = TraceTools.Tree(store, FixturePath(Speedscope));

        AssertEnvelope(envelope);
        envelope.Result.ScopeWeight.Should().BeGreaterThan(0.0);
        envelope.Result.Root.Children.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Tree_BenchmarkPresetWithoutWorkloadFrame_DropsAllSamples()
    {
        TraceStore store = new();

        AnalysisResult<CallTreeResult> envelope =
            TraceTools.Tree(store, FixturePath(Speedscope), benchmark: true);

        envelope.Result.ScopeWeight.Should().Be(0.0);
        envelope.Result.Root.Children.Should().BeEmpty();
    }

    [TestMethod]
    public void Tree_NegativeMaxDepth_Throws()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Tree(store, FixturePath(Speedscope), maxDepth: -1);

        act.Should().Throw<McpException>().WithMessage("*maxDepth*");
    }

    [TestMethod]
    public void Tree_NegativeMinPercent_Throws()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Tree(store, FixturePath(Speedscope), minPercent: -1.0);

        act.Should().Throw<McpException>().WithMessage("*minPercent*");
    }

    [TestMethod]
    public void Classify_Speedscope_BucketsSelfTimeByCategory()
    {
        TraceStore store = new();

        AnalysisResult<ClassifyResult> envelope = TraceTools.Classify(store, FixturePath(Speedscope));

        AssertEnvelope(envelope);
        envelope.Result.ScopeWeight.Should().BeGreaterThan(0.0);
        envelope.Result.Categories.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Classify_BenchmarkPresetWithoutWorkloadFrame_DropsAllSamples()
    {
        TraceStore store = new();

        AnalysisResult<ClassifyResult> envelope =
            TraceTools.Classify(store, FixturePath(Speedscope), benchmark: true);

        envelope.Result.ScopeWeight.Should().Be(0.0);
        envelope.Result.Categories.Should().BeEmpty();
    }

    [TestMethod]
    public void Jit_NetTrace_ReturnsCompileSummary()
    {
        AnalysisResult<JitStatsResult> envelope = TraceTools.Jit(FixturePath(Jit));

        AssertEnvelope(envelope);
        envelope.Result.MethodCount.Should().BeGreaterThan(0);
        envelope.Result.Methods.Should().NotBeEmpty();
        envelope.Result.TotalCompileMs.Should().BeGreaterThan(0.0);
    }

    [TestMethod]
    public void Jit_NonNetTraceInput_ThrowsMcpException()
    {
        // The JIT report parses the EventPipe format; an .etl or speedscope is rejected
        // up front by the extension guardrail.
        Action act = () => TraceTools.Jit(FixturePath(Speedscope));

        act.Should().Throw<McpException>().WithMessage("*requires a .nettrace*");
    }

    [TestMethod]
    public void Jit_NonPositiveTop_Throws()
    {
        Action act = () => TraceTools.Jit(FixturePath(Jit), top: 0);

        act.Should().Throw<McpException>().WithMessage("*top must be 1 or greater*");
    }

    [TestMethod]
    public void Jit_Top_CapsPerMethodDetail()
    {
        // The aggregate summary always reflects every method, but the per-method detail
        // list is capped to 'top' so a startup trace's thousands of methods cannot blow
        // the output budget.
        AnalysisResult<JitStatsResult> envelope = TraceTools.Jit(FixturePath(Jit), top: 1);

        envelope.Result.Methods.Count.Should().BeLessThanOrEqualTo(1);
    }

    [TestMethod]
    public void Export_Speedscope_WritesFileAndConfirms()
    {
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.speedscope.json");

        try
        {
            AnalysisResult<ExportResult> envelope = TraceTools.Export(store, FixturePath(Speedscope), outputPath);

            File.Exists(outputPath).Should().BeTrue();
            AssertEnvelope(envelope);

            ExportResult result = envelope.Result;
            result.Format.Should().Be("speedscope");
            result.OutputPath.Should().Be(Path.GetFullPath(outputPath));
            result.ByteCount.Should().BeGreaterThan(0);

            // The hint steers a human to the viewer for the chosen format.
            envelope.Hints.Should().Contain(h => h.Contains("speedscope.app", StringComparison.Ordinal));

            // The written file is the same speedscope JSON the exporter produced.
            string written = File.ReadAllText(outputPath);
            written.Should().Contain("\"$schema\"");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Export_Chromium_WritesChromeTraceFormat()
    {
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.chrome.json");

        try
        {
            AnalysisResult<ExportResult> envelope =
                TraceTools.Export(store, FixturePath(Speedscope), outputPath, format: "chromium");

            File.Exists(outputPath).Should().BeTrue();
            AssertEnvelope(envelope);
            envelope.Result.Format.Should().Be("chromium");
            envelope.Hints.Should().Contain(h => h.Contains("perfetto", StringComparison.OrdinalIgnoreCase));

            // The written file is the Chrome Trace Event Format the exporter produced; its
            // distinctive marker is the traceEvents array. Asserting on the file content -
            // not just the envelope - catches a regression that writes the wrong or empty
            // content to disk.
            string written = File.ReadAllText(outputPath);
            written.Should().Contain("\"traceEvents\"");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Export_ProcessScope_OnMachineWideCapture_Warns()
    {
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.speedscope.json");

        try
        {
            // The export tool now scopes a multi-process ETW capture to a named process
            // tree, mirroring the ranking tools; the scope notice surfaces in the envelope
            // warnings. Reading an .etl is Windows-only, so this is guarded.
            AnalysisResult<ExportResult> envelope =
                TraceTools.Export(store, FixturePath(Etw), outputPath, process: "HotLoopBench-Job");

            File.Exists(outputPath).Should().BeTrue();
            AssertEnvelope(envelope);
            envelope.Warnings.Should().Contain(w => w.Contains("Scoped to the"));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Export_ProcessScope_OnSpeedscope_IsHarmlessNoOp()
    {
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.speedscope.json");

        try
        {
            // Speedscope is single-process, so a process selector is a no-op: the export
            // still succeeds and writes the file.
            AnalysisResult<ExportResult> envelope =
                TraceTools.Export(store, FixturePath(Speedscope), outputPath, process: "anything");

            File.Exists(outputPath).Should().BeTrue();
            AssertEnvelope(envelope);
            envelope.Result.Format.Should().Be("speedscope");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Export_RootScoped_ExportsOnlySubtreeUnderRootFrame()
    {
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.speedscope.json");

        try
        {
            // The folding fixture's Program.Main -> MyApp.Work -> MyApp.Inner /
            // MyApp.Other tree: scoping to MyApp.Work must drop Program.Main and the
            // sibling MyApp.Other subtree, matching the same subtree `trace_rank`
            // with root=MyApp.Work would rank.
            AnalysisResult<ExportResult> envelope =
                TraceTools.Export(store, FixturePath(Speedscope), outputPath, root: "MyApp.Work");

            File.Exists(outputPath).Should().BeTrue();
            AssertEnvelope(envelope);

            string written = File.ReadAllText(outputPath);
            written.Should().Contain("MyApp.Work");
            written.Should().Contain("MyApp.Inner");
            written.Should().NotContain("Program.Main");
            written.Should().NotContain("MyApp.Other");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Export_AmbiguousRoot_ReportsDefinitionsAndSelection()
    {
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.speedscope.json");

        try
        {
            AnalysisResult<ExportResult> envelope =
                TraceTools.Export(store, FixturePath(Speedscope), outputPath, root: "MyApp");

            envelope.Warnings.Should().Contain(
                warning => warning.Contains("root 'MyApp'", StringComparison.Ordinal)
                    && warning.Contains("outermost", StringComparison.Ordinal)
                    && warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
            envelope.Warnings.Should().Contain(
                warning => warning.Contains("MyApp.Work", StringComparison.Ordinal));
            envelope.Warnings.Should().Contain(
                warning => warning.Contains("MyApp.Inner", StringComparison.Ordinal));
            envelope.Warnings.Should().Contain(
                warning => warning.Contains("MyApp.Other", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Export_BenchmarkPresetWithoutWorkloadFrame_DropsAllSamples()
    {
        // The folding fixture has no WorkloadAction frame, so this proves 'benchmark'
        // resolves to FrameNames.BenchmarkWorkloadFrame and gets applied (every sample
        // dropped) rather than that it finds a match - RootScopeTests covers the actual
        // trimming behavior against a stack that does contain the frame.
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.speedscope.json");

        try
        {
            AnalysisResult<ExportResult> envelope =
                TraceTools.Export(store, FixturePath(Speedscope), outputPath, benchmark: true);

            File.Exists(outputPath).Should().BeTrue();
            AssertEnvelope(envelope);

            string written = File.ReadAllText(outputPath);
            written.Should().NotContain("Program.Main");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Export_RootAndBenchmarkBothSet_Throws()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Export(
            store, FixturePath(Speedscope), output: "unused.json", root: "MyApp.Work", benchmark: true);

        act.Should().Throw<McpException>().WithMessage("*only one of 'root' and 'benchmark'*");
    }

    [TestMethod]
    public void Export_NativeSymbolsOnSpeedscope_BindsAndIsHarmlessNoOp()
    {
        // nativeSymbols binds on the export tool, just as it does on trace_rank;
        // speedscope carries no native frames, so it is a no-op that reaches no
        // symbol server (offline-safe) and still writes the flame graph.
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.speedscope.json");

        try
        {
            AnalysisResult<ExportResult> envelope =
                TraceTools.Export(store, FixturePath(Speedscope), outputPath, nativeSymbols: true);

            File.Exists(outputPath).Should().BeTrue();
            AssertEnvelope(envelope);
            envelope.Result.Format.Should().Be("speedscope");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Export_UnknownFormat_Throws()
    {
        TraceStore store = new();
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.json");

        Action act = () => TraceTools.Export(store, FixturePath(Speedscope), outputPath, format: "perfetto");

        act.Should().Throw<McpException>().WithMessage("*Unknown format 'perfetto'*");
        File.Exists(outputPath).Should().BeFalse();
    }

    [TestMethod]
    public void Export_EmptyOutput_Throws()
    {
        TraceStore store = new();

        Action act = () => TraceTools.Export(store, FixturePath(Speedscope), output: "  ");

        act.Should().Throw<McpException>().WithMessage("*output is required*");
    }

    [TestMethod]
    public void Export_UnwritablePath_ThrowsMcpException()
    {
        TraceStore store = new();

        // A path into a directory that does not exist is not writable; the failure
        // surfaces as a clean tool error rather than an unhandled exception.
        string badPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nested", "out.json");

        Action act = () => TraceTools.Export(store, FixturePath(Speedscope), badPath);

        act.Should().Throw<McpException>().WithMessage("*Could not write*");
    }

    [TestMethod]
    public void QueryEvents_NetTrace_ReturnsMatchingEventsPage()
    {
        AnalysisResult<EventQueryResult> envelope = TraceTools.QueryEvents(FixturePath(Alloc));

        AssertEnvelope(envelope);
        envelope.Result.TotalMatched.Should().BeGreaterThan(0);
        envelope.Result.Events.Should().NotBeEmpty();
    }

    [TestMethod]
    public void QueryEvents_PayloadFilter_NarrowsToMatchingEvents()
    {
        AnalysisResult<EventQueryResult> matched =
            TraceTools.QueryEvents(FixturePath(Alloc), name: "AllocationTick", take: 1000, payload: "Small");

        AssertEnvelope(matched);
        matched.Result.TotalMatched.Should().BeGreaterThan(0);

        AnalysisResult<EventQueryResult> none =
            TraceTools.QueryEvents(FixturePath(Alloc), name: "AllocationTick", payload: "__no_such_value__");
        none.Result.TotalMatched.Should().Be(0);
    }

    [TestMethod]
    public void QueryEvents_ProcessFilter_UnknownPid_MatchesNothing()
    {
        AnalysisResult<EventQueryResult> envelope =
            TraceTools.QueryEvents(FixturePath(Alloc), name: "AllocationTick", pid: 999999);

        AssertEnvelope(envelope);
        envelope.Result.TotalMatched.Should().Be(0);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void QueryEvents_EtlTrace_ReturnsMatchingEventsPage()
    {
        // The raw event query now spans both formats: an .etl carries a kernel event
        // stream (reading it is Windows-only, hence the OS guard), so the query returns a
        // page of its events.
        AnalysisResult<EventQueryResult> envelope = TraceTools.QueryEvents(FixturePath(Etw));

        AssertEnvelope(envelope);
        envelope.Result.TotalMatched.Should().BeGreaterThan(0);
        envelope.Result.Events.Should().NotBeEmpty();
    }

    [TestMethod]
    public void QueryEvents_Take_PagesAndHintsRemaining()
    {
        // A take smaller than the total match count returns one page and steers toward
        // the next with a paging hint.
        AnalysisResult<EventQueryResult> envelope = TraceTools.QueryEvents(FixturePath(Alloc), take: 1);

        envelope.Result.Events.Count.Should().BeLessThanOrEqualTo(1);

        // When more matches remain, a hint gives the next page's skip.
        if (envelope.Result.TotalMatched > 1)
        {
            envelope.Hints.Should().Contain(h => h.Contains("page with skip", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void QueryEvents_SpeedscopeInput_ThrowsMcpException()
    {
        // The raw event query spans .nettrace and .etl, but a speedscope export carries
        // only CPU stacks - no event stream - so it is rejected up front by the guardrail.
        Action act = () => TraceTools.QueryEvents(FixturePath(Speedscope));

        act.Should().Throw<McpException>().WithMessage("*requires a .nettrace EventPipe or .etl*");
    }

    [TestMethod]
    public void QueryEvents_NegativeSkip_Throws()
    {
        Action act = () => TraceTools.QueryEvents(FixturePath(Alloc), skip: -1);

        act.Should().Throw<McpException>().WithMessage("*skip must be 0 or greater*");
    }

    [TestMethod]
    public void QueryEvents_PidBelowSentinel_Throws()
    {
        // -1 is the "unset" sentinel; anything more negative is an invalid id and fails fast.
        Action act = () => TraceTools.QueryEvents(FixturePath(Alloc), pid: -2);

        act.Should().Throw<McpException>().WithMessage("*pid must be -1*");
    }

    [TestMethod]
    public void QueryEvents_TakeAboveMax_ClampsWithWarning()
    {
        // A take past the page ceiling is clamped rather than honored, so a caller cannot
        // request a page large enough to exhaust memory or blow the token budget; the
        // clamp is surfaced as a warning so paging still works.
        AnalysisResult<EventQueryResult> envelope = TraceTools.QueryEvents(FixturePath(Alloc), take: int.MaxValue);

        envelope.Result.Events.Count.Should().BeLessThanOrEqualTo(1000);
        envelope.Warnings.Should().Contain(w => w.Contains("clamped", StringComparison.Ordinal));
    }

    [TestMethod]
    public void QueryEvents_MaxPayloadAboveMax_ClampsWithWarning()
    {
        // A maxPayload past the ceiling is clamped with a warning for the same reason.
        AnalysisResult<EventQueryResult> envelope =
            TraceTools.QueryEvents(FixturePath(Alloc), maxPayload: int.MaxValue);

        envelope.Warnings.Should().Contain(w => w.Contains("clamped", StringComparison.Ordinal));
    }

    [TestMethod]
    public void QueryEvents_NullPath_ThrowsMcpException()
    {
        // A null path must fail through the format guardrail as a clean McpException, not
        // a NullReferenceException surfaced as an opaque JSON-RPC error.
        Action act = () => TraceTools.QueryEvents(null!);

        act.Should().Throw<McpException>().WithMessage("*requires a .nettrace*");
    }
}
