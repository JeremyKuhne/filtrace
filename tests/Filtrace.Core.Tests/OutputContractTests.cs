// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Output;

[TestClass]
public sealed class OutputContractTests
{
    private static AnalysisResult<RankingResult> SampleEnvelope()
    {
        RankingResult payload = new(
            25.0,
            "",
            [
                new RankRow("MyApp.Inner", 16.0, 64.0),
                new RankRow("MyApp.Work", 4.0, 16.0)
            ],
            4);

        return new AnalysisResult<RankingResult>(
            payload,
            warnings: ["Only 50% of frames resolved to a method name (< 80%); native frames may be unresolved."],
            hints: SteeringHints.ForRanking(payload),
            context: new AnalysisContext("rank")
            {
                Metric = "cpu",
                Measure = "self",
                Unit = "ms"
            });
    }

    [TestMethod]
    public void Serialize_Envelope_IsSingleLineCompactJson()
    {
        string json = OutputJson.Serialize(SampleEnvelope());

        json.Should().NotContain("\n");
        json.Should().NotContain("\r");
    }

    [TestMethod]
    public void Serialize_NullEnvelope_ThrowsArgumentNull()
    {
        Action act = () => OutputJson.Serialize<RankingResult>(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("result");
    }

    [TestMethod]
    public void Serialize_Envelope_CarriesSchemaVersionWarningsAndHints()
    {
        string json = OutputJson.Serialize(SampleEnvelope());

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(AnalysisResult<RankingResult>.CurrentSchemaVersion);
        root.GetProperty("warnings").EnumerateArray().Should().ContainSingle();
        JsonElement warning = root.GetProperty("warnings")[0];
        warning.GetProperty("code").GetString().Should().Be(AnalysisDiagnosticCodes.LowFrameResolution);
        warning.GetProperty("severity").GetString().Should().Be("warning");
        warning.GetProperty("message").GetString().Should().Contain("Only 50% of frames resolved");
        warning.GetProperty("data").GetProperty("resolutionPercent").GetInt32().Should().Be(50);
        warning.GetProperty("data").GetProperty("minimumResolutionPercent").GetInt32().Should().Be(80);
        root.GetProperty("hints").EnumerateArray().Should().ContainSingle();
        JsonElement next = root.GetProperty("hints")[0];
        next.GetProperty("operation").GetString().Should().Be("callers");
        next.GetProperty("reason").GetString().Should().Contain("drill into the hot frame");
        next.GetProperty("arguments").GetProperty("frame").GetString().Should().Be("MyApp.Inner");
        next.GetProperty("arguments").GetProperty("metric").GetString().Should().Be("cpu");
        root.GetProperty("context").GetProperty("operation").GetString().Should().Be("rank");
        root.GetProperty("context").GetProperty("metric").GetString().Should().Be("cpu");
        root.GetProperty("result").GetProperty("rows").EnumerateArray().Should().HaveCount(2);
    }

    [TestMethod]
    public void Serialize_Context_OmitsFieldsThatDoNotApply()
    {
        AnalysisResult<RankingResult> envelope = new(
            new RankingResult(0.0, string.Empty, []),
            context: new AnalysisContext("rank") { Metric = "cpu", Unit = "ms" });

        using JsonDocument document = JsonDocument.Parse(OutputJson.Serialize(envelope));
        JsonElement context = document.RootElement.GetProperty("context");

        context.TryGetProperty("measure", out _).Should().BeFalse();
        context.TryGetProperty("scope", out _).Should().BeFalse();
    }

    [TestMethod]
    public void Serialize_Context_CarriesResolvedProcessScope()
    {
        AnalysisResult<RankingResult> envelope = new(
            new RankingResult(0.0, "Workload", []),
            context: new AnalysisContext("rank")
            {
                Metric = "cpu",
                Measure = "self",
                Unit = "ms",
                Scope = new AnalysisScopeContext
                {
                    Root = "Workload",
                    ProcessMode = "ids",
                    RequestedProcessIds = [9144],
                    RootProcessIds = [9144],
                    DescendantProcessIds = [40356],
                    IncludeChildren = true
                }
            });

        using JsonDocument document = JsonDocument.Parse(OutputJson.Serialize(envelope));
        JsonElement scope = document.RootElement.GetProperty("context").GetProperty("scope");

        scope.GetProperty("root").GetString().Should().Be("Workload");
        scope.GetProperty("processMode").GetString().Should().Be("ids");
        scope.GetProperty("rootProcessIds")[0].GetInt32().Should().Be(9144);
        scope.GetProperty("descendantProcessIds")[0].GetInt32().Should().Be(40356);
        scope.GetProperty("includeChildren").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void ForTrace_RootScope_CarriesAncestryCoverageAndGuidance()
    {
        TraceInfo info = new(
            "parallel.nettrace",
            TraceFormat.NetTrace,
            10.0,
            2,
            1.0,
            [],
            [],
            TraceCapabilities.AnalysesFor(TraceFormat.NetTrace));
        LoadedTrace trace = new(
            info,
            new StackSampleSource(
                MetricInfo.Cpu,
                [
                    new SampleStack(["Process", "SelectedRoot", "Worker.One"], 4.0, "worker-1"),
                    new SampleStack(["Process", "Worker.Sibling"], 6.0, "worker-2")
                ],
                StackRecordSemantics.PeriodicCpuSamples));
        AnalysisContext context = AnalysisContext.ForTrace("rank", trace, "self", "SelectedRoot");
        AnalysisResult<RankingResult> envelope = new(
            trace.Aggregator.SelfTime("SelectedRoot", [], 25),
            context: context);

        AnalysisScopeContext scope = context.Scope!;
        scope.RootKind.Should().Be(AnalysisScopeContext.StackAncestryRootKind);
        scope.RootCoverage.Should().NotBeNull();
        scope.RootCoverage!.AvailableWeight.Should().Be(10.0);
        scope.RootCoverage.RetainedWeight.Should().Be(4.0);
        scope.RootCoverage.AvailableRecordCount.Should().Be(2);
        scope.RootCoverage.RetainedRecordCount.Should().Be(1);
        envelope.Diagnostics.Should().ContainSingle(
            diagnostic => diagnostic.Code == AnalysisDiagnosticCodes.RootScopeAncestry
                && diagnostic.Severity == "info");
        envelope.Hints.Should().ContainSingle(
            hint => hint.Contains("may omit sibling workers", StringComparison.Ordinal));
        envelope.NextSteps.Should().ContainSingle();
        AnalysisNextStep nextStep = envelope.NextSteps.Single();
        nextStep.Operation.Should().BeNull();
        nextStep.Reason.Should().Contain("validated time window");

        using JsonDocument document = JsonDocument.Parse(OutputJson.Serialize(envelope));
        JsonElement serializedScope = document.RootElement.GetProperty("context").GetProperty("scope");
        serializedScope.GetProperty("rootKind").GetString().Should().Be("stackAncestry");
        JsonElement coverage = serializedScope.GetProperty("rootCoverage");
        coverage.GetProperty("availableWeight").GetDouble().Should().Be(10.0);
        coverage.GetProperty("retainedWeight").GetDouble().Should().Be(4.0);
        coverage.GetProperty("retainedPercent").GetDouble().Should().Be(40.0);
        coverage.GetProperty("availableRecordCount").GetInt32().Should().Be(2);
        coverage.GetProperty("retainedRecordCount").GetInt32().Should().Be(1);
    }

    [TestMethod]
    public void ForTrace_ManyProcessIds_BoundsListsAndKeepsCounts()
    {
        int[] processIds = [.. Enumerable.Range(1, 100)];
        TraceInfo info = new(
            "trace.etl",
            TraceFormat.Etl,
            0.0,
            0,
            1.0,
            [],
            [],
            [])
        {
            AppliedProcessScope = new AppliedProcessScope(
                "ids",
                null,
                processIds,
                processIds,
                processIds,
                true)
        };
        LoadedTrace trace = new(info, new StackSampleSource(MetricInfo.Cpu, []));

        AnalysisScopeContext scope = AnalysisContext.ForTrace("rank", trace).Scope!;

        scope.RequestedProcessIds.Should().HaveCount(AnalysisScopeContext.MaxReportedProcessIds);
        scope.RootProcessIds.Should().HaveCount(AnalysisScopeContext.MaxReportedProcessIds);
        scope.DescendantProcessIds.Should().HaveCount(AnalysisScopeContext.MaxReportedProcessIds);
        scope.RequestedProcessIdCount.Should().Be(100);
        scope.RootProcessIdCount.Should().Be(100);
        scope.DescendantProcessIdCount.Should().Be(100);
        scope.ProcessIdsTruncated.Should().BeTrue();
    }

    [TestMethod]
    public void ForTrace_ExactIdsMatchedNothing_CarriesEmptyResolvedLists()
    {
        TraceInfo info = new(
            "trace.etl",
            TraceFormat.Etl,
            0.0,
            0,
            1.0,
            [],
            [],
            [])
        {
            AppliedProcessScope = new AppliedProcessScope("ids", null, [999999], [], [], true)
        };
        LoadedTrace trace = new(info, new StackSampleSource(MetricInfo.Cpu, []));

        AnalysisScopeContext scope = AnalysisContext.ForTrace("rank", trace).Scope!;

        scope.RequestedProcessIds.Should().Equal(999999);
        scope.RootProcessIds.Should().NotBeNull().And.BeEmpty();
        scope.DescendantProcessIds.Should().NotBeNull().And.BeEmpty();
        scope.RootProcessIdCount.Should().Be(0);
        scope.DescendantProcessIdCount.Should().Be(0);

        AnalysisResult<RankingResult> envelope = new(
            new RankingResult(0.0, string.Empty, []),
            context: AnalysisContext.ForTrace("rank", trace));
        string json = OutputJson.Serialize(envelope);
        json.Should().Contain("\"rootProcessIds\":[]");
        json.Should().Contain("\"descendantProcessIds\":[]");
    }

    [TestMethod]
    public void ForTrace_AllProcessesWithoutOtherScope_OmitsScope()
    {
        TraceInfo info = new(
            "trace.etl",
            TraceFormat.Etl,
            0.0,
            0,
            1.0,
            [],
            [],
            [])
        {
            AppliedProcessScope = AppliedProcessScope.AllProcesses
        };
        LoadedTrace trace = new(info, new StackSampleSource(MetricInfo.Cpu, []));

        AnalysisContext.ForTrace("rank", trace).Scope.Should().BeNull();
    }

    [TestMethod]
    public void Serialize_EmptyWarningsAndHints_AreEmptyArraysNotNull()
    {
        RankingResult payload = new(0.0, "", []);
        AnalysisResult<RankingResult> envelope = new(payload);

        string json = OutputJson.Serialize(envelope);

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("warnings").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("hints").GetArrayLength().Should().Be(0);
    }

    [TestMethod]
    public void Constructor_PreservesWarningMessagesForTextRenderers()
    {
        AnalysisResult<RankingResult> envelope = new(
            new RankingResult(0.0, string.Empty, []),
            warnings: ["plain warning"]);

        envelope.Warnings.Should().ContainSingle("plain warning");
        envelope.Diagnostics.Should().ContainSingle();
        envelope.Diagnostics[0].Message.Should().Be("plain warning");
    }

    [TestMethod]
    public void Constructor_SnapshotsWarningAndHintInputs()
    {
        List<string> warnings = ["first warning"];
        List<string> hints = ["first hint"];
        AnalysisResult<RankingResult> envelope = new(
            new RankingResult(0.0, string.Empty, []),
            warnings,
            hints);

        warnings.Add("late warning");
        hints.Add("late hint");

        envelope.Warnings.Should().ContainSingle("first warning");
        envelope.Diagnostics.Should().ContainSingle();
        envelope.Hints.Should().ContainSingle("first hint");
        envelope.NextSteps.Should().ContainSingle();
        envelope.NextSteps[0].Reason.Should().Be("first hint");
        envelope.NextSteps[0].Operation.Should().BeNull();
    }

    [TestMethod]
    public void Constructor_LegacyFourParameterSignature_RemainsAvailable()
    {
        Type envelopeType = typeof(AnalysisResult<RankingResult>);

        envelopeType.GetConstructor(
        [
            typeof(RankingResult),
            typeof(IReadOnlyList<string>),
            typeof(IReadOnlyList<string>),
            typeof(AnalysisContext)
        ]).Should().NotBeNull();
    }

    [TestMethod]
    public void AnalysisEnvelopeSchema_LegacyChannelsRemainStringLists()
    {
        typeof(AnalysisEnvelopeSchema).GetProperty(nameof(AnalysisEnvelopeSchema.Warnings))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<string>));
        typeof(AnalysisEnvelopeSchema).GetProperty(nameof(AnalysisEnvelopeSchema.Hints))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<string>));
    }

    [TestMethod]
    public void Serialize_NullOptionalsAreOmittedWhileSemanticEmptyValuesRemain()
    {
        AnalysisResult<RankingResult> rankingEnvelope = new(
            new RankingResult(0.0, string.Empty, []));
        using JsonDocument rankingDocument = JsonDocument.Parse(OutputJson.Serialize(rankingEnvelope));
        JsonElement ranking = rankingDocument.RootElement.GetProperty("result");
        ranking.TryGetProperty("contributingRecordCount", out _).Should().BeFalse();
        ranking.GetProperty("rootFrame").GetString().Should().BeEmpty();
        ranking.GetProperty("rows").GetArrayLength().Should().Be(0);

        AnalysisResult<CallersResult> callersEnvelope = new(
            new CallersResult("Focus", 0.0, 0.0, 0.0, []));
        using JsonDocument callersDocument = JsonDocument.Parse(OutputJson.Serialize(callersEnvelope));
        JsonElement callers = callersDocument.RootElement.GetProperty("result");
        callers.TryGetProperty("callees", out _).Should().BeFalse();
        callers.TryGetProperty("contributingRecordCount", out _).Should().BeFalse();
        callers.GetProperty("callers").GetArrayLength().Should().Be(0);
        callers.GetProperty("targetWeight").GetDouble().Should().Be(0.0);

        TimelineResult timelineResult = new(
                0.0,
                1.0,
                1.0,
                1,
                Process: null,
                Gc: null,
                Cpu: [],
                Exceptions: null,
                Alloc: null,
                Jit: null)
        {
            ScopeWarnings = ["scope warning"]
        };
        AnalysisResult<TimelineResult> timelineEnvelope = new(timelineResult);
        using JsonDocument timelineDocument = JsonDocument.Parse(OutputJson.Serialize(timelineEnvelope));
        JsonElement timeline = timelineDocument.RootElement.GetProperty("result");
        timeline.TryGetProperty("process", out _).Should().BeFalse();
        timeline.TryGetProperty("gc", out _).Should().BeFalse();
        timeline.TryGetProperty("exceptions", out _).Should().BeFalse();
        timeline.TryGetProperty("alloc", out _).Should().BeFalse();
        timeline.TryGetProperty("jit", out _).Should().BeFalse();
        timeline.TryGetProperty("mode", out _).Should().BeFalse();
        timeline.TryGetProperty("snapshot", out _).Should().BeFalse();
        timeline.TryGetProperty("appliedProcessScope", out _).Should().BeFalse();
        timeline.TryGetProperty("scopeWarnings", out _).Should().BeFalse();
        timeline.GetProperty("cpu").GetArrayLength().Should().Be(0);
        timeline.GetProperty("fromMs").GetDouble().Should().Be(0.0);
    }

    [TestMethod]
    public void Serialize_TraceInfoOmitsUnavailableOptionalSections()
    {
        TraceInfoView view = new(
            "trace.speedscope.json",
            "Speedscope",
            0.0,
            0,
            0.0,
            [],
            []);

        using JsonDocument document = JsonDocument.Parse(
            OutputJson.Serialize(new AnalysisResult<TraceInfoView>(view)));
        JsonElement result = document.RootElement.GetProperty("result");

        result.TryGetProperty("etlxCacheState", out _).Should().BeFalse();
        result.TryGetProperty("analyses", out _).Should().BeFalse();
        result.TryGetProperty("sourceResolution", out _).Should().BeFalse();
        result.TryGetProperty("nativeSymbols", out _).Should().BeFalse();
        result.GetProperty("threads").GetArrayLength().Should().Be(0);
        result.GetProperty("availableAnalyses").GetArrayLength().Should().Be(0);
    }

    [TestMethod]
    public void Serialize_EventBudgetFlagIsPresentOnlyWhenTruncated()
    {
        AnalysisResult<EventQueryResult> completeEnvelope = new(
            new EventQueryResult(0, 0, []));
        using JsonDocument completeDocument = JsonDocument.Parse(OutputJson.Serialize(completeEnvelope));
        JsonElement complete = completeDocument.RootElement.GetProperty("result");
        complete.TryGetProperty("budgetTruncated", out _).Should().BeFalse();
        complete.GetProperty("events").GetArrayLength().Should().Be(0);

        AnalysisResult<EventQueryResult> truncatedEnvelope = new(
            new EventQueryResult(2, 0, [], BudgetTruncated: true));
        using JsonDocument truncatedDocument = JsonDocument.Parse(OutputJson.Serialize(truncatedEnvelope));
        truncatedDocument.RootElement.GetProperty("result")
            .GetProperty("budgetTruncated").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void Serialize_Doubles_RoundedToTwoDecimals()
    {
        RankingResult payload = new(
            100.0,
            "",
            [new RankRow("A", 63.8567, 33.3333)]);
        AnalysisResult<RankingResult> envelope = new(payload);

        string json = OutputJson.Serialize(envelope);

        json.Should().Contain("\"weight\":63.86");
        json.Should().Contain("\"percentOfScope\":33.33");
    }

    [TestMethod]
    public void Serialize_FrameNamesWithAngleBrackets_AreNotOverEscaped()
    {
        RankingResult payload = new(5.0, "", [new RankRow("<root>", 5.0, 100.0)]);
        AnalysisResult<RankingResult> envelope = new(payload);

        string json = OutputJson.Serialize(envelope);

        json.Should().Contain("<root>");
        json.Should().NotContain("\\u003C");
    }

    [TestMethod]
    public void Serialize_DiffRow_EmitsNormalizedAndPerOperationFields()
    {
        DiffRow row = new("Frame", 10.0, 20.0, 10.0)
        {
            BeforePercentOfScope = 25.0,
            AfterPercentOfScope = 40.0,
            PercentagePointChange = 15.0,
            NormalizedWeightChange = 6.0,
            ChangeKind = "appeared",
            BeforeWeightPerOperation = 1.0,
            AfterWeightPerOperation = 2.0,
            PerOperationDelta = 1.0
        };
        AnalysisResult<RankingDiffResult> envelope = new(
            new RankingDiffResult(40.0, 50.0, 10.0, [row])
            {
                OperationUnit = "items"
            });

        string json = OutputJson.Serialize(envelope);

        json.Should().Contain("\"beforePercentOfScope\":25");
        json.Should().Contain("\"afterPercentOfScope\":40");
        json.Should().Contain("\"percentagePointChange\":15");
        json.Should().Contain("\"normalizedWeightChange\":6");
        json.Should().Contain("\"changeKind\":\"appeared\"");
        json.Should().Contain("\"beforeWeightPerOperation\":1");
        json.Should().Contain("\"afterWeightPerOperation\":2");
        json.Should().Contain("\"perOperationDelta\":1");
    }

    [TestMethod]
    public void Serialize_DiffKinds_KeepApplicableEmptyArraysAndOmitUnrelatedFields()
    {
        AnalysisResult<RankingDiffResult> traceEnvelope = new(
            new RankingDiffResult(0.0, 0.0, 0.0, []));
        AnalysisResult<RankingDiffResult> manifestEnvelope = new(
            new RankingDiffResult([]));

        using JsonDocument traceDocument = JsonDocument.Parse(OutputJson.Serialize(traceEnvelope));
        JsonElement trace = traceDocument.RootElement.GetProperty("result");
        trace.GetProperty("kind").GetString().Should().Be(RankingDiffResult.TraceKind);
        trace.GetProperty("rows").GetArrayLength().Should().Be(0);
        trace.TryGetProperty("cases", out _).Should().BeFalse();

        using JsonDocument manifestDocument = JsonDocument.Parse(OutputJson.Serialize(manifestEnvelope));
        JsonElement manifest = manifestDocument.RootElement.GetProperty("result");
        manifest.GetProperty("kind").GetString().Should().Be(RankingDiffResult.ManifestKind);
        manifest.GetProperty("cases").GetArrayLength().Should().Be(0);
        manifest.TryGetProperty("beforeScopeWeight", out _).Should().BeFalse();
        manifest.TryGetProperty("afterScopeWeight", out _).Should().BeFalse();
        manifest.TryGetProperty("scopeDelta", out _).Should().BeFalse();
        manifest.TryGetProperty("rows", out _).Should().BeFalse();
    }

    [TestMethod]
    public void Constructor_DiffCasesInitializer_SelectsManifestKind()
    {
        RankingDiffResult result = new(0.0, 0.0, 0.0, []) { Cases = [] };

        result.Kind.Should().Be(RankingDiffResult.ManifestKind);
    }

    [TestMethod]
    public void Constructor_DiffNullRows_ThrowsArgumentNull()
    {
        Action act = () => _ = new RankingDiffResult(0.0, 0.0, 0.0, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("Rows");
    }

    [TestMethod]
    public void Serialize_Envelope_MatchesGolden()
    {
        string goldenPath = Path.Join(AppContext.BaseDirectory, "Goldens", "ranking-envelope.golden.json");
        string expected = File.ReadAllText(goldenPath).Trim();

        string json = OutputJson.Serialize(SampleEnvelope());

        json.Should().Be(expected);
    }

    [TestMethod]
    public void Serialize_TraceInfoViewPayload_ResolvesThroughSourceGenContext()
    {
        // trace_info's payload is a second closed generic over AnalysisResult; serializing
        // it confirms the source-gen context covers more than the ranking envelope and
        // that the camel-case naming and double rounding apply uniformly.
        TraceInfoView view = new(
            "/traces/sample.nettrace",
            "EventPipe",
            1234.567,
            42,
            0.91234,
            [new ThreadSampleInfo("tid-1", 30)],
            ["cpu", "alloc", "exceptions"])
        {
            Analyses = new Dictionary<string, AnalysisAvailabilityView>
            {
                ["cpu"] = new("enabled", 42),
                ["alloc"] = new("disabled", null),
                ["exceptions"] = new("unknown", null)
            },
            SourceResolution = new SourceResolutionInfo(
                ["/symbols"],
                100,
                25,
                ["MyApp"],
                ["OtherLibrary (0/75 mapped)"])
            {
                PdbIdentityMismatchModules = ["WrongBuild"],
                SampledManagedMethodCount = 12,
                SourceMappedManagedMethodCount = 4,
                UnmappedNamedManagedFrameCount = 60,
                HighestUnmappedMethods = ["OtherLibrary!Run (0/40 mapped)"]
            }
        };
        AnalysisResult<TraceInfoView> envelope = new(view);

        string json = OutputJson.Serialize(envelope);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement result = doc.RootElement.GetProperty("result");
        result.GetProperty("format").GetString().Should().Be("EventPipe");
        result.GetProperty("totalWeight").GetDouble().Should().Be(1234.57);
        result.GetProperty("symbolResolutionRate").GetDouble().Should().Be(0.91);
        result.GetProperty("threads")[0].GetProperty("thread").GetString().Should().Be("tid-1");
        result.GetProperty("availableAnalyses")[0].GetString().Should().Be("cpu");
        result.GetProperty("analyses").GetProperty("cpu").TryGetProperty("formatSupported", out _)
            .Should().BeFalse();
        result.GetProperty("analyses").GetProperty("cpu").GetProperty("eventCount").GetInt32().Should().Be(42);
        result.GetProperty("analyses").GetProperty("alloc").GetProperty("captureStatus").GetString()
            .Should().Be("disabled");
        JsonElement exceptions = result.GetProperty("analyses").GetProperty("exceptions");
        exceptions.GetProperty("captureStatus").GetString().Should().Be("unknown");
        exceptions.TryGetProperty("eventCount", out _).Should().BeFalse();
        JsonElement source = result.GetProperty("sourceResolution");
        source.GetProperty("sourceResolutionRate").GetDouble().Should().Be(0.25);
        source.GetProperty("matchingPdbModules")[0].GetString().Should().Be("MyApp");
        source.GetProperty("pdbIdentityMismatchModules")[0].GetString().Should().Be("WrongBuild");
        source.GetProperty("sampledManagedMethodCount").GetInt32().Should().Be(12);
        source.GetProperty("sourceMappedManagedMethodCount").GetInt32().Should().Be(4);
        source.GetProperty("unmappedNamedManagedFrameCount").GetInt32().Should().Be(60);
        source.GetProperty("highestUnmappedMethods")[0].GetString()
            .Should().Be("OtherLibrary!Run (0/40 mapped)");

        TraceInfoView unavailableView = view with
        {
            SourceResolution = new SourceResolutionInfo([], 100, 0, [], [])
        };
        string unavailableJson = OutputJson.Serialize(
            new AnalysisResult<TraceInfoView>(unavailableView));
        using JsonDocument unavailableDoc = JsonDocument.Parse(unavailableJson);
        JsonElement unavailableSource = unavailableDoc.RootElement
            .GetProperty("result")
            .GetProperty("sourceResolution");
        unavailableSource.TryGetProperty("sampledManagedMethodCount", out _).Should().BeFalse();
        unavailableSource.TryGetProperty("sourceMappedManagedMethodCount", out _).Should().BeFalse();
    }

    [TestMethod]
    public void FromTraceInfo_MapsSharedCliMcpView()
    {
        IReadOnlyDictionary<string, AnalysisAvailability> analyses =
            TraceCapabilities.AvailabilityFor(
                TraceFormat.NetTrace,
                new Dictionary<string, int> { ["cpu"] = 42 },
                new Dictionary<string, CaptureStatus>
                {
                    ["alloc"] = CaptureStatus.Disabled,
                    ["wait"] = CaptureStatus.Unknown
                });
        TraceInfo info = new(
            "/traces/sample.nettrace",
            TraceFormat.NetTrace,
            42.0,
            42,
            1.0,
            [],
            [],
            TraceCapabilities.AnalysesFor(TraceFormat.NetTrace),
            analyses)
        {
            SourceResolution = new SourceResolutionInfo(
                ["/symbols"],
                100,
                25,
                ["MyApp"],
                ["OtherLibrary (0/75 mapped)"])
        };

        TraceInfoView view = TraceInfoView.FromTraceInfo(info, EtlxCacheState.Waited);

        view.EtlxCacheState.Should().Be("waited");
        view.Analyses!["cpu"].Should().Be(new AnalysisAvailabilityView("enabled", 42));
        view.Analyses["alloc"].Should().Be(new AnalysisAvailabilityView("disabled", null));
        view.Analyses["wait"].Should().Be(new AnalysisAvailabilityView("unknown", null));
        view.Analyses.Should().NotContainKey("threadtime");
        view.SourceResolution!.SourceResolutionRate.Should().Be(0.25);
        view.SourceResolution.MatchingPdbModules.Should().Equal("MyApp");
    }

    [TestMethod]
    public void FromTraceInfo_InvalidInput_Throws()
    {
        Action nullInfo = () => TraceInfoView.FromTraceInfo(null!, null);
        TraceInfo invalidCaptureInfo = CreateTraceInfo((CaptureStatus)999);
        Action invalidCapture = () => TraceInfoView.FromTraceInfo(invalidCaptureInfo, null);
        TraceInfo validInfo = CreateTraceInfo(CaptureStatus.Enabled);
        Action invalidCache = () => TraceInfoView.FromTraceInfo(validInfo, (EtlxCacheState)999);

        nullInfo.Should().Throw<ArgumentNullException>().WithParameterName("info");
        invalidCapture.Should().Throw<ArgumentOutOfRangeException>();
        invalidCache.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void Serialize_UnregisteredPayloadType_Throws()
    {
        // The source-gen context is the only type-info resolver, so a payload type that
        // is not declared in FiltraceJsonContext has no metadata and cannot be serialized.
        // This pins the maintenance invariant: every new payload type must be registered.
        AnalysisResult<int> envelope = new(42);

        Action act = () => OutputJson.Serialize(envelope);

        act.Should().Throw<NotSupportedException>();
    }

    private static TraceInfo CreateTraceInfo(CaptureStatus captureStatus) =>
        new(
            "/traces/sample.nettrace",
            TraceFormat.NetTrace,
            1.0,
            1,
            1.0,
            [],
            [],
            ["cpu"],
            new Dictionary<string, AnalysisAvailability>
            {
                ["cpu"] = new(true, captureStatus, 1)
            });
}
