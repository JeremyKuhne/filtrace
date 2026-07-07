// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

[TestClass]
public sealed class EventsExecutorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string Alloc => FixturePath("alloc.nettrace");

    private static string Speedscope => FixturePath("folding.speedscope.json");

    private static string DiskIoTrace => FixturePath("diskio.etl");

    private static EventsRequest Request(
        string path,
        string name = "",
        int skip = 0,
        int take = 50,
        int maxPayload = 200,
        string payload = "",
        int? pid = null,
        int? tid = null,
        OutputFormat format = OutputFormat.Text) =>
        new(path, name, skip, take, maxPayload, payload, pid, tid, format);

    private static (int Exit, string Out, string Error) Run(EventsRequest request)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = EventsExecutor.Run(request, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [TestMethod]
    public void Run_TextFormat_WritesMatchedEvents()
    {
        (int exit, string output, _) = Run(Request(Alloc, take: 5));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("events");
        output.Should().Contain("matched");
    }

    [TestMethod]
    public void Run_JsonFormat_WritesSingleLineEnvelope()
    {
        (int exit, string output, _) = Run(Request(Alloc, take: 5, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        string json = output.Trim();
        json.Should().NotContain("\n");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"totalMatched\"");
    }

    [TestMethod]
    public void Run_TakeCapsThePage()
    {
        (int exit, string output, _) = Run(Request(Alloc, take: 3, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        // The page carries at most take events, each rendered with an eventName field.
        Regex.Matches(output, "\"eventName\":").Count.Should().BeLessThanOrEqualTo(3);
    }

    [TestMethod]
    public void Run_MorePagesRemain_EmitsAPagingHint()
    {
        // The fixture carries many events, so a tiny page leaves more matching and the
        // executor steers toward the next page.
        (int exit, string output, _) = Run(Request(Alloc, take: 1));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("more match");
        output.Should().Contain("--skip 1");
    }

    [TestMethod]
    public void Run_NameFilter_MatchesOnlyTheNamedEvents()
    {
        (int exit, string output, _) = Run(Request(Alloc, name: "AllocationTick", take: 1000, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("AllocationTick");
    }

    [TestMethod]
    public void Run_PayloadFilter_NarrowsToMatchingEvents()
    {
        (int exit, string output, _) =
            Run(Request(Alloc, name: "AllocationTick", take: 1000, payload: "Small", format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("Small");

        // A payload value that appears in nothing narrows to zero matches.
        (int exit2, string output2, _) =
            Run(Request(Alloc, name: "AllocationTick", payload: "__no_such_value__", format: OutputFormat.Json));
        exit2.Should().Be(ExitCodes.Success);
        output2.Should().Contain("\"totalMatched\":0");
    }

    [TestMethod]
    public void Run_ProcessFilter_UnknownPid_MatchesNothing()
    {
        (int exit, string output, _) =
            Run(Request(Alloc, name: "AllocationTick", pid: 999999, format: OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("\"totalMatched\":0");
    }

    [TestMethod]
    public void Run_TextView_IncludesProcessColumn()
    {
        (int exit, string output, _) = Run(Request(Alloc, name: "AllocationTick", take: 1));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("proc");
    }

    [TestMethod]
    public void Run_MissingFile_ReturnsInputError()
    {
        (int exit, _, string error) = Run(Request(FixturePath("does-not-exist.nettrace")));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Run_SpeedscopeInput_ReturnsInputError()
    {
        // The events query spans .nettrace and .etl, but a speedscope export carries only
        // CPU stacks - no event stream - so it is rejected up front.
        (int exit, _, string error) = Run(Request(Speedscope));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().Contain("requires a .nettrace EventPipe or .etl");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Run_EtlTrace_ReturnsSuccess()
    {
        // The events query accepts an .etl too (reading it is Windows-only, hence the OS
        // guard); the kernel event stream yields a matched page.
        (int exit, string output, _) = Run(Request(DiskIoTrace, take: 5));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("matched");
    }
}
