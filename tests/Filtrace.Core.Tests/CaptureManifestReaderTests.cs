// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Touki;

namespace Filtrace.Tracing;

[TestClass]
public sealed class CaptureManifestReaderTests
{
    [TestMethod]
    public void BoundFrame_ControlCharactersAndLongInput_ReturnsSafeDisplayText()
    {
        string frame = $"Bad\tFrame\0{new string('x', 200)}";

        string bounded = CaptureManifestOutput.BoundFrame(frame);

        bounded.Should().HaveLength(CaptureManifestOutput.MaxFrameLength);
        bounded.Should().StartWith("Bad Frame ");
        bounded.Any(char.IsControl).Should().BeFalse();
    }

    [TestMethod]
    public void BoundFrame_TruncationAtSurrogatePair_PreservesValidUtf16()
    {
        string frame = $"{new string('x', CaptureManifestOutput.MaxFrameLength - 1)}😀suffix";

        string bounded = CaptureManifestOutput.BoundFrame(frame);

        bounded.Should().HaveLength(CaptureManifestOutput.MaxFrameLength - 1);
        char.IsSurrogate(bounded[^1]).Should().BeFalse();
    }

    [TestMethod]
    public void Read_CommandManifest_PreservesKindAndInvocations()
    {
        using TemporaryManifest manifest = new(
            """
            {"schemaVersion":2,"kind":"command","cases":[
              {"id":"warm","benchmark":"dotnet --version","parameters":"","trace":"warm.etl","invocations":[
                {"ordinal":1,"processId":100,"exitCode":0,"startedUtc":"2026-07-31T10:00:00.0000000+00:00","stoppedUtc":"2026-07-31T10:00:00.0500000+00:00"},
                {"ordinal":2,"processId":101,"exitCode":3,"startedUtc":"2026-07-31T10:00:01.0000000+00:00","stoppedUtc":"2026-07-31T10:00:01.0400000+00:00"}
              ]}
            ]}
            """);

        CaptureManifest read = CaptureManifestReader.Read(manifest.ManifestPath);

        read.Kind.Should().Be(CaptureKind.Command);
        CaptureManifestCase single = read.Cases.Should().ContainSingle().Subject;
        single.Invocations.Should().HaveCount(2);
        single.Invocations[1].ProcessId.Should().Be(101);
        single.Invocations[1].ExitCode.Should().Be(3);
        single.Invocations[0].StoppedUtc.Should().BeAfter(single.Invocations[0].StartedUtc);
    }

