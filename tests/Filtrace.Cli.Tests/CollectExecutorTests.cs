// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using Filtrace.Output;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

[TestClass]
public sealed class CollectExecutorTests
{
    // A real capture needs Windows + Administrator. Hosted Windows runners have both, so
    // the capture path does run in CI; it stays guarded so the same test is inert on a
    // non-Windows or unelevated developer box.

    [TestMethod]
    public void IsSupported_MatchesWindows()
    {
        EtwCollector.IsSupported.Should().Be(OperatingSystem.IsWindows());
    }

    [TestMethod]
    public void Collect_MissingLaunch_ThrowsArgumentException()
    {
        // Argument validation runs before the OS / elevation guard, so this is
        // deterministic on every OS.
        Action act = () => EtwCollector.Collect(new EtwCollectRequest
        {
            LaunchExecutable = "",
            OutputPath = "out.etl",
        });

        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Collect_DefaultRequest_UsesTheCpuProfile()
    {
        EtwCollectRequest request = new()
        {
            LaunchExecutable = "app.exe",
            OutputPath = "out.etl",
        };

        request.Profile.Should().Be(CollectProfile.Cpu);
    }

    [TestMethod]
    public void Run_WhenNotElevated_ReportsCleanError()
    {
        // When a real capture could run there is no clean-error to observe; the elevated
        // path is covered by Run_WhenElevated_ProducesEtl instead.
        if (EtwCollector.IsSupported && EtwCollector.IsElevated)
        {
            Assert.Inconclusive("Elevated: the not-elevated guard cannot be exercised here.");
        }

        string outputPath = Path.Join(Path.GetTempPath(), $"filtrace-collect-{Guid.NewGuid():N}.etl");
        EtwCollectRequest request = new()
        {
            LaunchExecutable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/true",
            OutputPath = outputPath,
        };

        StringWriter output = new();
        StringWriter error = new();
        int exit = CollectExecutor.Run(request, output, error);

        exit.Should().Be(ExitCodes.InputError);
        error.ToString().Should().NotBeEmpty();
        output.ToString().Should().BeEmpty();
        File.Exists(outputPath).Should().BeFalse();
    }

    [TestMethod]
    public void Run_WhenElevated_ProducesEtl()
    {
        // The capture step only works on Windows with Administrator; skip cleanly otherwise
        // so the same test is meaningful on an unelevated dev box.
        if (!EtwCollector.IsSupported || !EtwCollector.IsElevated)
        {
            Assert.Inconclusive("ETW capture needs Windows + Administrator; not available here.");
        }

        string outputPath = Path.Join(Path.GetTempPath(), $"filtrace-collect-{Guid.NewGuid():N}.etl");
        EtwCollectRequest request = new()
        {
            // A process that starts and exits on its own, so the capture spans a real (if
            // tiny) process lifetime. The duration cap only guards against a wedged launch.
            LaunchExecutable = "cmd.exe",
            LaunchArguments = "/c exit 0",
            OutputPath = outputPath,
            DurationSeconds = 60,
        };

        StringWriter output = new();
        StringWriter error = new();
        try
        {
            int exit = CollectExecutor.Run(request, output, error);

            exit.Should().Be(ExitCodes.Success);
            error.ToString().Should().BeEmpty();
            output.ToString().Should().Contain("Captured");
            File.Exists(outputPath).Should().BeTrue();
            new FileInfo(outputPath).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Run_WhenElevated_StartupProfileStillNamesManagedMethods()
    {
        if (!EtwCollector.IsSupported || !EtwCollector.IsElevated)
        {
            Assert.Inconclusive("ETW capture needs Windows + Administrator; not available here.");
        }

        // The startup profile narrows the CLR provider to the naming keywords, and that
        // trade is only worth making if the names actually survive it. Capture a real
        // managed process and prove the method events are in the trace - a sampling-based
        // assertion would be flaky on a run this short.
        string managedApp = Path.Join(AppContext.BaseDirectory, "filtrace.dll");
        File.Exists(managedApp).Should().BeTrue("the CLI assembly is the managed capture target");

        string outputPath = Path.Join(Path.GetTempPath(), $"filtrace-startup-{Guid.NewGuid():N}.etl");
        EtwCollectRequest request = new()
        {
            LaunchExecutable = "dotnet",
            LaunchArguments = $"\"{managedApp}\" --version",
            Profile = CollectProfile.Startup,
            OutputPath = outputPath,
            DurationSeconds = 120,
        };

        StringWriter output = new();
        StringWriter error = new();
        try
        {
            int exit = CollectExecutor.Run(request, output, error);
            exit.Should().Be(ExitCodes.Success);

            // Method/LoadVerbose is the event that carries the namespace, name, and
            // signature; if the narrowed keyword set or a lowered level had dropped it,
            // every managed frame in a startup capture would be an unnamed address.
            EventQueryResult methods = new EventQueryProvider().Query(
                outputPath, nameFilter: "Method/LoadVerbose", take: 1);

            methods.TotalMatched.Should().BeGreaterThan(
                0, "the startup profile keeps the CLR keywords that name managed methods");

            // The keywords it drops must genuinely be absent, or the profile is not the
            // low-perturbation capture it claims to be.
            new EventQueryProvider().Query(outputPath, nameFilter: "GC/Start", take: 1)
                .TotalMatched.Should().Be(0, "startup drops the GC keyword");

            new EventQueryProvider().Query(outputPath, nameFilter: "FileIO/Name", take: 1)
                .TotalMatched.Should().Be(0, "no profile enables the file-name rundown");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Collect_DefaultRequest_UsesOneIteration()
    {
        EtwCollectRequest request = new()
        {
            LaunchExecutable = "app.exe",
            OutputPath = "out.etl",
        };

        request.Iterations.Should().Be(1);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(1001)]
    public void Collect_IterationsOutsideTheSupportedRange_ThrowsArgumentOutOfRange(int iterations)
    {
        // The ceiling matters as much as the floor: the capture manifest rejects more
        // invocations than this, so a capture the core accepted but no manifest could
        // describe would only fail later, somewhere less obvious.
        Action act = () => EtwCollector.Collect(new EtwCollectRequest
        {
            LaunchExecutable = "app.exe",
            OutputPath = "out.etl",
            Iterations = iterations,
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void SerializedInvocation_CarriesOnlyTheRecordedFacts()
    {
        // Duration is derived from the timestamps, so putting it on the wire would repeat
        // the same information a thousand times over at the maximum iteration count.
        EtwCollectResult result = new()
        {
            OutputPath = "out.etl",
            ProcessId = 42,
            ProcessName = "app",
            ProcessExitCode = 0,
            Invocations =
            [
                new EtwInvocation(1, 42, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMilliseconds(50))
            ],
            FileSizeBytes = 1,
            Profile = CollectProfile.Cpu,
            KernelKeywords = "Process",
            ClrKeywords = "none",
            CpuSample = new CpuSampleInterval(1.0, 1.0, 0.1221, 100.0),
        };

        string json = OutputJson.Serialize(new AnalysisResult<EtwCollectResult>(result, [], []));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement invocation = document.RootElement
            .GetProperty("result").GetProperty("invocations")[0];

        invocation.EnumerateObject().Select(static property => property.Name)
            .Should().BeEquivalentTo("ordinal", "processId", "exitCode", "startedUtc", "stoppedUtc");
    }

    [TestMethod]
    public void Run_WhenElevated_SubMillisecondInterval_SamplesMoreDensely()
    {
        if (!EtwCollector.IsSupported || !EtwCollector.IsElevated)
        {
            Assert.Inconclusive("ETW capture needs Windows + Administrator; not available here.");
        }

        string workload = Path.Join(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "NativeLoop", "NativeLoop.exe");

        if (!File.Exists(workload))
        {
            Assert.Inconclusive("The NativeLoop workload has not been built.");
        }

        // Fixed work per run, so wall time is about constant and the sample count is the
        // only thing the interval changes. This is the measurement that decided SC6: the
        // OS echoes any requested interval back, so density is the only proof it was honored.
        int baseline = CountSamples(workload, 1.0);
        int dense = CountSamples(workload, 0.25);

        // A quarter of the interval should be about four times the samples; the assertion
        // is loose because a live machine is noisy, but it fails outright if the request
        // was ignored.
        dense.Should().BeGreaterThan(baseline * 2,
            "quartering the interval must sample materially more densely, not identically");
    }

    [TestMethod]
    public void Run_WhenElevated_BelowTheHonoredFloor_ReportsTheClamp()
    {
        if (!EtwCollector.IsSupported || !EtwCollector.IsElevated)
        {
            Assert.Inconclusive("ETW capture needs Windows + Administrator; not available here.");
        }

        if (!CpuSampleBounds.TryReadTimerBounds(out double minimumMSec, out _))
        {
            Assert.Inconclusive("This platform does not report profile source bounds.");
        }

        string outputPath = Path.Join(Path.GetTempPath(), $"filtrace-clamp-{Guid.NewGuid():N}.etl");
        try
        {
            EtwCollectResult result = EtwCollector.Collect(new EtwCollectRequest
            {
                LaunchExecutable = "cmd.exe",
                LaunchArguments = "/c exit 0",
                OutputPath = outputPath,
                CpuSampleMSec = minimumMSec / 4.0,
                DurationSeconds = 60,
            });

            // Windows accepts the request and reports no error, so the capture has to say
            // so itself or a caller silently gets a rate four times slower than asked for.
            result.CpuSample.Clamped.Should().BeTrue();
            result.CpuSample.EffectiveMSec.Should().Be(minimumMSec);
            result.CpuSample.RequestedMSec.Should().Be(minimumMSec / 4.0);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    // Captures the workload at one interval and returns how many CPU samples it produced.
    private static int CountSamples(string workload, double cpuSampleMSec)
    {
        string outputPath = Path.Join(Path.GetTempPath(), $"filtrace-density-{Guid.NewGuid():N}.etl");
        try
        {
            EtwCollector.Collect(new EtwCollectRequest
            {
                LaunchExecutable = workload,
                LaunchArguments = "--iterations 200000",
                OutputPath = outputPath,
                Profile = CollectProfile.Startup,
                CpuSampleMSec = cpuSampleMSec,
                DurationSeconds = 120,
            });

            LoadedTrace trace = new Filtrace.Server.TraceStore().Get(
                outputPath, symbolsDirectory: null, TraceMetric.Cpu, ScopeRequest.ForProcess("NativeLoop"));

            return trace.Info.SampleCount;
        }
        finally
        {
            foreach (string path in new[] { outputPath, $"{outputPath}.etlx" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [TestMethod]
    public void Run_WhenElevated_RepeatedIterations_RecordsEveryLaunchInOneSession()
    {
        if (!EtwCollector.IsSupported || !EtwCollector.IsElevated)
        {
            Assert.Inconclusive("ETW capture needs Windows + Administrator; not available here.");
        }

        string outputPath = Path.Join(Path.GetTempPath(), $"filtrace-iterations-{Guid.NewGuid():N}.etl");
        EtwCollectRequest request = new()
        {
            LaunchExecutable = "cmd.exe",
            LaunchArguments = "/c exit 0",
            OutputPath = outputPath,
            Iterations = 3,
            DurationSeconds = 60,
        };

        try
        {
            EtwCollectResult result = EtwCollector.Collect(request);

            // One trace, three launches: the point of the iteration count is that the
            // session is paid for once rather than per run.
            result.Invocations.Should().HaveCount(3);
            result.Invocations.Select(static invocation => invocation.Ordinal).Should().Equal(1, 2, 3);
            result.Invocations.Should().AllSatisfy(
                static invocation => invocation.ExitCode.Should().Be(0));

            // Distinct processes run in sequence, so the ids differ and no run starts
            // before the previous one is observed to have stopped.
            result.Invocations.Select(static invocation => invocation.ProcessId).Distinct()
                .Should().HaveCount(3);

            result.Invocations[1].StartedUtc.Should().BeOnOrAfter(result.Invocations[0].StoppedUtc);
            result.Invocations[2].StartedUtc.Should().BeOnOrAfter(result.Invocations[1].StoppedUtc);

            result.ProcessId.Should().Be(result.Invocations[0].ProcessId);
            File.Exists(outputPath).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Run_WhenElevated_FailingIteration_ReportsTheFailureNotTheLastResult()
    {
        if (!EtwCollector.IsSupported || !EtwCollector.IsElevated)
        {
            Assert.Inconclusive("ETW capture needs Windows + Administrator; not available here.");
        }

        // Every launch fails, so the single reported exit code has to be a failure rather
        // than whichever run happened to finish last.
        string outputPath = Path.Join(Path.GetTempPath(), $"filtrace-iterations-{Guid.NewGuid():N}.etl");
        EtwCollectRequest request = new()
        {
            LaunchExecutable = "cmd.exe",
            LaunchArguments = "/c exit 3",
            OutputPath = outputPath,
            Iterations = 2,
            DurationSeconds = 60,
        };

        try
        {
            EtwCollectResult result = EtwCollector.Collect(request);

            result.ProcessExitCode.Should().Be(3);
            result.Invocations.Should().HaveCount(2);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Run_WhenElevated_JsonFormat_CarriesEveryInvocation()
    {
        if (!EtwCollector.IsSupported || !EtwCollector.IsElevated)
        {
            Assert.Inconclusive("ETW capture needs Windows + Administrator; not available here.");
        }

        // A capture script has to record each launch in its manifest, and reading them back
        // out of the human summary would be guesswork - this is the machine-readable path
        // that makes the manifest's invocations accurate.
        string outputPath = Path.Join(Path.GetTempPath(), $"filtrace-json-{Guid.NewGuid():N}.etl");
        EtwCollectRequest request = new()
        {
            LaunchExecutable = "cmd.exe",
            LaunchArguments = "/c exit 0",
            OutputPath = outputPath,
            Iterations = 2,
            DurationSeconds = 60,
        };

        StringWriter output = new();
        StringWriter error = new();
        try
        {
            int exit = CollectExecutor.Run(request, OutputFormat.Json, output, error);

            exit.Should().Be(ExitCodes.Success);
            error.ToString().Should().BeEmpty();

            using JsonDocument document = JsonDocument.Parse(output.ToString());
            JsonElement invocations = document.RootElement
                .GetProperty("result").GetProperty("invocations");

            invocations.GetArrayLength().Should().Be(2);
            invocations[0].GetProperty("ordinal").GetInt32().Should().Be(1);
            invocations[0].GetProperty("processId").GetInt32().Should().BeGreaterThan(0);
            invocations[0].GetProperty("startedUtc").GetDateTimeOffset()
                .Should().BeBefore(invocations[1].GetProperty("startedUtc").GetDateTimeOffset());
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public void Collect_InvalidCpuSampleMSec_ThrowsArgumentOutOfRange()
    {
        // Input validation runs before the OS / elevation guard, so this is deterministic
        // on every OS.
        Action act = () => EtwCollector.Collect(new EtwCollectRequest
        {
            LaunchExecutable = "app.exe",
            OutputPath = "out.etl",
            CpuSampleMSec = 0,
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void Collect_NonPositiveMaxSizeMB_ThrowsArgumentOutOfRange()
    {
        // Input validation runs before the OS / elevation guard, so this is deterministic
        // on every OS. A set-but-non-positive cap is an error; omitting it (null) is how a
        // capture stays unbounded.
        Action act = () => EtwCollector.Collect(new EtwCollectRequest
        {
            LaunchExecutable = "app.exe",
            OutputPath = "out.etl",
            MaxSizeMB = 0,
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void Collect_NegativeDuration_ThrowsArgumentOutOfRange()
    {
        Action act = () => EtwCollector.Collect(new EtwCollectRequest
        {
            LaunchExecutable = "app.exe",
            OutputPath = "out.etl",
            DurationSeconds = -5,
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
