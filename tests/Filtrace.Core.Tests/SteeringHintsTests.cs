// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
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

        AnalysisNextStep next = new AnalysisResult<RankingResult>(ranking, hints: hints).NextSteps[0];
        next.Operation.Should().Be("rank");
        next.Arguments.Should().NotBeNull();
        next.Arguments!.FromMs.Should().Be(1000.0);
        next.Arguments.ToMs.Should().Be(2000.0);
    }

    [TestMethod]
    public void ForRanking_RootAndProcessScopedCpu_PreservesBoth()
    {
        RankingResult ranking = new(25.0, "WorkloadAction", [new RankRow("MyApp.Inner", 16.0, 64.0)]);
        ScopeRequest scope = ScopeRequest.ForProcess("MyApp");

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking, MetricInfo.Cpu, scope);

        hints.Should().ContainSingle().Which.Should().Be(
            "drill into the hot frame with: callers MyApp.Inner --root 'WorkloadAction' --process 'MyApp'");

        AnalysisNextStepArguments arguments = new AnalysisResult<RankingResult>(ranking, hints: hints)
            .NextSteps[0].Arguments!;
        arguments.Frame.Should().Be("MyApp.Inner");
        arguments.Root.Should().Be("WorkloadAction");
        arguments.Process.Should().Be("MyApp");
    }

    [TestMethod]
    public void ForRanking_PidScopedCpu_EmitsOneCommaSeparatedPidOption()
    {
        RankingResult ranking = new(25.0, "", [new RankRow("MyApp.Inner", 16.0, 64.0)]);
        ScopeRequest scope = ScopeRequest.ForProcessIds([9144, 40356]);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking, MetricInfo.Cpu, scope);

        // A repeated --pid keeps only the last value, so a hint that spelled the ids as
        // separate options would silently widen the drill-down to one process.
        hints.Should().ContainSingle().Which.Should().Be(
            "drill into the hot frame with: callers MyApp.Inner --pid 9144,40356");

        AnalysisResult<RankingResult> envelope = new(ranking, hints: hints);
        envelope.NextSteps.Should().ContainSingle();
        AnalysisNextStep next = envelope.NextSteps[0];
        next.Operation.Should().Be("callers");
        next.Arguments.Should().NotBeNull();
        next.Arguments!.Frame.Should().Be("MyApp.Inner");
        next.Arguments.ProcessIds.Should().Equal(9144, 40356);
        next.Arguments.IncludeChildren.Should().BeTrue();
    }

    [TestMethod]
    public void ForRanking_ProcessIdsAtMetadataLimit_AreComplete()
    {
        int[] processIds = [.. Enumerable.Range(1, AnalysisScopeContext.MaxReportedProcessIds)];
        RankingResult ranking = new(25.0, "", [new RankRow("MyApp.Inner", 16.0, 64.0)]);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(
            ranking,
            MetricInfo.Cpu,
            ScopeRequest.ForProcessIds(processIds));
        AnalysisResult<RankingResult> envelope = new(ranking, hints: hints);

        AnalysisNextStepArguments arguments = envelope.NextSteps[0].Arguments!;
        arguments.ProcessIds.Should().Equal(processIds);
        arguments.ProcessIdCount.Should().Be(processIds.Length);
        arguments.ProcessIdsTruncated.Should().BeFalse();
    }

    [TestMethod]
    public void ForRanking_ProcessIdsOverMetadataLimit_AreBoundedAndCounted()
    {
        int[] processIds = [.. Enumerable.Range(1, AnalysisScopeContext.MaxReportedProcessIds + 1)];
        RankingResult ranking = new(25.0, "", [new RankRow("MyApp.Inner", 16.0, 64.0)]);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(
            ranking,
            MetricInfo.Cpu,
            ScopeRequest.ForProcessIds(processIds));
        AnalysisResult<RankingResult> envelope = new(ranking, hints: hints);

        AnalysisNextStepArguments arguments = envelope.NextSteps[0].Arguments!;
        arguments.ProcessIds.Should().Equal(processIds.Take(AnalysisScopeContext.MaxReportedProcessIds));
        arguments.ProcessIdCount.Should().Be(processIds.Length);
        arguments.ProcessIdsTruncated.Should().BeTrue();
    }

    [TestMethod]
    public void ForRanking_ParentOnlyPidScope_CarriesTheDescendantMode()
    {
        RankingResult ranking = new(25.0, "", [new RankRow("MyApp.Inner", 16.0, 64.0)]);
        ScopeRequest scope = ScopeRequest.ForProcessIds([9144], includeChildren: false);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking, MetricInfo.Cpu, scope);

        hints.Should().ContainSingle().Which.Should().Be(
            "drill into the hot frame with: callers MyApp.Inner --pid 9144 --children exclude");

        AnalysisNextStepArguments arguments = new AnalysisResult<RankingResult>(ranking, hints: hints)
            .NextSteps[0].Arguments!;
        arguments.ProcessIds.Should().Equal(9144);
        arguments.IncludeChildren.Should().BeFalse();
    }

    [TestMethod]
    public void ForRanking_ParentOnlyAutomaticScope_StillCarriesTheDescendantMode()
    {
        RankingResult ranking = new(25.0, "", [new RankRow("MyApp.Inner", 16.0, 64.0)]);
        ScopeRequest scope = ScopeRequest.AutoScope(includeChildren: false);

        IReadOnlyList<string> hints = SteeringHints.ForRanking(ranking, MetricInfo.Cpu, scope);

        // The automatic scope carries no selector but still picks a tree, so dropping
        // --children exclude here would widen the drill-down back to the children.
        hints.Should().ContainSingle().Which.Should().Be(
            "drill into the hot frame with: callers MyApp.Inner --children exclude");
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

        AnalysisResult<TraceInfo> envelope = new(info, hints: hints);
        envelope.NextSteps.Should().HaveSameCount(hints);
        envelope.NextSteps.Should().AllSatisfy(step => step.Operation.Should().BeNull());
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
    public void ForTraceInfo_PoorSourceResolutionUnderFrenchCulture_UsesAsciiPercentages()
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
            {
                PdbIdentityMismatchModules = ["GeneratedChild"],
                SampledManagedMethodCount = 10,
                SourceMappedManagedMethodCount = 0,
                UnmappedNamedManagedFrameCount = 100,
                HighestUnmappedMethods = ["GeneratedChild!Run (0/75 mapped)"]
            }
        };

        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        IReadOnlyList<string> hints;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            hints = SteeringHints.ForTraceInfo(info);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        hints.Should().Contain(hint =>
            hint.Contains("method-name resolution (100%) is separate from source mapping (0%)", StringComparison.Ordinal)
            && hint.Contains("GeneratedChild", StringComparison.Ordinal)
            && hint.Contains("generated child output", StringComparison.Ordinal));
        hints.Should().Contain(hint =>
            hint.Contains("PDB identity mismatch for: GeneratedChild", StringComparison.Ordinal)
            && hint.Contains("trace-recorded GUID/age", StringComparison.Ordinal));
        hints.Should().Contain(hint =>
            hint.Contains("named managed frames without source: 100", StringComparison.Ordinal)
            && hint.Contains("sourceResolution.highestUnmappedMethods", StringComparison.Ordinal));
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

        AnalysisResult<CallersResult> envelope = new(callers, hints: hints);
        envelope.NextSteps.Should().HaveCount(2);
        envelope.NextSteps.Should().OnlyContain(step => step.Operation == "callers");
        envelope.NextSteps.Should().OnlyContain(step => step.Arguments!.Root == "WorkloadAction");
        envelope.NextSteps.Should().OnlyContain(step => step.Arguments!.Process == "MyApp");
        envelope.NextSteps[0].Arguments!.Frame.Should().Be("Program.Main");
        envelope.NextSteps[0].Arguments!.Callees.Should().BeNull();
        envelope.NextSteps[1].Arguments!.Frame.Should().Be("MyApp.Inner");
        envelope.NextSteps[1].Arguments!.Callees.Should().BeTrue();
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

        AnalysisNextStep next = new AnalysisResult<RankingDiffResult>(diff, hints: hints).NextSteps[0];
        next.Operation.Should().Be("callers");
        next.Arguments.Should().NotBeNull();
        next.Arguments!.Metric.Should().Be("cpu");
        next.Arguments.Frame.Should().Be("MyApp.Slow");
    }

    [TestMethod]
    public void ForDiff_NoChanges_NotesTheMatch()
    {
        RankingDiffResult diff = new(20.0, 20.0, 0.0, []);

        IReadOnlyList<string> hints = SteeringHints.ForDiff(diff);

        hints.Should().ContainSingle().Which.Should().Contain("no frames changed");
    }

    [TestMethod]
    public void ForDiff_EmptyManifest_NotesThatNoCaseChanged()
    {
        RankingDiffResult diff = new([]);

        IReadOnlyList<string> hints = SteeringHints.ForDiff(diff);

        hints.Should().ContainSingle().Which.Should().Contain("paired manifest cases");
        hints.Should().NotContain(hint => hint.Contains("two rankings match", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForBatch_LegacyCaseWithoutId_RemainsReasonOnly()
    {
        BatchRankingResult batch = new(
            "manifest.json",
            "cpu",
            "self",
            string.Empty,
            [new BatchRankingCaseResult(
                "Command.Run",
                string.Empty,
                "command.etl",
                10.0,
                "ms",
                "MyApp.Hot",
                8.0,
                80.0,
                10,
                [])]);

        IReadOnlyList<string> hints = SteeringHints.ForBatch(batch);
        AnalysisResult<BatchRankingResult> envelope = new(batch, hints: hints);

        envelope.NextSteps.Should().ContainSingle();
        envelope.NextSteps[0].Operation.Should().BeNull();
        envelope.NextSteps[0].Reason.Should().Contain("Command.Run");
    }

    [TestMethod]
    public void ForBatch_CaseReference_PreservesRankOverrides()
    {
        BatchRankingCaseResult captureCase = new(
            "Command.Run",
            string.Empty,
            "command.etl",
            10.0,
            "ms",
            "MyApp.Hot",
            8.0,
            80.0,
            10,
            [])
        {
            CaseId = "command"
        };
        BatchRankingResult batch = new(
            "manifest.json",
            "cpu",
            "inclusive",
            "Workload",
            [captureCase]);
        ScopeRequest scope = ScopeRequest.ForProcessIds([9144, 40356], includeChildren: false);

        IReadOnlyList<string> hints = SteeringHints.ForBatch(
            batch,
            scope,
            symbols: "symbols",
            foldPatterns: ["CustomFold"]);
        AnalysisResult<BatchRankingResult> envelope = new(batch, hints: hints);

        AnalysisNextStep next = envelope.NextSteps.Should().ContainSingle().Subject;
        next.Operation.Should().Be("rank");
        next.Arguments.Should().NotBeNull();
        AnalysisNextStepArguments arguments = next.Arguments!;
        arguments.ManifestPath.Should().Be("manifest.json");
        arguments.CaseId.Should().Be("command");
        arguments.Metric.Should().Be("cpu");
        arguments.Measure.Should().Be("inclusive");
        arguments.Root.Should().Be("Workload");
        arguments.ProcessIds.Should().Equal(9144, 40356);
        arguments.IncludeChildren.Should().BeFalse();
        arguments.Symbols.Should().Be("symbols");
        arguments.Fold.Should().Equal("CustomFold");
        next.Reason.Should().Be("inspect manifest case 'command' in detail");
    }

    [TestMethod]
    public void ForBatch_CaseReference_AtOverrideLimitsRemainsActionable()
    {
        BatchRankingResult batch = BatchWithCaseId();
        string symbols = new('s', 1024);
        string[] foldPatterns = [.. Enumerable.Repeat(new string('f', 256), 32)];

        IReadOnlyList<string> hints = SteeringHints.ForBatch(
            batch,
            scope: null,
            symbols,
            foldPatterns);

        AnalysisNextStep next = new AnalysisResult<BatchRankingResult>(batch, hints: hints).NextSteps[0];
        next.Operation.Should().Be("rank");
        next.Arguments!.Symbols.Should().HaveLength(1024);
        next.Arguments.Fold.Should().HaveCount(32);
    }

    [TestMethod]
    public void ForBatch_CaseReference_TooManyFoldPatternsUsesReasonOnlyGuidance()
    {
        BatchRankingResult batch = BatchWithCaseId();
        string[] foldPatterns = [.. Enumerable.Repeat("fold", 33)];

        IReadOnlyList<string> hints = SteeringHints.ForBatch(batch, null, null, foldPatterns);
        AnalysisNextStep next = new AnalysisResult<BatchRankingResult>(batch, hints: hints).NextSteps[0];

        next.Operation.Should().BeNull();
        next.Arguments.Should().BeNull();
    }

    [TestMethod]
    public void ForBatch_CaseReference_OverlongFoldPatternUsesReasonOnlyGuidance()
    {
        BatchRankingResult batch = BatchWithCaseId();

        IReadOnlyList<string> hints = SteeringHints.ForBatch(batch, null, null, [new string('f', 257)]);
        AnalysisNextStep next = new AnalysisResult<BatchRankingResult>(batch, hints: hints).NextSteps[0];

        next.Operation.Should().BeNull();
        next.Arguments.Should().BeNull();
    }

    [TestMethod]
    public void ForBatch_CaseReference_OverlongSymbolsUsesReasonOnlyGuidance()
    {
        BatchRankingResult batch = BatchWithCaseId();

        IReadOnlyList<string> hints = SteeringHints.ForBatch(batch, null, new string('s', 1025), null);
        AnalysisNextStep next = new AnalysisResult<BatchRankingResult>(batch, hints: hints).NextSteps[0];

        next.Operation.Should().BeNull();
        next.Arguments.Should().BeNull();
    }

    private static BatchRankingResult BatchWithCaseId() =>
        new(
            "manifest.json",
            "cpu",
            "self",
            string.Empty,
            [new BatchRankingCaseResult(
                "Command.Run",
                string.Empty,
                "command.etl",
                10.0,
                "ms",
                "MyApp.Hot",
                8.0,
                80.0,
                10,
                [])
            {
                CaseId = "command"
            }]);

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
            Exceptions: null, Alloc: null, Jit: null)
        {
            AppliedProcessScope = new AppliedProcessScope("name", "HotLoopBench", [], [123], [], true)
        };

        IReadOnlyList<string> hints = SteeringHints.ForTimeline(timeline);

        // A scoped timeline propagates its process into the drill so the follow-up
        // ranking stays on the same tree rather than re-auto-scoping.
        hints.Should().ContainSingle().Which.Should().EndWith("--time 40,60 --process 'HotLoopBench'");

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: hints);
        envelope.NextSteps.Should().ContainSingle();
        AnalysisNextStep next = envelope.NextSteps[0];
        next.Operation.Should().Be("rank");
        next.Arguments.Should().NotBeNull();
        next.Arguments!.Metric.Should().Be("cpu");
        next.Arguments.Process.Should().Be("HotLoopBench");
        next.Arguments.FromMs.Should().Be(40.0);
        next.Arguments.ToMs.Should().Be(60.0);
    }

    [TestMethod]
    public void ForTimeline_BucketWithPidScope_PreservesIdsAndChildrenMode()
    {
        TimelineResult timeline = CpuBucketTimeline() with
        {
            AppliedProcessScope = new AppliedProcessScope("ids", null, [123, 456], [123, 456], [], false)
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().EndWith("--pid 123,456 --children exclude");
        AnalysisNextStepArguments arguments = envelope.NextSteps.Single().Arguments!;
        arguments.Process.Should().BeNull();
        arguments.ProcessIds.Should().Equal(123, 456);
        arguments.ProcessIdCount.Should().Be(2);
        arguments.IncludeChildren.Should().BeFalse();
        arguments.FromMs.Should().Be(40.0);
        arguments.ToMs.Should().Be(60.0);
    }

    [TestMethod]
    public void ForTimeline_BucketWithAllProcesses_PreservesOptOut()
    {
        TimelineResult timeline = CpuBucketTimeline() with
        {
            AppliedProcessScope = AppliedProcessScope.AllProcesses
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().EndWith("--all-processes");
        AnalysisNextStepArguments arguments = envelope.NextSteps.Single().Arguments!;
        arguments.AllProcesses.Should().BeTrue();
        arguments.Process.Should().BeNull();
        arguments.ProcessIds.Should().BeNull();
        arguments.IncludeChildren.Should().BeNull();
        arguments.FromMs.Should().Be(40.0);
        arguments.ToMs.Should().Be(60.0);
    }

    [TestMethod]
    public void ForTimeline_BucketWithAutomaticScope_PreservesResolvedRootIds()
    {
        TimelineResult timeline = CpuBucketTimeline() with
        {
            AppliedProcessScope = new AppliedProcessScope("automatic", "App", [], [789], [790], true)
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().EndWith("--pid 789");
        AnalysisNextStepArguments arguments = envelope.NextSteps.Single().Arguments!;
        arguments.Process.Should().BeNull();
        arguments.ProcessIds.Should().Equal(789);
        arguments.IncludeChildren.Should().BeTrue();
        arguments.FromMs.Should().Be(40.0);
        arguments.ToMs.Should().Be(60.0);
    }

    [TestMethod]
    public void ForTimeline_BucketWithReusedAutomaticRootId_EmitsNoRunnableFollowUp()
    {
        AppliedProcessScope scope = new("automatic", "App", [], [789], [], true)
        {
            RootProcessIdsReplayable = false
        };
        TimelineResult timeline = CpuBucketTimeline() with { AppliedProcessScope = scope };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should()
            .Contain("root pid reused by multiple process instances")
            .And.Contain("choose an explicit selector")
            .And.NotContain("--pid 789");
        envelope.NextSteps.Should().ContainSingle().Which.Operation.Should().BeNull();
        envelope.NextSteps[0].Arguments.Should().BeNull();
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
    public void ForTimeline_Snapshot_PreservesWindowAndProcessInCpuDrill()
    {
        const string process = "My App's $(Get-Item)";
        TimelineSnapshot snapshot = new(
            50.0,
            new SnapshotGcSummary(0, 0.0, 0.0, []),
            new SnapshotCpuSummary(10, 1, [new SnapshotCpuMethod("App.Hot", 10, 100.0)]),
            new SnapshotExceptionSummary(0, 0, []),
            new SnapshotAllocationSummary(0, 0, 0, []),
            new SnapshotJitSummary(0, 0, []),
            new SnapshotEventSummary(10, 1, [new SnapshotEventType("SampleProfiler", "ThreadSample", 10)]),
            false);
        TimelineResult timeline = new(
            40.0, 60.0, 20.0, 1, process, null, null, null, null, null)
        {
            Mode = "snapshot",
            Snapshot = snapshot,
            AppliedProcessScope = new AppliedProcessScope("name", process, [], [123], [], true)
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should()
            .EndWith("--time 40,60 --process 'My App''s $(Get-Item)'");
        AnalysisNextStep next = envelope.NextSteps.Should().ContainSingle().Subject;
        next.Operation.Should().Be("rank");
        next.Arguments!.Metric.Should().Be("cpu");
        next.Arguments.Process.Should().Be(process);
        next.Arguments.FromMs.Should().Be(40.0);
        next.Arguments.ToMs.Should().Be(60.0);
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithPidScope_PreservesIdsAndChildrenMode()
    {
        TimelineResult timeline = SnapshotTimeline() with
        {
            AppliedProcessScope = new AppliedProcessScope("ids", null, [123, 456], [123, 456], [], false)
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().EndWith("--pid 123,456 --children exclude");
        AnalysisNextStepArguments arguments = envelope.NextSteps.Single().Arguments!;
        arguments.Process.Should().BeNull();
        arguments.ProcessIds.Should().Equal(123, 456);
        arguments.ProcessIdCount.Should().Be(2);
        arguments.IncludeChildren.Should().BeFalse();
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithAllProcesses_PreservesOptOut()
    {
        TimelineResult timeline = SnapshotTimeline() with
        {
            AppliedProcessScope = AppliedProcessScope.AllProcesses
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().EndWith("--all-processes");
        AnalysisNextStepArguments arguments = envelope.NextSteps.Single().Arguments!;
        arguments.AllProcesses.Should().BeTrue();
        arguments.Process.Should().BeNull();
        arguments.ProcessIds.Should().BeNull();
        arguments.IncludeChildren.Should().BeNull();
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithAutomaticScope_PreservesResolvedRootIds()
    {
        TimelineResult timeline = SnapshotTimeline() with
        {
            AppliedProcessScope = new AppliedProcessScope("automatic", "App", [], [789], [790], true)
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().EndWith("--pid 789");
        AnalysisNextStepArguments arguments = envelope.NextSteps.Single().Arguments!;
        arguments.ProcessIds.Should().Equal(789);
        arguments.IncludeChildren.Should().BeTrue();
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithTooManyAutomaticRootIds_AsksForNarrowerSelector()
    {
        int[] processIds = [.. Enumerable.Range(1, AnalysisScopeContext.MaxReportedProcessIds + 1)];
        TimelineResult timeline = SnapshotTimeline() with
        {
            AppliedProcessScope = new AppliedProcessScope("automatic", "App", [], processIds, [], true)
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should()
            .Contain($"exact process scope has {processIds.Length} ids")
            .And.Contain("choose a narrower --process or --pid selector")
            .And.NotContain("original process selector");
        envelope.NextSteps.Should().ContainSingle().Which.Operation.Should().BeNull();
        envelope.NextSteps[0].Arguments.Should().BeNull();
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithReusedAutomaticRootId_EmitsNoRunnableFollowUp()
    {
        AppliedProcessScope scope = new("automatic", "App", [], [789], [], true)
        {
            RootProcessIdsReplayable = false
        };
        TimelineResult timeline = SnapshotTimeline() with { AppliedProcessScope = scope };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should()
            .Contain("root pid reused by multiple process instances")
            .And.Contain("choose an explicit selector")
            .And.NotContain("--pid 789");
        envelope.NextSteps.Should().ContainSingle().Which.Operation.Should().BeNull();
        envelope.NextSteps[0].Arguments.Should().BeNull();
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithNameScopeAndReusedRootId_PreservesNameFollowUp()
    {
        AppliedProcessScope scope = new("name", "App", [], [789], [], true)
        {
            RootProcessIdsReplayable = false
        };
        TimelineResult timeline = SnapshotTimeline() with { AppliedProcessScope = scope };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().EndWith("--process 'App'");
        envelope.NextSteps.Should().ContainSingle().Which.Operation.Should().Be("rank");
        envelope.NextSteps[0].Arguments!.Process.Should().Be("App");
        envelope.NextSteps[0].Arguments!.ProcessIds.Should().BeNull();
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithTooManyExactIds_EmitsNoRunnableFollowUp()
    {
        int[] processIds = [.. Enumerable.Range(1, AnalysisScopeContext.MaxReportedProcessIds + 1)];
        TimelineResult timeline = SnapshotTimeline() with
        {
            AppliedProcessScope = new AppliedProcessScope("ids", null, processIds, processIds, [], true)
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should()
            .Contain($"exact process scope has {processIds.Length} ids")
            .And.Contain("original process selector");
        envelope.NextSteps.Should().ContainSingle().Which.Operation.Should().BeNull();
        envelope.NextSteps[0].Arguments.Should().BeNull();
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithOnlyExceptions_DrillsExceptionPaths()
    {
        TimelineSnapshot snapshot = new(
            50.0,
            new SnapshotGcSummary(0, 0.0, 0.0, []),
            new SnapshotCpuSummary(0, 0, []),
            new SnapshotExceptionSummary(3, 1, [new SnapshotCountRow("System.InvalidOperationException", 3)]),
            new SnapshotAllocationSummary(0, 0, 0, []),
            new SnapshotJitSummary(0, 0, []),
            new SnapshotEventSummary(3, 1, [new SnapshotEventType("Runtime", "Exception", 3)]),
            false);
        TimelineResult timeline = new(
            40.0, 60.0, 20.0, 1, null, null, null, null, null, null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().Contain("top exception paths");
        AnalysisNextStep next = envelope.NextSteps.Should().ContainSingle().Subject;
        next.Operation.Should().Be("rank");
        next.Arguments!.Metric.Should().Be("exceptions");
        next.Arguments.FromMs.Should().Be(40.0);
        next.Arguments.ToMs.Should().Be(60.0);
    }

    [TestMethod]
    public void ForTimeline_SnapshotWithUnresolvedCpuSamples_DrillsCpuRanking()
    {
        TimelineSnapshot snapshot = new(
            50.0,
            new SnapshotGcSummary(0, 0.0, 0.0, []),
            new SnapshotCpuSummary(10, 0, []),
            new SnapshotExceptionSummary(0, 0, []),
            new SnapshotAllocationSummary(0, 0, 0, []),
            new SnapshotJitSummary(0, 0, []),
            new SnapshotEventSummary(10, 1, [new SnapshotEventType("SampleProfiler", "ThreadSample", 10)]),
            false);
        TimelineResult timeline = new(40.0, 60.0, 20.0, 1, null, null, null, null, null, null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };

        AnalysisResult<TimelineResult> envelope = new(timeline, hints: SteeringHints.ForTimeline(timeline));

        envelope.Hints.Should().ContainSingle().Which.Should().Contain("top CPU work");
        envelope.NextSteps.Single().Arguments!.Metric.Should().Be("cpu");
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

    private static TimelineResult SnapshotTimeline()
    {
        TimelineSnapshot snapshot = new(
            50.0,
            new SnapshotGcSummary(0, 0.0, 0.0, []),
            new SnapshotCpuSummary(10, 1, [new SnapshotCpuMethod("App.Hot", 10, 100.0)]),
            new SnapshotExceptionSummary(0, 0, []),
            new SnapshotAllocationSummary(0, 0, 0, []),
            new SnapshotJitSummary(0, 0, []),
            new SnapshotEventSummary(10, 1, [new SnapshotEventType("Runtime", "Sample", 10)]),
            false);
        return new TimelineResult(40.0, 60.0, 20.0, 1, null, null, null, null, null, null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };
    }

    private static TimelineResult CpuBucketTimeline() =>
        new(
            0.0,
            100.0,
            20.0,
            5,
            null,
            Gc: null,
            Cpu:
            [
                new CpuBucket(0, null),
                new CpuBucket(0, null),
                new CpuBucket(50, "App.Hot"),
                new CpuBucket(1, null),
                new CpuBucket(0, null)
            ],
            Exceptions: null,
            Alloc: null,
            Jit: null);
}
