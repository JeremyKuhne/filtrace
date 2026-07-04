// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

[TestClass]
public sealed class CollectExecutorTests
{
    // A real capture needs Windows + Administrator. CI has neither, so the guard and
    // validation paths run everywhere while the actual-capture path runs only when the
    // environment can support it (and is inconclusive otherwise).

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
    public void Run_WhenNotElevated_ReportsCleanError()
    {
        // When a real capture could run there is no clean-error to observe; the elevated
        // path is covered by Run_WhenElevated_ProducesEtl instead.
        if (EtwCollector.IsSupported && EtwCollector.IsElevated)
        {
            Assert.Inconclusive("Elevated: the not-elevated guard cannot be exercised here.");
        }

        string outputPath = Path.Combine(Path.GetTempPath(), $"filtrace-collect-{Guid.NewGuid():N}.etl");
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
        // so the same test is meaningful on a dev box and inert in CI.
        if (!EtwCollector.IsSupported || !EtwCollector.IsElevated)
        {
            Assert.Inconclusive("ETW capture needs Windows + Administrator; not available here.");
        }

        string outputPath = Path.Combine(Path.GetTempPath(), $"filtrace-collect-{Guid.NewGuid():N}.etl");
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
