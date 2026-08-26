// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using Filtrace.Output;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

[TestClass]
public sealed class TimelineExecutorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string Alloc => FixturePath("alloc.nettrace");

    private static string Speedscope => FixturePath("folding.speedscope.json");

    private static TimelineRequest Request(
        string path,
        TimelineMode mode = TimelineMode.Buckets,
        double? at = null,
        double? window = null,
        string lanes = "",
        string time = "",
        int? buckets = null,
        string process = "",
        bool allProcesses = false,
        int[]? pid = null,
        Children children = Children.Include,
        OutputFormat format = OutputFormat.Text) =>
        new(path, mode, at, window, time, lanes, buckets, process, allProcesses, pid, children, format);

    private static (int Exit, string Out, string Error) Run(TimelineRequest request)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = TimelineExecutor.Run(request, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [TestMethod]
    public void Run_TextFormat_WritesGeometryAndLanes()
    {
        (int exit, string output, _) = Run(Request(Alloc, buckets: 20));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("timeline");
        output.Should().Contain("buckets");
        output.Should().Contain("gc");
        output.Should().Contain("alloc");
    }

    [TestMethod]
    public void Run_JsonFormat_WritesSingleLineEnvelope()
    {
        (int exit, string output, _) = Run(Request(Alloc, buckets: 10, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        string json = output.Trim();
        json.Should().NotContain("\n");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"bucketCount\":10");
    }

    [TestMethod]
    public void Run_LanesSelector_LimitsLanesAndOmitsTheRest()
    {
        (int exit, string output, _) = Run(Request(Alloc, lanes: "gc", format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement result = document.RootElement.GetProperty("result");
        result.GetProperty("gc").GetArrayLength().Should().BeGreaterThan(0);
        result.TryGetProperty("cpu", out _).Should().BeFalse();
        result.TryGetProperty("exceptions", out _).Should().BeFalse();
        result.TryGetProperty("alloc", out _).Should().BeFalse();
        result.TryGetProperty("jit", out _).Should().BeFalse();
    }

    [TestMethod]
    public void Run_SnapshotJson_ReturnsExactWindowAndBoundedEvidence()
    {
        (int exit, string output, string error) = Run(Request(
            Alloc,
            mode: TimelineMode.Snapshot,
            at: 10.0,
            window: 2.0,
            format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        error.Should().BeEmpty();
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement result = document.RootElement.GetProperty("result");
        result.GetProperty("mode").GetString().Should().Be("snapshot");
        result.GetProperty("fromMs").GetDouble().Should().Be(8.0);
        result.GetProperty("toMs").GetDouble().Should().Be(12.0);
        JsonElement snapshot = result.GetProperty("snapshot");
        snapshot.GetProperty("events").GetProperty("eventCount").GetInt64().Should().BeGreaterThan(0);
        snapshot.GetProperty("events").GetProperty("types").GetArrayLength()
            .Should().BeLessThanOrEqualTo(TimelineProvider.SnapshotDetailLimit);
    }

    [TestMethod]
    public void Run_SnapshotWithoutAt_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(Alloc, mode: TimelineMode.Snapshot));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("--at is required");
    }

    [TestMethod]
    public void Run_BucketsWithExplicitDefaultWindow_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(
            Alloc,
            window: TimelineProvider.DefaultSnapshotHalfWindowMs));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("--window require --mode snapshot");
    }

    [TestMethod]
    public void Run_SnapshotWithExplicitDefaultBucketCount_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(
            Alloc,
            mode: TimelineMode.Snapshot,
            at: 10.0,
            buckets: TimelineProvider.DefaultBucketCount));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("--buckets apply only to --mode buckets");
    }

    [TestMethod]
    public void Run_SnapshotWithBucketSelector_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(
            Alloc,
            mode: TimelineMode.Snapshot,
            at: 10.0,
            lanes: "cpu"));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("apply only to --mode buckets");
    }

    [TestMethod]
    public void Run_SnapshotWindowBelowMinimum_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(
            Alloc,
            mode: TimelineMode.Snapshot,
            at: 10.0,
            window: 0.001));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain(TimelineProvider.MinSnapshotHalfWindowMs.ToString("N2"));
    }

    [TestMethod]
    public void Run_SnapshotCenterBeyondWirePrecision_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(
            Alloc,
            mode: TimelineMode.Snapshot,
            at: 10.005,
            window: 2.0));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("--at").And.Contain("0.01 millisecond increments");
    }

    [TestMethod]
    public void Run_SnapshotWindowBeyondWirePrecision_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(
            Alloc,
            mode: TimelineMode.Snapshot,
            at: 10.0,
            window: 0.015));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("--window").And.Contain("0.01 millisecond increments");
    }

    [TestMethod]
    public void Run_SnapshotText_PreservesMinimumWindowPrecision()
    {
        (int exit, string output, string error) = Run(Request(
            Alloc,
            mode: TimelineMode.Snapshot,
            at: 10.0,
            window: TimelineProvider.MinSnapshotHalfWindowMs));

        exit.Should().Be(ExitCodes.Success);
        error.Should().BeEmpty();
        output.Should().Contain("at 10.00 ms").And.Contain("window [9.99, 10.01] ms");
    }

    [TestMethod]
    public void Run_ProcessSelectorAboveLimit_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(
            Alloc,
            process: new string('x', ProcessNameSelector.MaxNameSubstringLength + 1)));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain($"--process may not exceed {ProcessNameSelector.MaxNameSubstringLength} characters");
    }

    [TestMethod]
    public void Run_ProcessSelectorWithControlCharacter_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(Alloc, process: "App\nInjected"));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("--process may not contain control characters");
    }

    [TestMethod]
    public void Render_SnapshotText_WritesEverySummaryHintAndWarning()
    {
        TimelineSnapshot snapshot = new(
            50.0,
            new SnapshotGcSummary(1, 0.25, 0.25, [new SnapshotGcRecord(1, 49.9, 2, "Blocking", "Induced", 0.5)]),
            new SnapshotCpuSummary(10, 1, [new SnapshotCpuMethod("App.Hot", 10, 100.0)]),
            new SnapshotExceptionSummary(2, 1, [new SnapshotCountRow("System.InvalidOperationException", 2)]),
            new SnapshotAllocationSummary(1, 1_048_576, 1, [new SnapshotAllocationType("System.Byte[]", 1, 1_048_576)]),
            new SnapshotJitSummary(1, 1, [new SnapshotCountRow("App.Start", 1)]),
            new SnapshotEventSummary(15, 1, [new SnapshotEventType("Runtime", "Sample", 15)]),
            false);
        TimelineResult result = new(
            40.0, 60.0, 20.0, 1, "App", null, null, null, null, null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };
        AnalysisResult<TimelineResult> envelope = new(
            result,
            warnings: ["snapshot warning"],
            hints: ["snapshot hint"]);
        StringWriter output = new();

        TimelineTextRenderer.Render(envelope, "trace.nettrace", output);

        string text = output.ToString();
        text.Should().Contain("timeline snapshot")
            .And.Contain("events")
            .And.Contain("Runtime/Sample")
            .And.Contain("cpu")
            .And.Contain("App.Hot")
            .And.Contain("gc")
            .And.Contain("Blocking/Induced")
            .And.Contain("exceptions")
            .And.Contain("System.InvalidOperationException")
            .And.Contain("alloc")
            .And.Contain("System.Byte[]")
            .And.Contain("jit")
            .And.Contain("App.Start")
            .And.Contain("> snapshot hint")
            .And.Contain("! snapshot warning");
    }

    [TestMethod]
    public void Run_UnknownLane_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(Alloc, lanes: "bogus"));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("Unknown lane 'bogus'");
    }

    [TestMethod]
    public void Run_BadTimeWindow_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(Alloc, time: "not-a-window"));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Run_BucketsBelowMinimum_ClampsAndWarns()
    {
        (int exit, string output, _) = Run(Request(Alloc, buckets: 1, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain($"\"bucketCount\":{TimelineProvider.MinBucketCount}");
        output.Should().Contain("below the minimum");
    }

    [TestMethod]
    public void Run_Speedscope_ReturnsInputError()
    {
        // A speedscope export carries only CPU stacks, not the event stream the timeline
        // reads; the dual-format guardrail rejects it before any parse.
        (int exit, _, string error) = Run(Request(Speedscope));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().Contain("timeline");
    }

    [TestMethod]
    public void Run_MissingFile_ReturnsInputError()
    {
        (int exit, _, string error) = Run(Request(FixturePath("does-not-exist.nettrace")));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void TryReadDualFormatReport_InvalidData_ReturnsCleanInputFailure()
    {
        StringWriter error = new();

        bool success = TraceExecution.TryReadDualFormatReport(
            Alloc,
            "timeline snapshot",
            static () => throw new InvalidDataException("malformed snapshot trace"),
            error,
            out TimelineResult? result);

        success.Should().BeFalse();
        result.Should().BeNull();
        error.ToString().Should().Contain("malformed snapshot trace");
    }
}
