// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class LifecycleExecutorTests
{
    private static string FixturePath(string name) =>
        Path.Join(AppContext.BaseDirectory, "Fixtures", name);

    // The ETW fixture captured a job process that launched a console host, with both
    // process edges recorded, so it is a fully observed parent-and-child invocation.
    private static string Etw => FixturePath("etw.etl");

    // A .nettrace carries no kernel process events; the ETL guardrail rejects it.
    private static string Alloc => FixturePath("alloc.nettrace");

    private static LifecycleRequest Request(
        string path,
        string? process = "HotLoop",
        IReadOnlyList<string>? images = null,
        int top = 25,
        OutputFormat format = OutputFormat.Text) =>
        new(
            path,
            string.IsNullOrEmpty(process) ? ScopeRequest.Auto : ScopeRequest.ForProcess(process),
            images ?? [],
            top,
            format);

    private static (int Exit, string Out, string Error) Run(LifecycleRequest request)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = LifecycleExecutor.Run(request, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [TestMethod]
    public void Run_TextFormat_WritesThePhaseTable()
    {
        (int exit, string output, _) = Run(Request(Etw));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("Lifecycle report");
        output.Should().Contain("wall-clock phases");
        output.Should().Contain("root lifetime");
        output.Should().Contain("root start to first child");
    }

    [TestMethod]
    public void Run_TextFormat_SeparatesSampledCpuFromWallClock()
    {
        (_, string output, _) = Run(Request(Etw));

        // The report exists to keep these apart, so the text has to say which is which.
        output.Should().Contain("sampled CPU, not wall clock");
    }

    [TestMethod]
    public void Run_JsonFormat_WritesSingleLineEnvelope()
    {
        (int exit, string output, _) = Run(Request(Etw, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        string json = output.Trim();
        json.Should().NotContain("\n");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"invocationCount\"");
        json.Should().Contain("\"measuredCount\"");
        json.Should().Contain("\"phases\"");
        json.Should().Contain("\"invocations\"");
    }

    [TestMethod]
    public void Run_JsonFormat_CarriesTheChildAndItsPhases()
    {
        (_, string output, _) = Run(Request(Etw, format: OutputFormat.Json));

        output.Should().Contain("\"children\"");
        output.Should().Contain("\"rootStartToChildStartMs\"");
        output.Should().Contain("\"childSpanMs\"");
        output.Should().Contain("\"childStopToRootStopMs\"");
    }

    [TestMethod]
    public void Run_WithImages_ReportsTheLoaderMilestone()
    {
        (int exit, string output, _) = Run(Request(Etw, images: ["ntdll"]));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("image load offsets");
        output.Should().Contain("ntdll");
    }

    [TestMethod]
    public void Run_UnmatchedProcess_SucceedsWithAWarning()
    {
        (int exit, string output, _) = Run(Request(Etw, process: "no-such-process-name"));

        // An empty result is a scope problem to report, not a failure to load the trace.
        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("no matching process");
        output.Should().Contain("! No process matching");
    }

    [TestMethod]
    public void Run_EmitsSteeringHints()
    {
        (_, string output, _) = Run(Request(Etw));

        output.Should().Contain("> ");
        output.Should().Contain("wall clock is not CPU");
    }

    [TestMethod]
    public void Run_NetTraceInput_ReturnsInputError()
    {
        (int exit, _, string error) = Run(Request(Alloc));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().Contain("process lifecycle");
        error.Should().Contain(".etl");
    }

    [TestMethod]
    public void Run_MissingFile_ReturnsInputError()
    {
        (int exit, _, string error) = Run(Request(FixturePath("does-not-exist.etl")));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Run_NegativeTop_ReturnsUsageError()
    {
        (int exit, _, string error) = Run(Request(Etw, top: -1));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("top must be 0 or greater.");
    }

    [TestMethod]
    public void Run_ZeroTop_KeepsTheMediansWithoutInvocationRows()
    {
        (int exit, string output, _) = Run(Request(Etw, top: 0, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("\"invocations\":[]").And.Contain("Aggregate only");
    }

    [TestMethod]
    public void Run_TopCapsInvocationRowsButKeepsTheMedians()
    {
        // The fixture holds one invocation, so a cap of 1 is not exceeded and no
        // truncation warning appears - the medians still describe every invocation.
        (int exit, string output, _) = Run(Request(Etw, top: 1, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        output.Should().NotContain("Showing the first 1");
        output.Should().Contain("\"phases\"");
    }
}
