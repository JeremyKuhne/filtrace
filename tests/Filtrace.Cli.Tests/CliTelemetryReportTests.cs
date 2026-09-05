// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Nodes;

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
    [DataRow("[\"info\",null]", false)]
    [DataRow("[null]", false)]
    [DataRow("[]", false)]
    [DataRow("null", false)]
    [DataRow("[\"info\",\"\"]", true)]
    [DataRow("[\"info\",\" \" ]", true)]
    public void HasValidCompleteLaunchSet_DeserializedArgumentTokens_RejectsOnlyMissingTokens(
        string argumentsJson,
        bool expectedValid)
    {
        CliTelemetryReport valid = CreateReport(
            [CreateLaunch(iteration: 1, elapsedMilliseconds: 1)],
            iterations: 1);

        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        JsonNode document = JsonSerializer.SerializeToNode(valid, options)!;
        document["launches"]![0]!["arguments"] = JsonNode.Parse(argumentsJson);

        CliTelemetryReport report = document.Deserialize<CliTelemetryReport>(options)!;
        CliTelemetryReport created = CreateReport(report.Launches, iterations: 1);

        report.HasValidCompleteLaunchSet(expectedIterations: 1).Should().Be(expectedValid);
        created.Complete.Should().Be(expectedValid);
        created.ChildWallP50Milliseconds.HasValue.Should().Be(expectedValid);
        created.ChildWallP95Milliseconds.HasValue.Should().Be(expectedValid);
    }

    [TestMethod]
    public void Create_DifferentComparisonDigests_IsIncomplete()
    {
        CliTelemetryReport report = CreateReport(
            [
                CreateLaunch(iteration: 1, elapsedMilliseconds: 1),
                CreateLaunch(iteration: 2, elapsedMilliseconds: 2) with
                {
                    ComparisonOutputSha256 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
                },
            ],
            iterations: 2);

        report.Complete.Should().BeFalse();
        report.ChildWallP50Milliseconds.Should().BeNull();
        report.ChildWallP95Milliseconds.Should().BeNull();
    }

    [TestMethod]
    public void Create_DifferentRawDigestsWithSameComparisonIdentity_RemainsComplete()
    {
        CliTelemetryReport report = CreateReport(
            [
                CreateLaunch(iteration: 1, elapsedMilliseconds: 1),
                CreateLaunch(iteration: 2, elapsedMilliseconds: 2) with
                {
                    OutputSha256 = new string('B', 64)
                },
            ],
            iterations: 2);

        report.Complete.Should().BeTrue();
        report.HasValidCompleteLaunchSet(expectedIterations: 2).Should().BeTrue();
        report.Launches.Select(static launch => launch.OutputSha256).Distinct().Should().HaveCount(2);
    }

    [TestMethod]
    [DataRow(stringArrayData: null)]
    [DataRow("")]
    [DataRow("bad-digest")]
    public void HasValidCompleteLaunchSet_MissingOrMalformedComparisonDigest_IsIncomplete(string? digest)
    {
        CliProcessTelemetry[] launches =
        [
            CreateLaunch(iteration: 1, elapsedMilliseconds: 1) with { ComparisonOutputSha256 = digest }
        ];

        CliTelemetryReport created = CreateReport(launches, iterations: 1);
        CliTelemetryReport readBack = created with
        {
            Complete = true,
            Failure = null,
            ChildWallP50Milliseconds = 1,
            ChildWallP95Milliseconds = 1
        };

        created.Complete.Should().BeFalse();
        readBack.HasValidCompleteLaunchSet(expectedIterations: 1).Should().BeFalse();
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
    [DataRow("info-warm")]
    [DataRow("info-cold")]
    [DataRow("symbols-1")]
    [DataRow("rank-self-warm")]
    [DataRow("batch-8")]
    [DataRow("diff-8")]
    [DataRow("batch-cold-8")]
    [DataRow("diff-cold-8")]
    public async Task RunAsync_AcrossInputDirectories_PreservesRawAndComparisonIdentity(string scenario)
    {
        string temporaryDirectory = Path.Join(Path.GetTempPath(), $"filtrace telemetry {Guid.NewGuid():N}");
        List<CliTelemetryReport> reports = [];
        try
        {
            foreach (string arm in new[] { "baseline", "candidate" })
            {
                string directory = Path.Join(temporaryDirectory, arm);
                Directory.CreateDirectory(directory);
                string trace = Path.Join(directory, "activity.nettrace");
                File.Copy(Path.Join(AppContext.BaseDirectory, "Fixtures", "activity.nettrace"), trace);
                string output = Path.Join(directory, "telemetry.json");

                await CliTelemetryCommand.RunAsync(
                [
                    "--cli-telemetry",
                    "--scenario", scenario,
                    "--trace", trace,
                    "--output", output,
                    "--iterations", "2",
                    "--filtrace", CliProcessRunner.FindFiltraceExecutable()
                ]);

                CliTelemetryReport report = JsonSerializer.Deserialize<CliTelemetryReport>(
                    File.ReadAllText(output),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

                report.HasValidCompleteLaunchSet(expectedIterations: 2).Should().BeTrue();
                report.Launches.Should().HaveCount(2);
                foreach (CliProcessTelemetry launch in report.Launches)
                {
                    launch.ComparisonOutputSha256.Should().MatchRegex("^[0-9A-F]{64}$");
                }

                reports.Add(report);
            }

            if (scenario.StartsWith("info-", StringComparison.Ordinal))
            {
                reports[0].Launches[0].OutputSha256.Should().NotBe(reports[1].Launches[0].OutputSha256);
            }

            reports.SelectMany(static report => report.Launches)
                .Select(static launch => launch.ComparisonOutputSha256)
                .Distinct(StringComparer.Ordinal)
                .Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void NormalizeComparisonOutput_Info_PreservesNonPathFields()
    {
        string path = Path.GetFullPath("sample.nettrace");
        JsonObject document = new()
        {
            ["result"] = new JsonObject
            {
                ["path"] = path,
                ["sampleCount"] = 7,
                ["etlxCacheState"] = "hit"
            },
            ["diagnostics"] = new JsonArray("original warning")
        };

        string expected = CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), ["info", path]);
        document["result"]!["sampleCount"] = 8;

        CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), ["info", path]).Should().NotBe(expected);
        document["result"]!["sampleCount"] = 7;
        document["result"]!["etlxCacheState"] = "recovered";

        CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), ["info", path]).Should().NotBe(expected);
        document["result"]!["etlxCacheState"] = "hit";
        document["diagnostics"] = new JsonArray("different warning");

        CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), ["info", path]).Should().NotBe(expected);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"result\":{\"path\":null}}")]
    [DataRow("{\"result\":{\"path\":123}}")]
    [DataRow("{\"result\":{\"path\":\"different.nettrace\"}}")]
    public void NormalizeComparisonOutput_InfoWithUnverifiedPath_Throws(string output)
    {
        Action normalize = () => CliProcessRunner.NormalizeComparisonOutput(output, ["info", "sample.nettrace"]);

        normalize.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void NormalizeComparisonOutput_Batch_PreservesCaseIdentityAndRejectsOutsidePaths()
    {
        string directory = Path.GetFullPath("manifest-root");
        string manifest = Path.Join(directory, "manifest.json");
        JsonObject captureCase = new()
        {
            ["caseId"] = "one",
            ["tracePath"] = Path.Join(directory, "case-one.nettrace"),
            ["scopeWeight"] = 7
        };

        JsonObject document = new()
        {
            ["result"] = new JsonObject
            {
                ["manifestPath"] = manifest,
                ["cases"] = new JsonArray(captureCase)
            }
        };

        string expected = CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), ["batch", manifest]);
        captureCase["tracePath"] = Path.Join(directory, "case-two.nettrace");

        CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), ["batch", manifest]).Should().NotBe(expected);
        captureCase["tracePath"] = Path.GetFullPath(Path.Join(directory, "..", "outside.nettrace"));

        Action normalize = () => CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), ["batch", manifest]);

        normalize.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void NormalizeComparisonOutput_SymbolsWithUnverifiedDirectory_Throws()
    {
        string path = Path.GetFullPath("sample.nettrace");
        JsonObject document = new()
        {
            ["result"] = new JsonObject
            {
                ["path"] = path,
                ["sourceResolution"] = new JsonObject
                {
                    ["searchedDirectories"] = new JsonArray(Path.GetFullPath("different-symbols"))
                }
            }
        };

        Action normalize = () => CliProcessRunner.NormalizeComparisonOutput(
            document.ToJsonString(),
            ["info", path, "--symbols", Path.GetFullPath("requested-symbols")]);

        normalize.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void NormalizeComparisonOutput_OptionalSymbolMetadata_PreservesItsPresenceAndContents()
    {
        string path = Path.GetFullPath("sample.nettrace");
        string symbols = Path.GetFullPath("symbols");
        string[] arguments = ["info", path, "--symbols", symbols];
        JsonObject result = new() { ["path"] = path };
        JsonObject document = new() { ["result"] = result };

        string absent = CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), arguments);

        JsonNode.Parse(absent)!["result"]!.AsObject().ContainsKey("sourceResolution").Should().BeFalse();
        result["sourceResolution"] = new JsonObject { ["searchedDirectories"] = new JsonArray() };

        string empty = CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), arguments);

        empty.Should().NotBe(absent);
        result["sourceResolution"]!["searchedDirectories"] = new JsonArray(symbols);

        string searched = CliProcessRunner.NormalizeComparisonOutput(document.ToJsonString(), arguments);

        searched.Should().NotBe(absent);
        searched.Should().NotBe(empty);
    }

    [TestMethod]
    public void NormalizeComparisonOutput_MalformedInfoJson_Throws()
    {
        Action normalize = () => CliProcessRunner.NormalizeComparisonOutput("{", ["info", "sample.nettrace"]);

        normalize.Should().Throw<JsonException>();
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
                OutputSha256: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                ComparisonOutputSha256: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
}