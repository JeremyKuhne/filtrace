// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class DiskIoExecutorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // The disk I/O fixture is a trimmed ETW capture of a write-through workload, so it
    // carries the physical DiskIO write events the report reads.
    private static string DiskIo => FixturePath("diskio.etl");

    // A .nettrace carries no kernel disk events; the ETL guardrail rejects it.
    private static string Alloc => FixturePath("alloc.nettrace");

    private static DiskIoRequest Request(string path, int top = 25, OutputFormat format = OutputFormat.Text) =>
        new(path, top, format);

    private static (int Exit, string Out, string Error) Run(DiskIoRequest request)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = DiskIoExecutor.Run(request, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [TestMethod]
    public void Run_TextFormat_WritesTheSummary()
    {
        (int exit, string output, _) = Run(Request(DiskIo));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("DiskIO report");
        output.Should().Contain("writes");
        output.Should().Contain("disk time");
    }

    [TestMethod]
    public void Run_JsonFormat_WritesSingleLineEnvelope()
    {
        (int exit, string output, _) = Run(Request(DiskIo, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        string json = output.Trim();
        json.Should().NotContain("\n");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"writeCount\"");
        json.Should().Contain("\"files\"");
    }

    [TestMethod]
    public void Run_TopLimitsFileRowsButKeepsTheFullCounts()
    {
        (int exit, string output, _) = Run(Request(DiskIo, top: 1, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        int shown = Regex.Matches(output, "\"fileName\":").Count;

        // The per-file detail is capped to top, but the aggregate write count reflects
        // every operation.
        shown.Should().BeLessThanOrEqualTo(1);
        int writeCount = int.Parse(Regex.Match(output, "\"writeCount\":(\\d+)").Groups[1].Value);
        writeCount.Should().BeGreaterThan(0);
        output.Should().Contain("Showing the top 1");
    }

    [TestMethod]
    public void Run_MissingFile_ReturnsInputError()
    {
        (int exit, _, string error) = Run(Request(FixturePath("does-not-exist.etl")));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Run_WrongFormat_ReturnsInputError()
    {
        // A .nettrace carries no kernel disk events; the format guardrail rejects it
        // before any parse.
        (int exit, _, string error) = Run(Request(Alloc));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().Contain("disk I/O report requires");
    }

    [TestMethod]
    public void Run_NonPositiveTop_ReturnsUsageError()
    {
        // The verb enforces top >= 1, but the executor guards the boundary too so a
        // direct call with a bad top fails cleanly rather than emitting a "top 0" report.
        (int exit, _, string error) = Run(Request(DiskIo, top: 0));

        exit.Should().Be(ExitCodes.UsageError);
        error.Should().Contain("top must be 1 or greater");
    }
}