    [TestMethod]
    public void Read_AllFailedCommandManifest_AcceptsDiagnosticEnvelopeWithoutCases()
    {
        using TemporaryManifest manifest = new(
            """
            {"schemaVersion":2,"kind":"command","iterations":2,"cases":[],"failedCases":[
              {"id":"invalid","status":"invalidResult","collectExitCode":0,"diagnostic":"missing processId"}
            ],"warnings":["invalid result"]}
            """);

        CaptureManifest read = CaptureManifestReader.Read(manifest.ManifestPath);

        read.Kind.Should().Be(CaptureKind.Command);
        read.Cases.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_SchemaVersionOneWithoutKind_StillReadsAsABenchmarkCapture()
    {
        // Version 1 manifests predate the discriminator, so they have to keep working with
        // batch and diff rather than becoming unreadable when the schema moved.
        using TemporaryManifest manifest = new(
            """
            {"schemaVersion":1,"cases":[{"id":"a","benchmark":"Bench.Work","trace":"a.nettrace"}]}
            """);

        CaptureManifest read = CaptureManifestReader.Read(manifest.ManifestPath);

        read.Kind.Should().Be(CaptureKind.Benchmark);
        read.Cases.Should().ContainSingle().Which.Invocations.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_UnknownKind_Rejects()
    {
        using TemporaryManifest manifest = new(
            """
            {"schemaVersion":2,"kind":"something-else","cases":[{"id":"a","trace":"a.etl"}]}
            """);

        Action act = () => CaptureManifestReader.Read(manifest.ManifestPath);

        act.Should().Throw<InvalidDataException>().WithMessage("*not recognized*");
    }

    [TestMethod]
    public void Read_UnsupportedSchemaVersion_Rejects()
    {
        using TemporaryManifest manifest = new(
            """
            {"schemaVersion":3,"cases":[{"id":"a","trace":"a.etl"}]}
            """);

        Action act = () => CaptureManifestReader.Read(manifest.ManifestPath);

        act.Should().Throw<InvalidDataException>().WithMessage("*must be 1 or 2*");
    }

    [TestMethod]
    public void Read_InvocationMissingATimestamp_Rejects()
    {
        using TemporaryManifest manifest = new(
            """
            {"schemaVersion":2,"kind":"command","cases":[
              {"id":"a","trace":"a.etl","invocations":[{"ordinal":1,"processId":1,"exitCode":0}]}
            ]}
            """);

        Action act = () => CaptureManifestReader.Read(manifest.ManifestPath);

        act.Should().Throw<InvalidDataException>().WithMessage("*timestamp*");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Read_InvocationWithNonPositiveProcessId_Rejects(int processId)
    {
        using TemporaryManifest manifest = new(
            $$"""
            {"schemaVersion":2,"kind":"command","cases":[
              {"id":"a","trace":"a.etl","invocations":[{"ordinal":1,"processId":{{processId}},"exitCode":0,"startedUtc":"2026-08-03T00:00:00Z","stoppedUtc":"2026-08-03T00:00:01Z"}]}
            ]}
            """);

        Action act = () => CaptureManifestReader.Read(manifest.ManifestPath);

        act.Should().Throw<InvalidDataException>().WithMessage("*processId must be positive*");
    }

    [TestMethod]
    public void Read_MoreInvocationsThanTheToolCanCapture_Rejects()
    {
        string invocations = string.Join(
            ",",
            Enumerable.Range(1, 1001).Select(static ordinal =>
                $$"""{"ordinal":{{ordinal}},"processId":{{ordinal}},"exitCode":0,"startedUtc":"2026-07-31T10:00:00.0000000+00:00","stoppedUtc":"2026-07-31T10:00:00.0100000+00:00"}"""));

        using TemporaryManifest manifest = new(
            $$"""
            {"schemaVersion":2,"kind":"command","cases":[{"id":"a","trace":"a.etl","invocations":[{{invocations}}]}]}
            """);

        Action act = () => CaptureManifestReader.Read(manifest.ManifestPath);

        act.Should().Throw<InvalidDataException>().WithMessage("*maximum is 1000*");
    }

    private sealed class TemporaryManifest : DisposableBase
    {
        private readonly string _directory;

        public TemporaryManifest(string json)
        {
            _directory = Path.Join(Path.GetTempPath(), $"filtrace-manifest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            ManifestPath = Path.Join(_directory, "manifest.json");
            File.WriteAllText(ManifestPath, json);
        }

        public string ManifestPath { get; }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory must not fail a passing test.
            }
        }
    }

    [TestMethod]
    public void Read_ParameterCasesAndOperationStates_PreservesIdentityAndMetadata()
    {
        string directory = Path.Join(Path.GetTempPath(), $"filtrace-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Join(directory, "manifest.json");
        File.WriteAllText(
            path,
            """
            {"schemaVersion":1,"process":"Bench","cases":[
              {"id":"a","benchmark":"Bench.Work","benchmarkDisplay":"Work(Size: 1): Job-A","trace":"a.nettrace"},
              {"id":"b","benchmark":"Bench.Work","parameters":"Size: 2","benchmarkDisplay":"Work(Size: 2): Job-B","trace":"b.nettrace","operationCount":100},
              {"id":"c","benchmark":"Bench.Other","parameters":"","benchmarkDisplay":"Other: Job-C","speedscope":"c.speedscope.json","symbolsDirectory":"symbols","operationCount":200,"operationUnit":"items"}
            ]}
            """);

        try
        {
            CaptureManifest manifest = CaptureManifestReader.Read(path);

            manifest.Process.Should().Be("Bench");
            manifest.Cases.Should().HaveCount(3);
            manifest.Cases[0].Parameters.Should().Be("Size: 1");
            manifest.Cases[0].PairingKey.Should().Be("Bench.Work\0Size: 1");
            manifest.Cases[0].HasCompleteOperationMetadata.Should().BeFalse();
            manifest.Cases[1].OperationCount.Should().Be(100);
            manifest.Cases[1].OperationUnit.Should().BeNull();
            manifest.Cases[1].HasCompleteOperationMetadata.Should().BeFalse();
            manifest.Cases[2].HasCompleteOperationMetadata.Should().BeTrue();
            manifest.Cases[2].OperationUnit.Should().Be("items");
            manifest.Cases[2].TracePath.Should().Be(Path.Join(directory, "c.speedscope.json"));
            manifest.Cases[2].SymbolsDirectory.Should().Be(Path.Join(directory, "symbols"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("BinaryFormattedObjectPerf.Parse: Dry(...) [Scenario=SerializableCallback]")]
    [DataRow("BinaryFormattedObjectPerf.Parse: Dry(...) [Scenario=SerializableCallback] ")]
    public void ExtractParameters_BracketedDisplay_ReturnsParameters(string display)
    {
        CaptureManifestReader.ExtractParameters(display).Should().Be("Scenario=SerializableCallback");
    }

    [TestMethod]
    public void Read_DurableManifestSizeBoundary_AcceptsCompleteLargeFileAndRejectsLimit()
    {
        string path = Path.Join(Path.GetTempPath(), $"filtrace-manifest-{Guid.NewGuid():N}.json");
        const string prefix = "{\"schemaVersion\":1,\"padding\":\"";
        const string suffix = "\",\"cases\":[{\"id\":\"a\",\"benchmark\":\"Bench.Work\",\"trace\":\"a.nettrace\"}]}";
        try
        {
            int acceptedPaddingLength = CaptureManifestReader.MaxManifestBytes
                - 1
                - prefix.Length
                - suffix.Length;

            string accepted = $"{prefix}{new string('x', acceptedPaddingLength)}{suffix}";
            File.WriteAllText(path, accepted);
            new FileInfo(path).Length.Should().Be(CaptureManifestReader.MaxManifestBytes - 1);

            CaptureManifest manifest = CaptureManifestReader.Read(path);

            manifest.Cases.Should().ContainSingle();
            new FileInfo(path).Length.Should().BeGreaterThan(20 * 1024);

            string rejected = $"{prefix}{new string('x', acceptedPaddingLength + 1)}{suffix}";
            File.WriteAllText(path, rejected);
            Action atLimit = () => CaptureManifestReader.Read(path);
            atLimit.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Read_MalformedAndOversizedInput_ThrowsInvalidDataException()
    {
        string path = Path.Join(Path.GetTempPath(), $"filtrace-manifest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"cases\":[{\"id\":\"a\"}]}");
            Action missingTrace = () => CaptureManifestReader.Read(path);
            missingTrace.Should().Throw<InvalidDataException>();

            string longParameters = new('x', 513);
            File.WriteAllText(
                path,
                $$"""
                {"schemaVersion":1,"cases":[{"id":"a","benchmark":"Bench.Work","benchmarkDisplay":"Work({{longParameters}}): Job-A","trace":"a.nettrace"}]}
                """);

            Action derivedParametersTooLong = () => CaptureManifestReader.Read(path);
            derivedParametersTooLong.Should().Throw<InvalidDataException>();

            File.WriteAllText(path, new string('x', CaptureManifestReader.MaxManifestBytes));
            Action oversized = () => CaptureManifestReader.Read(path);
            oversized.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Read_DuplicateProperties_ThrowsInvalidDataException()
    {
        string path = Path.Join(Path.GetTempPath(), $"filtrace-manifest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"schemaVersion":1,"schemaVersion":1,"cases":[]}""");
            Action duplicateRoot = () => CaptureManifestReader.Read(path);
            duplicateRoot.Should().Throw<InvalidDataException>();

            File.WriteAllText(
                path,
                """{"schemaVersion":1,"cases":[{"id":"a","id":"b","trace":"a.nettrace"}]}""");

            Action duplicateCase = () => CaptureManifestReader.Read(path);
            duplicateCase.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Read_CaseCountBoundary_Accepts256AndRejects257()
    {
        string path = Path.Join(Path.GetTempPath(), $"filtrace-manifest-{Guid.NewGuid():N}.json");
        try
        {
            string Cases(int count) => string.Join(",", Enumerable.Range(0, count).Select(
                index => $"{{\"id\":\"{index}\",\"benchmark\":\"B\",\"trace\":\"t\"}}"));

            File.WriteAllText(path, $"{{\"schemaVersion\":1,\"cases\":[{Cases(256)}]}}");

            CaptureManifest manifest = CaptureManifestReader.Read(path);

            manifest.Cases.Should().HaveCount(256);

            File.WriteAllText(path, $"{{\"schemaVersion\":1,\"cases\":[{Cases(257)}]}}");
            Action overLimit = () => CaptureManifestReader.Read(path);
            overLimit.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Read_RelativeTraversalPath_NormalizesAgainstManifestDirectory()
    {
        string directory = Path.Join(Path.GetTempPath(), $"filtrace-manifest-{Guid.NewGuid():N}");
        string nested = Path.Join(directory, "nested");
        Directory.CreateDirectory(nested);
        string path = Path.Join(nested, "manifest.json");
        File.WriteAllText(
            path,
            """{"schemaVersion":1,"cases":[{"id":"a","benchmark":"B","trace":"../outside.nettrace"}]}""");

        try
        {
            CaptureManifest manifest = CaptureManifestReader.Read(path);

            manifest.Cases[0].TracePath.Should().Be(Path.Join(directory, "outside.nettrace"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Pair_JobLabelsDiffer_MatchesByBenchmarkAndParameters()
    {
        CaptureManifest before = Manifest(
            Case("before-1", "Bench.Work", "Size: 1", "Work(Size: 1): Job-OLD"),
            Case("before-2", "Bench.Work", "Size: 2", "Work(Size: 2): Job-OLD"));

        CaptureManifest after = Manifest(
            Case("after-2", "Bench.Work", "Size: 2", "Work(Size: 2): Job-NEW"),
            Case("after-1", "Bench.Work", "Size: 1", "Work(Size: 1): Job-NEW"));

        CaptureManifestPairResult result = CaptureManifestPairer.Pair(before, after);

        result.Warnings.Should().BeEmpty();
        result.Pairs.Select(static pair => pair.Before.Id).Should().Equal("before-1", "before-2");
        result.Pairs.Select(static pair => pair.After.Id).Should().Equal("after-1", "after-2");
    }

    [TestMethod]
    public void Pair_DuplicateAndUnmatchedKeys_FailsClosedOrWarns()
    {
        CaptureManifest duplicate = Manifest(
            Case("a", "Bench.Work", "Size: 1", "A"),
            Case("b", "Bench.Work", "Size: 1", "B"));

        Action duplicatePair = () => CaptureManifestPairer.Pair(duplicate, Manifest());
        duplicatePair.Should().Throw<InvalidDataException>();

        CaptureManifestPairResult unmatched = CaptureManifestPairer.Pair(
            Manifest(Case("before", "Bench.Work", "Size: 1", "Before")),
            Manifest(Case("after", "Bench.Work", "Size: 2", "After")));

        unmatched.Pairs.Should().BeEmpty();
        unmatched.Warnings.Should().HaveCount(2);

        CaptureManifestPairResult unresolvedCurrent = CaptureManifestPairer.Pair(
            Manifest(),
            new CaptureManifest(
                "manifest.json",
                    Process: null,
                    [new CaptureManifestCase("unknown", Benchmark: null, "", "Unknown", "trace.nettrace", SymbolsDirectory: null, OperationCount: null, OperationUnit: null)]));

        unresolvedCurrent.Warnings.Should().ContainSingle(
            warning => warning.Contains("current case 'unknown'", StringComparison.Ordinal)
                && warning.Contains("analyze its trace directly", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Analyze_OperationMetadataStates_OnlyCompletePairGetsPerOperationValues()
    {
        CaptureManifest before = Manifest(
            Case("before-absent", "Bench.Work", "Mode: Absent", "Absent"),
            Case("before-count", "Bench.Work", "Mode: Count", "Count") with
            {
                OperationCount = 10
            },
            Case("before-complete", "Bench.Work", "Mode: Complete", "Complete") with
            {
                OperationCount = 10,
                OperationUnit = "items"
            },
            Case("before-mismatch", "Bench.Work", "Mode: Mismatch", "Mismatch") with
            {
                OperationCount = 10,
                OperationUnit = "items"
            },
            Case("before-failed", "Bench.Work", "Mode: Failed", "Failed"));

        CaptureManifest after = Manifest(
            Case("after-absent", "Bench.Work", "Mode: Absent", "Absent"),
            Case("after-count", "Bench.Work", "Mode: Count", "Count") with
            {
                OperationCount = 20
            },
            Case("after-complete", "Bench.Work", "Mode: Complete", "Complete") with
            {
                OperationCount = 20,
                OperationUnit = "items"
            },
            Case("after-mismatch", "Bench.Work", "Mode: Mismatch", "Mismatch") with
            {
                OperationCount = 20,
                OperationUnit = "operations"
            },
            Case("after-failed", "Bench.Work", "Mode: Failed", "Failed"));

        Dictionary<string, LoadedTrace> traces = new(StringComparer.Ordinal)
        {
            ["before-absent.nettrace"] = Loaded("before-absent", 38.0, 62.0),
            ["after-absent.nettrace"] = Loaded("after-absent", 2.0, 98.0),
            ["before-count.nettrace"] = Loaded("before-count", 40.0, 60.0),
            ["after-count.nettrace"] = Loaded("after-count", 20.0, 80.0),
            ["before-complete.nettrace"] = Loaded("before-complete", 50.0, 50.0),
            ["after-complete.nettrace"] = Loaded("after-complete", 60.0, 60.0),
            ["before-mismatch.nettrace"] = Loaded("before-mismatch", 50.0, 50.0),
            ["after-mismatch.nettrace"] = Loaded("after-mismatch", 60.0, 60.0)
        };

        CaptureManifestDiffAnalysis analysis = CaptureManifestDiffAnalyzer.Analyze(
            before,
            after,
            inclusive: false,
            root: "",
            FrameNames.DefaultFoldPatterns,
            top: 25,
            (_, captureCase) => traces.TryGetValue(captureCase.TracePath, out LoadedTrace? trace)
                ? trace
                : throw new FileNotFoundException("case trace not present"));

        analysis.Result.Cases.Should().HaveCount(5);
        analysis.Warnings.Should().ContainSingle(
            warning => warning.Contains("capped to 5", StringComparison.Ordinal));

        RankingDiffCaseResult absent = analysis.Result.Cases[0];
        absent.OperationUnit.Should().BeNull();
        absent.Warnings.Should().BeEmpty();
        absent.Rows.Single(row => row.Frame == "Hot").BeforePercentOfScope.Should().Be(38.0);
        absent.Rows.Single(row => row.Frame == "Hot").AfterPercentOfScope.Should().Be(2.0);
        absent.Rows.Single(row => row.Frame == "Hot").PercentagePointChange.Should().Be(-36.0);
        RankingDiffCaseResult countOnly = analysis.Result.Cases[1];
        countOnly.OperationUnit.Should().BeNull();
        countOnly.Warnings.Should().ContainSingle(
            warning => warning.Contains("per-operation values omitted", StringComparison.Ordinal));

        RankingDiffCaseResult complete = analysis.Result.Cases[2];
        complete.OperationUnit.Should().Be("items");
        complete.BeforeScopeWeightPerOperation.Should().Be(10.0);
        complete.AfterScopeWeightPerOperation.Should().Be(6.0);
        complete.ScopeWeightPerOperationDelta.Should().Be(-4.0);
        RankingDiffCaseResult mismatch = analysis.Result.Cases[3];
        mismatch.OperationUnit.Should().BeNull();
        mismatch.Warnings.Should().ContainSingle(
            warning => warning.Contains("same operationUnit", StringComparison.Ordinal));

        RankingDiffCaseResult failed = analysis.Result.Cases[4];
        failed.Rows.Should().BeEmpty();
        failed.Warnings.Should().ContainSingle(
            warning => warning.Contains("not present", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AnalyzeBatch_MixedCaseOutcomes_ReturnsCaseSpecificRowsAndWarnings()
    {
        CaptureManifest manifest = Manifest(
            Case("complete", "Bench.Work", "Mode: Complete", "Complete") with
            {
                OperationCount = 10,
                OperationUnit = "items"
            },
            Case("thin", "Bench.Work", "Mode: Thin", "Thin"),
            Case("failed", "Bench.Work", "Mode: Failed", "Failed"));

        Dictionary<string, LoadedTrace> traces = new(StringComparer.Ordinal)
        {
            ["complete.nettrace"] = Loaded("complete", 60.0, 40.0),
            ["thin.nettrace"] = Loaded(
                "thin",
                1.0,
                1.0,
                StackRecordSemantics.PeriodicCpuSamples)
        };

        BatchRankingResult result = CaptureManifestBatchAnalyzer.Analyze(
            manifest,
            "cpu",
            inclusive: false,
            root: "",
            FrameNames.DefaultFoldPatterns,
            (_, captureCase) => traces.TryGetValue(captureCase.TracePath, out LoadedTrace? trace)
                ? trace
                : throw new FileNotFoundException("missing case trace"));

        result.Cases.Should().HaveCount(3);
        BatchRankingCaseResult complete = result.Cases[0];
        complete.CaseId.Should().Be("complete");
        complete.TopFrame.Should().Be("Hot");
        complete.TopPercentOfScope.Should().Be(60.0);
        complete.OperationUnit.Should().Be("items");
        complete.ScopeWeightPerOperation.Should().Be(10.0);
        complete.TopWeightPerOperation.Should().Be(6.0);
        result.Cases[1].Warnings.Should().Contain(
            warning => warning.Contains("periodic CPU records", StringComparison.Ordinal)
                && warning.Contains("at least 200", StringComparison.Ordinal));

        result.Cases[2].Warnings.Should().ContainSingle("missing case trace");
        result.Cases[2].CaseId.Should().Be("failed");
        result.Cases[2].TopFrame.Should().BeNull();
    }

    [TestMethod]
    public void AnalyzeBatch_RootScope_CarriesPerCaseCoverage()
    {
        CaptureManifest manifest = Manifest(Case("rooted", "Bench.Work", "", "Rooted"));

        BatchRankingResult result = CaptureManifestBatchAnalyzer.Analyze(
            manifest,
            "cpu",
            inclusive: false,
            root: "Hot",
            FrameNames.DefaultFoldPatterns,
            (_, _) => Loaded("rooted", 40.0, 60.0));

        RootScopeCoverage coverage = result.Cases.Single().RootCoverage!;
        coverage.AvailableWeight.Should().Be(100.0);
        coverage.RetainedWeight.Should().Be(40.0);
        coverage.RetainedPercent.Should().Be(40.0);
        coverage.AvailableRecordCount.Should().Be(2);
        coverage.RetainedRecordCount.Should().Be(1);
    }

    [TestMethod]
    public void AnalyzeManifestDiff_RootScope_CarriesPerSideCoverage()
    {
        CaptureManifest before = Manifest(Case("before", "Bench.Work", "", "Before"));
        CaptureManifest after = Manifest(Case("after", "Bench.Work", "", "After"));

        CaptureManifestDiffAnalysis analysis = CaptureManifestDiffAnalyzer.Analyze(
            before,
            after,
            inclusive: false,
            root: "Hot",
            FrameNames.DefaultFoldPatterns,
            top: 5,
            (manifest, _) => manifest == before
                ? Loaded("before", 25.0, 75.0)
                : Loaded("after", 50.0, 50.0));

        RankingDiffCaseResult result = analysis.Result.Cases.Single();
        result.BeforeRootCoverage!.AvailableWeight.Should().Be(100.0);
        result.BeforeRootCoverage.RetainedWeight.Should().Be(25.0);
        result.AfterRootCoverage!.AvailableWeight.Should().Be(100.0);
        result.AfterRootCoverage.RetainedWeight.Should().Be(50.0);
    }

    [TestMethod]
    public void AnalyzeBatch_UnresolvedIdentity_SkipsCaseAndRequiresDirectTraceAnalysis()
    {
        CaptureManifest manifest = new(
            "manifest.json",
                Process: null,
                [new CaptureManifestCase("unknown", Benchmark: null, "", "Unknown", "unknown.nettrace", SymbolsDirectory: null, OperationCount: null, OperationUnit: null)]);

        int loadCount = 0;

        BatchRankingResult result = CaptureManifestBatchAnalyzer.Analyze(
            manifest,
            "cpu",
            inclusive: false,
            root: "",
            FrameNames.DefaultFoldPatterns,
            (_, _) =>
            {
                loadCount++;
                return Loaded("unknown", 1.0, 1.0);
            });

        loadCount.Should().Be(0);
        BatchRankingCaseResult unresolved = result.Cases.Should().ContainSingle().Subject;
        unresolved.CaseId.Should().Be("unknown");
        unresolved.TopFrame.Should().BeNull();
        unresolved.Warnings.Should().ContainSingle(
            warning => warning.Contains("manifest batch skipped", StringComparison.Ordinal)
                && warning.Contains("analyze the trace directly", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AnalyzeBatch_MaxDegreeOverload_PreservesSequentialCaseOrder()
    {
        CaptureManifest manifest = Manifest(
            Case("first", "Bench.Work", "Size: 1", "First"),
            Case("second", "Bench.Work", "Size: 2", "Second"));

        List<string> loaded = [];

        BatchRankingResult result = CaptureManifestBatchAnalyzer.Analyze(
            manifest,
            "cpu",
            inclusive: false,
            root: "",
            FrameNames.DefaultFoldPatterns,
            maxDegreeOfParallelism: CaptureManifestBatchAnalyzer.MaxAnalyzedCases,
            (_, captureCase) =>
            {
                loaded.Add(captureCase.Id);
                return Loaded(captureCase.Id, 2.0, 1.0);
            });

        loaded.Should().Equal("first", "second");
        result.Cases.Select(static captureCase => captureCase.CaseId)
            .Should().Equal("first", "second");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(CaptureManifestBatchAnalyzer.MaxAnalyzedCases + 1)]
    public void AnalyzeBatch_MaxDegreeOverload_DegreeOutsideSupportedRangeThrows(int maxDegreeOfParallelism)
    {
        Action analyze = () => CaptureManifestBatchAnalyzer.Analyze(
            Manifest(),
            "cpu",
            inclusive: false,
            root: "",
            FrameNames.DefaultFoldPatterns,
            maxDegreeOfParallelism,
            (_, _) => Loaded("unused", 1.0, 1.0));

        analyze.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void AnalyzeDiff_MaxDegreeOverload_PreservesSequentialPairOrder()
    {
        CaptureManifest before = Manifest(
            Case("before-first", "Bench.Work", "Size: 1", "First"),
            Case("before-second", "Bench.Work", "Size: 2", "Second"));

        CaptureManifest after = Manifest(
            Case("after-first", "Bench.Work", "Size: 1", "First"),
            Case("after-second", "Bench.Work", "Size: 2", "Second"));

        List<string> loaded = [];

        CaptureManifestDiffAnalysis result = CaptureManifestDiffAnalyzer.Analyze(
            before,
            after,
            inclusive: false,
            root: "",
            FrameNames.DefaultFoldPatterns,
            top: 5,
            maxDegreeOfParallelism: CaptureManifestDiffAnalyzer.MaxAnalyzedCases,
            (_, captureCase) =>
            {
                loaded.Add(captureCase.Id);
                return Loaded(captureCase.Id, 2.0, 1.0);
            });

        loaded.Should().Equal("before-first", "after-first", "before-second", "after-second");
        result.Result.Cases.Select(static captureCase => captureCase.Parameters)
            .Should().Equal("Size: 1", "Size: 2");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(CaptureManifestDiffAnalyzer.MaxAnalyzedCases + 1)]
    public void AnalyzeDiff_MaxDegreeOverload_DegreeOutsideSupportedRangeThrows(int maxDegreeOfParallelism)
    {
        Action analyze = () => CaptureManifestDiffAnalyzer.Analyze(
            Manifest(),
            Manifest(),
            inclusive: false,
            root: "",
            FrameNames.DefaultFoldPatterns,
            top: 5,
            maxDegreeOfParallelism,
            (_, _) => Loaded("unused", 1.0, 1.0));

        analyze.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static CaptureManifest Manifest(params CaptureManifestCase[] cases) =>
        new("manifest.json", Process: null, cases);

    private static CaptureManifestCase Case(
        string id,
        string benchmark,
        string parameters,
        string display) =>
            new(id, benchmark, parameters, display, $"{id}.nettrace", SymbolsDirectory: null, OperationCount: null, OperationUnit: null);

    private static LoadedTrace Loaded(
        string path,
        double hotWeight,
        double otherWeight,
        StackRecordSemantics recordSemantics = StackRecordSemantics.EventedIntervals)
    {
        SampleStack[] samples =
        [
            new(["Hot"], hotWeight),
            new(["Other"], otherWeight)
        ];

        TraceInfo info = new(
            path,
            TraceFormat.Speedscope,
            hotWeight + otherWeight,
            samples.Length,
            1.0,
            [],
            [],
            ["cpu"]);

        return new LoadedTrace(
            info,
            new StackSampleSource(MetricInfo.Cpu, samples, recordSemantics));
    }
}
