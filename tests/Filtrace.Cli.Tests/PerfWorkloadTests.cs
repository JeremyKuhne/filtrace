// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Benchmarks;

namespace Filtrace.PerfWorkload;

[TestClass]
[DoNotParallelize]
public sealed class PerfWorkloadTests
{
    private static (int Exit, string Out, string Error) Run(params string[] args)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exit = Program.Main(args);
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    public void Main_Help_WritesUsageToStandardOutput(string argument)
    {
        (int exit, string output, string error) = Run(argument);

        exit.Should().Be(0);
        output.Should().StartWith("Usage:");
        error.Should().BeEmpty();
    }

    [TestMethod]
    public void Main_NoArguments_WritesUsageToStandardError()
    {
        (int exit, string output, string error) = Run();

        exit.Should().Be(2);
        output.Should().BeEmpty();
        error.Should().StartWith("Usage:");
    }

    [TestMethod]
    public async Task RunTelemetryAsync_BlockedChild_RecordsElapsedTimeDistinctFromCpuTime()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string workload = typeof(Program).Assembly.Location;

        CliProcessTelemetry telemetry = await CliProcessRunner.RunTelemetryAsync(
            dotnet,
            [workload, "wait", "--workers", "1", "--duration-ms", "500"],
            iteration: 1);

        telemetry.ElapsedMilliseconds.HasValue.Should().BeTrue();
        double elapsedMilliseconds = telemetry.ElapsedMilliseconds.GetValueOrDefault();
        elapsedMilliseconds.Should().BeGreaterThanOrEqualTo(350);
        elapsedMilliseconds.Should().BeLessThan(10_000);
        double.IsFinite(elapsedMilliseconds).Should().BeTrue();
        telemetry.TotalProcessorMilliseconds.Should().BeLessThan(elapsedMilliseconds - 200);
    }

    [TestMethod]
    public async Task RunTelemetryAsync_NonzeroChild_RetainsRejectedObservation()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string workload = typeof(Program).Assembly.Location;

        Func<Task> action = async () => await CliProcessRunner.RunTelemetryAsync(
            dotnet,
            [workload, "not-a-mode"],
            iteration: 3);

        CliProcessTelemetryException exception = (await action.Should()
            .ThrowExactlyAsync<CliProcessTelemetryException>()).Which;

        exception.Message.Should().Contain("exited with code 2");
        exception.Telemetry.Iteration.Should().Be(3);
        exception.Telemetry.Arguments.Should().Equal(workload, "not-a-mode");
        exception.Telemetry.ElapsedMilliseconds.Should().BeGreaterThan(0);
        exception.Telemetry.ExitCode.Should().Be(2);
        exception.Telemetry.StandardOutputLength.Should().Be(0);
        exception.Telemetry.StandardErrorLength.Should().BeGreaterThan(0);
        exception.Telemetry.OutputSha256.Should().MatchRegex("^[0-9A-F]{64}$");
    }
}