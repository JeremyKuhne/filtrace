// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

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
        string lanes = "",
        string time = "",
        int buckets = TimelineProvider.DefaultBucketCount,
        string process = "",
        bool allProcesses = false,
        OutputFormat format = OutputFormat.Text) =>
        new(path, time, lanes, buckets, process, allProcesses, format);

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
    public void Run_LanesSelector_LimitsLanesAndNullsTheRest()
    {
        (int exit, string output, _) = Run(Request(Alloc, lanes: "gc", format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("\"gc\":[");
        output.Should().Contain("\"cpu\":null");
        output.Should().Contain("\"alloc\":null");
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
}
