// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

[TestClass]
public sealed class DiffExecutorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string Speedscope => FixturePath("folding.speedscope.json");

    // A minimal evented speedscope where frame A wraps frame B: the B close at
    // 'bCloseAt' fixes B's self-weight, and A's self-weight is the remainder up to
    // 'aCloseAt'. Authoring both sides lets a test assert exact diff deltas.
    private static string TwoFrameSpeedscope(int bCloseAt, int aCloseAt) =>
        $$"""
        {"shared":{"frames":[{"name":"A"},{"name":"B"}]},
         "profiles":[{"type":"evented","name":"t","unit":"milliseconds","startValue":0,"endValue":{{aCloseAt}},
          "events":[{"type":"O","frame":0,"at":0},{"type":"O","frame":1,"at":0},
                    {"type":"C","frame":1,"at":{{bCloseAt}}},{"type":"C","frame":0,"at":{{aCloseAt}}}]}]}
        """;

    private static DiffRequest Request(
        string beforePath,
        string afterPath,
        Measure measure = Measure.Self,
        string root = "",
        int top = 25,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        IReadOnlyList<string>? fold = null,
        ScopeRequest? scope = null) =>
        new(
            beforePath,
            afterPath,
            root,
            top,
            fold ?? FrameNames.DefaultFoldPatterns,
            measure,
            format,
            Symbols: null,
            strict,
            scope);

    private static (int Exit, string Out, string Error) Run(DiffRequest request)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = DiffExecutor.Run(request, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static (int Exit, string Out, string Error) Run(BatchRequest request)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = BatchExecutor.Run(request, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [TestMethod]
    public void Run_SameTraceTwice_ReportsNoChanges()
    {
        (int exit, string output, _) = Run(Request(Speedscope, Speedscope));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("no changes in scope");
    }

    [TestMethod]
    public void Run_ChangedWeights_ShowsPerFrameDeltas()
    {
        // before: B self 5, A self 5; after: B self 8, A self 2.
        string before = Path.Combine(Path.GetTempPath(), $"filtrace-before-{Guid.NewGuid():N}.speedscope.json");
        string after = Path.Combine(Path.GetTempPath(), $"filtrace-after-{Guid.NewGuid():N}.speedscope.json");
        File.WriteAllText(before, TwoFrameSpeedscope(bCloseAt: 5, aCloseAt: 10));
        File.WriteAllText(after, TwoFrameSpeedscope(bCloseAt: 8, aCloseAt: 10));
        try
        {
            (int exit, string output, _) = Run(Request(before, after, format: OutputFormat.Json));

            exit.Should().Be(ExitCodes.Success);
            // B regressed +3, A improved -3; both rows present.
            output.Should().Contain("\"frame\":\"B\"");
            output.Should().Contain("\"frame\":\"A\"");
            output.Should().Contain("\"delta\":3");
            output.Should().Contain("\"delta\":-3");
        }
        finally
        {
            File.Delete(before);
            File.Delete(after);
        }
    }

    [TestMethod]
    public void Run_ChangedWeights_TextShowsNormalizedColumns()
    {
        string before = Path.Combine(Path.GetTempPath(), $"filtrace-before-{Guid.NewGuid():N}.speedscope.json");
        string after = Path.Combine(Path.GetTempPath(), $"filtrace-after-{Guid.NewGuid():N}.speedscope.json");
        File.WriteAllText(before, TwoFrameSpeedscope(bCloseAt: 5, aCloseAt: 10));
        File.WriteAllText(after, TwoFrameSpeedscope(bCloseAt: 8, aCloseAt: 10));
        try
        {
            (int exit, string output, _) = Run(Request(before, after));

            exit.Should().Be(ExitCodes.Success);
            output.Should().Contain("before %");
            output.Should().Contain("after %");
            output.Should().Contain("pp");
            output.Should().Contain("changed");
        }
        finally
        {
            File.Delete(before);
            File.Delete(after);
        }
    }

    [TestMethod]
    public void Run_JsonFormat_WritesSingleLineEnvelope()
    {
        (int exit, string output, _) = Run(Request(Speedscope, Speedscope, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        string json = output.Trim();
        json.Should().NotContain("\n");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"scopeDelta\"");
    }

    [TestMethod]
    public void Run_InvalidFoldPattern_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(Speedscope, Speedscope, fold: ["("]));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Run_MissingBeforeFile_ReturnsInputError()
    {
        (int exit, _, string error) = Run(Request(FixturePath("nope.nettrace"), Speedscope));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Run_MissingAfterFile_ReturnsInputError()
    {
        (int exit, _, string error) = Run(Request(Speedscope, FixturePath("nope.nettrace")));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Run_ProcessScopeOnNetTrace_IsHarmlessNoOp()
    {
        string path = FixturePath("activity.nettrace");

        (int exit, _, string error) = Run(Request(
            path,
            path,
            scope: ScopeRequest.ForProcess("definitely-not-a-process")));

        exit.Should().Be(ExitCodes.Success);
        error.Should().BeEmpty();
    }

    [TestMethod]
    public void Run_ExplicitMissingEtlProcess_WarnsForBothEmptyScopes()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("ETL process scope is Windows-only.");
        }

        string path = FixturePath("etw.etl");

        (int exit, string output, string error) = Run(Request(
            path,
            path,
            scope: ScopeRequest.ForProcess("definitely-not-a-process")));

        exit.Should().Be(ExitCodes.Success);
        error.Should().BeEmpty();
        output.Should().Contain("baseline: No samples remained after scoping");
        output.Should().Contain("current: No samples remained after scoping");
        output.Should().Contain("definitely-not-a-process");
    }

    [TestMethod]
    public void Run_PeriodicThinRoot_PrefixesWarningsForBothSides()
    {
        string path = FixturePath("activity.nettrace");

        (int exit, string output, _) = Run(Request(path, path, root: "ActivityLoop"));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("baseline: Only 180 periodic CPU records");
        output.Should().Contain("current: Only 180 periodic CPU records");
    }

    [TestMethod]
    public void Run_PairedManifests_ReportsNormalizedCaseAndPerOperationValues()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"filtrace-diff-{Guid.NewGuid():N}");
        string beforeDirectory = Path.Combine(directory, "before");
        string afterDirectory = Path.Combine(directory, "after");
        Directory.CreateDirectory(beforeDirectory);
        Directory.CreateDirectory(afterDirectory);
        string beforeTrace = Path.Combine(beforeDirectory, "case.speedscope.json");
        string afterTrace = Path.Combine(afterDirectory, "case.speedscope.json");
        string beforeManifest = Path.Combine(beforeDirectory, "manifest.json");
        string afterManifest = Path.Combine(afterDirectory, "manifest.json");
        try
        {
            File.WriteAllText(beforeTrace, TwoFrameSpeedscope(bCloseAt: 4, aCloseAt: 10));
            File.WriteAllText(afterTrace, TwoFrameSpeedscope(bCloseAt: 8, aCloseAt: 10));
            File.WriteAllText(
                beforeManifest,
                """
                {"schemaVersion":1,"cases":[{"id":"before","benchmark":"Bench.Work","parameters":"Size: 1","benchmarkDisplay":"Work(Size: 1): Job-OLD","speedscope":"case.speedscope.json","operationCount":10,"operationUnit":"items"}]}
                """);
            File.WriteAllText(
                afterManifest,
                """
                {"schemaVersion":1,"cases":[{"id":"after","benchmark":"Bench.Work","parameters":"Size: 1","benchmarkDisplay":"Work(Size: 1): Job-NEW","speedscope":"case.speedscope.json","operationCount":20,"operationUnit":"items"}]}
                """);

            (int exit, string output, string error) = Run(Request(
                beforeManifest,
                afterManifest,
                format: OutputFormat.Json));

            exit.Should().Be(ExitCodes.Success);
            error.Should().BeEmpty();
            output.Should().Contain("\"benchmark\":\"Bench.Work\"");
            output.Should().Contain("\"parameters\":\"Size: 1\"");
            output.Should().Contain("\"operationUnit\":\"items\"");
            output.Should().Contain("\"percentagePointChange\"");
            output.Should().Contain("\"scopeWeightPerOperationDelta\":-0.5");

            (int textExit, string textOutput, _) = Run(Request(beforeManifest, afterManifest));
            textExit.Should().Be(ExitCodes.Success);
            textOutput.Should().Contain("per items:");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Run_ManifestBatch_ReturnsParameterKeyedCompactRows()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"filtrace-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "manifest.json");
        string trace = Speedscope.Replace("\\", "\\\\", StringComparison.Ordinal);
        try
        {
            File.WriteAllText(
                manifest,
                $$"""
                {"schemaVersion":1,"cases":[
                  {"id":"one","benchmark":"Bench.Work","parameters":"Size: 1","benchmarkDisplay":"Work(Size: 1): Job-A","speedscope":"{{trace}}","operationCount":10,"operationUnit":"items"},
                  {"id":"two","benchmark":"Bench.Work","parameters":"Size: 2","benchmarkDisplay":"Work(Size: 2): Job-A","speedscope":"{{trace}}"}
                ]}
                """);
            BatchRequest request = new(
                manifest,
                TraceMetric.Cpu,
                "",
                FrameNames.DefaultFoldPatterns,
                Measure.Self,
                OutputFormat.Json,
                Symbols: null,
                Strict: false,
                ScopeRequest.Auto);

            (int exit, string output, string error) = Run(request);

            exit.Should().Be(ExitCodes.Success);
            error.Should().BeEmpty();
            output.Should().Contain("\"parameters\":\"Size: 1\"");
            output.Should().Contain("\"parameters\":\"Size: 2\"");
            output.Should().Contain("\"topFrame\":\"MyApp.Inner\"");
            output.Should().Contain("\"operationUnit\":\"items\"");
            output.Should().Contain("\"scopeWeightPerOperation\":2.5");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
