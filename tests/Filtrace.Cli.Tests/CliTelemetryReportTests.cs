// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.Benchmarks;

[TestClass]
public sealed class CliTelemetryReportTests
{
    [TestMethod]
    public void Create_25ShuffledLaunches_UsesNearestRankAndPreservesRawOrder()
    {
        double[] durations = [.. Enumerable.Range(1, 24).Select(static value => (double)value).Prepend(1_000)];
        CliProcessTelemetry[] launches = [.. durations.Select(
            (duration, index) => CreateLaunch(index + 1, duration))];

        CliTelemetryReport report = CreateReport(launches, iterations: 25);

        report.Complete.Should().BeTrue();
        report.ChildWallP50Milliseconds.Should().Be(13);
        report.ChildWallP95Milliseconds.Should().Be(24);
        report.ChildWallP50Milliseconds.Should().NotBe(durations.Average());
        report.Launches.Select(static launch => launch.ElapsedMilliseconds).Should().Equal(durations.Cast<double?>());
        report.HasValidCompleteLaunchSet(expectedIterations: 25).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Create_InvalidElapsed_DoesNotEmitPercentiles(double elapsedMilliseconds)
    {
        CliTelemetryReport report = CreateReport(
            [CreateLaunch(iteration: 1, elapsedMilliseconds)],
            iterations: 1);

        report.Complete.Should().BeFalse();
        report.ChildWallP50Milliseconds.Should().BeNull();
        report.ChildWallP95Milliseconds.Should().BeNull();
        report.HasValidCompleteLaunchSet(expectedIterations: 1).Should().BeFalse();
    }

    [TestMethod]
    public void Create_MissingNonzeroAndPartialLaunches_DoNotEmitPercentiles()
    {
        CliTelemetryReport missing = CreateReport([], iterations: 1);
        CliTelemetryReport nonzero = CreateReport(
            [CreateLaunch(iteration: 1, elapsedMilliseconds: 1) with
            {
                ExitCode = 1
            }],
            iterations: 1);

        CliTelemetryReport partial = CreateReport(
            [CreateLaunch(iteration: 1, elapsedMilliseconds: 1) with
            {
                StandardOutputLength = 0
            }],
            iterations: 1);

        CliTelemetryReport malformed = CreateReport(
            [CreateLaunch(iteration: 1, elapsedMilliseconds: 1) with
            {
                OutputSha256 = "not-a-digest"
            }],
            iterations: 1);

        foreach (CliTelemetryReport report in new[]
        {
            missing,
            nonzero,
            partial,
            malformed
        })
        {
            report.Complete.Should().BeFalse();
            report.ChildWallP50Milliseconds.Should().BeNull();
            report.ChildWallP95Milliseconds.Should().BeNull();
        }
    }

    [TestMethod]
    public void Create_ExplicitFailure_PreservesLaunchesWithoutPercentiles()
    {
        CliProcessTelemetry[] launches = [CreateLaunch(iteration: 1, elapsedMilliseconds: 1)];

        CliTelemetryReport report = CliTelemetryReport.Create(
            "2026-09-04T00:00:00.0000000+00:00",
            "test",
            iterations: 2,
            "subject",
            launches,
            failure: "second launch failed");

        report.Complete.Should().BeFalse();
        report.Failure.Should().Be("second launch failed");
        report.Launches.Should().ContainSingle();
        report.ChildWallP50Milliseconds.Should().BeNull();
        report.ChildWallP95Milliseconds.Should().BeNull();
    }

    [TestMethod]
    public void Create_NullLaunchCollection_IsExplicitlyIncomplete()
    {
        CliTelemetryReport report = CreateReport(launches: null!, iterations: 1);

        report.Complete.Should().BeFalse();
        report.Failure.Should().NotBeNullOrEmpty();
        report.ChildWallP50Milliseconds.Should().BeNull();
        report.ChildWallP95Milliseconds.Should().BeNull();
        report.HasValidCompleteLaunchSet(expectedIterations: 1).Should().BeFalse();
    }

    [TestMethod]
    public void Create_NullArgumentsAndInvalidIterationIdentity_AreIncomplete()
    {
        CliTelemetryReport nullArguments = CreateReport(
            [CreateLaunch(iteration: 1, elapsedMilliseconds: 1) with
            {
                Arguments = null!
            }],
            iterations: 1);

        CliTelemetryReport reordered = CreateReport(
            [
                CreateLaunch(iteration: 2, elapsedMilliseconds: 1),
                CreateLaunch(iteration: 1, elapsedMilliseconds: 2)
            ],
            iterations: 2);

        CliTelemetryReport duplicate = CreateReport(
            [
                CreateLaunch(iteration: 1, elapsedMilliseconds: 1),
                CreateLaunch(iteration: 1, elapsedMilliseconds: 2)
            ],
            iterations: 2);

        nullArguments.Complete.Should().BeFalse();
        reordered.Complete.Should().BeFalse();
        duplicate.Complete.Should().BeFalse();
    }

    [TestMethod]
    public void Create_DifferentOutputDigests_IsIncomplete()
    {
        CliTelemetryReport report = CreateReport(
            [
                CreateLaunch(iteration: 1, elapsedMilliseconds: 1),
                CreateLaunch(iteration: 2, elapsedMilliseconds: 2) with
                {
                    OutputSha256 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
                },
            ],
            iterations: 2);

        report.Complete.Should().BeFalse();
        report.ChildWallP50Milliseconds.Should().BeNull();
        report.ChildWallP95Milliseconds.Should().BeNull();
    }

    [TestMethod]
    public void HasValidCompleteLaunchSet_CorruptSummaryState_ReturnsFalse()
    {
        CliTelemetryReport valid = CreateReport(
            [CreateLaunch(iteration: 1, elapsedMilliseconds: 10)],
            iterations: 1);

        CliTelemetryReport[] invalid =
        [
            valid with { SchemaVersion = 1 },
            valid with { Complete = false },
            valid with { Iterations = 2 },
            valid with { Failure = "unexpected failure" },
            valid with { ChildWallP50Milliseconds = null },
            valid with { ChildWallP95Milliseconds = null },
            valid with { ChildWallP50Milliseconds = double.NaN },
            valid with { ChildWallP95Milliseconds = double.PositiveInfinity },
            valid with { ChildWallP50Milliseconds = 9 },
            valid with { ChildWallP95Milliseconds = 11 }
        ];

        foreach (CliTelemetryReport report in invalid)
        {
            report.HasValidCompleteLaunchSet(expectedIterations: 1).Should().BeFalse();
        }
    }

    [TestMethod]
    public void Deserialize_NullReport_RemainsUnavailable()
    {
        CliTelemetryReport? report = JsonSerializer.Deserialize<CliTelemetryReport>("null");

        report.Should().BeNull();
    }

    [TestMethod]
    public void Deserialize_OldArtifactWithoutElapsed_IsExplicitlyIncomplete()
    {
        string json = """
            {
              "schemaVersion": 1,
              "createdUtc": "2026-09-04T00:00:00.0000000+00:00",
              "scenario": "test",
              "iterations": 1,
              "executable": "subject",
              "launches": [
                {
                  "iteration": 1,
                  "arguments": ["info"],
                  "totalProcessorMilliseconds": 1,
                  "peakWorkingSetBytes": 1,
                  "maxPrivateMemoryBytes": 1,
                  "exitCode": 0,
                  "standardOutputLength": 1,
                  "standardErrorLength": 0,
                  "outputSha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                }
              ]
            }
            """;

        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        CliTelemetryReport? report = JsonSerializer.Deserialize<CliTelemetryReport>(json, options);

        report.Should().NotBeNull();
        report!.Launches[0].ElapsedMilliseconds.Should().BeNull();
        report.HasValidCompleteLaunchSet(expectedIterations: 1).Should().BeFalse();
    }

    [TestMethod]
    public void HasValidCompleteLaunchSet_DeserializedNullLaunch_ReturnsFalse()
    {
        string json = """
            {
              "schemaVersion": 2,
              "createdUtc": "2026-09-04T00:00:00.0000000+00:00",
              "scenario": "test",
              "iterations": 1,
              "executable": "subject",
              "complete": true,
              "childWallP50Milliseconds": 1,
              "childWallP95Milliseconds": 1,
              "failure": null,
              "launches": [null]
            }
            """;

        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        CliTelemetryReport? report = JsonSerializer.Deserialize<CliTelemetryReport>(json, options);

        report.Should().NotBeNull();
        report!.HasValidCompleteLaunchSet(expectedIterations: 1).Should().BeFalse();
    }

    [TestMethod]
    public async Task RunAsync_NonzeroChild_WritesIncompleteReportWithRejectedRow()
    {
        string temporaryDirectory = Path.Join(
            Path.GetTempPath(),
            $"filtrace-telemetry-failure-{Guid.NewGuid():N}");

        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string trace = Path.Join(temporaryDirectory, "alloc.nettrace");
            File.Copy(Path.Join(AppContext.BaseDirectory, "Fixtures", "alloc.nettrace"), trace);
            string output = Path.Join(temporaryDirectory, "telemetry.json");
            string workloadDirectory = Path.GetDirectoryName(typeof(PerfWorkload.Program).Assembly.Location)!;
            string workload = Path.Join(
                workloadDirectory,
                OperatingSystem.IsWindows() ? "Filtrace.PerfWorkload.exe" : "Filtrace.PerfWorkload");

            Func<Task> action = async () => await CliTelemetryCommand.RunAsync(
            [
                "--cli-telemetry",
                "--scenario", "info-warm",
                "--trace", trace,
                "--output", output,
                "--iterations", "2",
                "--filtrace", workload
            ]);

            await action.Should().ThrowExactlyAsync<CliProcessTelemetryException>();
            CliTelemetryReport? report = JsonSerializer.Deserialize<CliTelemetryReport>(
                File.ReadAllText(output),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            report.Should().NotBeNull();
            report!.Complete.Should().BeFalse();
            report.Failure.Should().Contain("exited with code 2");
            report.Launches.Should().ContainSingle();
            report.Launches[0].Iteration.Should().Be(1);
            report.Launches[0].Arguments[0].Should().Be("info");
            report.Launches[0].ElapsedMilliseconds.Should().BeGreaterThan(0);
            report.Launches[0].ExitCode.Should().Be(2);
            report.Launches[0].StandardOutputLength.Should().Be(0);
            report.Launches[0].StandardErrorLength.Should().BeGreaterThan(0);
            report.ChildWallP50Milliseconds.Should().BeNull();
            report.ChildWallP95Milliseconds.Should().BeNull();
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static CliTelemetryReport CreateReport(
        IReadOnlyList<CliProcessTelemetry> launches,
        int iterations) =>
            CliTelemetryReport.Create(
                "2026-09-04T00:00:00.0000000+00:00",
                "test",
                iterations,
                "subject",
                launches,
                failure: null);

    private static CliProcessTelemetry CreateLaunch(
        int iteration,
        double? elapsedMilliseconds) =>
            new(
                iteration,
                ["info"],
                elapsedMilliseconds,
                TotalProcessorMilliseconds: 1,
                PeakWorkingSetBytes: 1,
                MaxPrivateMemoryBytes: 1,
                ExitCode: 0,
                StandardOutputLength: 1,
                StandardErrorLength: 0,
                OutputSha256: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
}