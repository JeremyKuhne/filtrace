// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

[TestClass]
public sealed class ThreadPoolExecutorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // The threadpool trace is captured under thread-pool starvation, so it carries the
    // worker-thread adjustment events the report reads.
    private static string ThreadPool => FixturePath("threadpool.nettrace");

    private static string Speedscope => FixturePath("folding.speedscope.json");

    private static ThreadPoolRequest Request(string path, OutputFormat format = OutputFormat.Text) =>
        new(path, format);

    private static (int Exit, string Out, string Error) Run(ThreadPoolRequest request)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = ThreadPoolExecutor.Run(request, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [TestMethod]
    public void Run_TextFormat_WritesTheSummary()
    {
        (int exit, string output, _) = Run(Request(ThreadPool));

        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("ThreadPool report");
        output.Should().Contain("worker-thread adjustments");
        output.Should().Contain("starvation");
        output.Should().Contain("by reason");
    }

    [TestMethod]
    public void Run_JsonFormat_WritesSingleLineEnvelope()
    {
        (int exit, string output, _) = Run(Request(ThreadPool, OutputFormat.Json));

        exit.Should().Be(ExitCodes.Success);
        string json = output.Trim();
        json.Should().NotContain("\n");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"adjustmentCount\"");
        json.Should().Contain("\"starvationCount\"");
    }

    [TestMethod]
    public void Run_MissingFile_ReturnsInputError()
    {
        (int exit, _, string error) = Run(Request(FixturePath("does-not-exist.nettrace")));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Run_WrongFormat_ReturnsInputError()
    {
        // A speedscope export carries no thread-pool events; the format guardrail rejects
        // it before any EventPipe parse.
        (int exit, _, string error) = Run(Request(Speedscope));

        exit.Should().Be(ExitCodes.InputError);
        error.Should().Contain("thread-pool report requires");
    }
}
