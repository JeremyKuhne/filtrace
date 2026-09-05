// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.ComponentModel;
using System.Diagnostics;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class BoundedProcessRunnerTests
{
    [TestMethod]
    public async Task RunAsync_NormalExit_PreservesStructuredArgumentsAndEnvironmentOverride()
    {
        string? parentValue = Environment.GetEnvironmentVariable(
            BoundedProcessRunnerProbe.EnvironmentValueVariable);

        ProcessResult result = await new BoundedProcessRunner().RunAsync(CreateProbeInvocation(
            "arguments-environment",
            new Dictionary<string, string?>
            {
                [BoundedProcessRunnerProbe.EnvironmentValueVariable] = "child value"
            },
            ["argument with spaces", "--literal=*?[value]"]));

        result.ExitCode.Should().Be(0);
        result.RootProcessId.Should().NotBeNull();
        result.ExecutionTimedOut.Should().BeFalse();
        result.OutputCaptureIncomplete.Should().BeFalse();
        result.StandardOutputTruncated.Should().BeFalse();
        result.StandardErrorTruncated.Should().BeFalse();
        result.StandardOutput.Should().Be($"argument with spaces{Environment.NewLine}--literal=*?[value]");
        result.StandardError.Should().Be("child value");
        Environment.GetEnvironmentVariable(BoundedProcessRunnerProbe.EnvironmentValueVariable)
            .Should().Be(parentValue);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task RunAsync_NullEnvironmentValue_RemovesInheritedVariable()
    {
        string? parentValue = Environment.GetEnvironmentVariable(
            BoundedProcessRunnerProbe.EnvironmentValueVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                BoundedProcessRunnerProbe.EnvironmentValueVariable,
                "parent value");

            ProcessResult result = await new BoundedProcessRunner().RunAsync(CreateProbeInvocation(
                "arguments-environment",
                new Dictionary<string, string?>
                {
                    [BoundedProcessRunnerProbe.EnvironmentValueVariable] = null
                },
                ["first", "second"]));

            result.ExitCode.Should().Be(0);
            result.StandardError.Should().Be("<missing>");
            Environment.GetEnvironmentVariable(BoundedProcessRunnerProbe.EnvironmentValueVariable)
                .Should().Be("parent value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                BoundedProcessRunnerProbe.EnvironmentValueVariable,
                parentValue);
        }
    }

    [TestMethod]
    public async Task RunAsync_RootExitsWithInheritedPipes_ReturnsIncompleteCaptureWithinBound()
    {
        using TemporaryDirectory directory = new();
        string processIdPath = Path.Join(directory.Path, "child.pid");
        string releasePath = Path.Join(directory.Path, "release");
        ProcessResult? result = null;
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            result = await new BoundedProcessRunner(
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(150)).RunAsync(CreateProbeInvocation(
                    "inherited-pipe-parent",
                    new Dictionary<string, string?>
                    {
                        [BoundedProcessRunnerProbe.ChildProcessIdPathVariable] = processIdPath,
                        [BoundedProcessRunnerProbe.ChildReleasePathVariable] = releasePath
                    }));
        }
        finally
        {
            elapsed.Stop();
            File.WriteAllText(releasePath, string.Empty);
            await StopProbeChildAsync(processIdPath);
        }

        result.Should().NotBeNull();
        result!.ExitCode.Should().Be(0);
        result.RootProcessId.Should().NotBeNull();
        result.ExecutionTimedOut.Should().BeFalse();
        result.OutputCaptureIncomplete.Should().BeTrue();
        result.StandardOutputTruncated.Should().BeFalse();
        result.StandardErrorTruncated.Should().BeFalse();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task RunAsync_ExecutionTimeout_TerminatesRootAndCompletesCapture()
    {
        ProcessResult result = await new BoundedProcessRunner(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2)).RunAsync(CreateProbeInvocation(
                "execution-timeout",
                timeout: TimeSpan.FromMilliseconds(150)));

        result.RootProcessId.Should().NotBeNull();
        result.ExecutionTimedOut.Should().BeTrue();
        result.OutputCaptureIncomplete.Should().BeFalse();
        result.StandardOutputTruncated.Should().BeFalse();
        result.StandardErrorTruncated.Should().BeFalse();
        result.StandardOutput.Should().Be("started");
        IsProcessRunning(result.RootProcessId!.Value).Should().BeFalse();
    }

    [TestMethod]
    public async Task RunAsync_ExecutionTimeout_TerminatesProcessTreeAndCompletesCapture()
    {
        using TemporaryDirectory directory = new();
        string processIdPath = Path.Join(directory.Path, "child.pid");
        string releasePath = Path.Join(directory.Path, "release");
        Task<ProcessResult> resultTask = new BoundedProcessRunner(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2)).RunAsync(CreateProbeInvocation(
                "execution-timeout-tree",
                new Dictionary<string, string?>
                {
                    [BoundedProcessRunnerProbe.ChildProcessIdPathVariable] = processIdPath,
                    [BoundedProcessRunnerProbe.ChildReleasePathVariable] = releasePath
                },
                timeout: TimeSpan.FromSeconds(5)));

        Process? childProcess = null;
        try
        {
            childProcess = await WaitForProbeProcessAsync(processIdPath, TimeSpan.FromSeconds(4));
            ProcessResult result = await resultTask;
            await WaitForProcessExitAsync(childProcess, TimeSpan.FromSeconds(2));

            result.RootProcessId.Should().NotBeNull();
            result.ExecutionTimedOut.Should().BeTrue();
            result.OutputCaptureIncomplete.Should().BeFalse();
            result.StandardOutputTruncated.Should().BeFalse();
            result.StandardErrorTruncated.Should().BeFalse();
            result.StandardOutput.Should().Be("started");
            IsProcessRunning(result.RootProcessId!.Value).Should().BeFalse();
            childProcess.HasExited.Should().BeTrue();
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
            if (childProcess is not null)
            {
                await WaitForProcessExitAsync(childProcess, TimeSpan.FromSeconds(5));
                childProcess.Dispose();
            }

            if (!resultTask.IsCompleted)
            {
                await resultTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_SimultaneousOversizedStreamsAndNonzeroExit_OnlyReportsSizeTruncation()
    {
        ProcessResult result = await new BoundedProcessRunner().RunAsync(
            CreateProbeInvocation("oversized-output"));

        result.ExitCode.Should().Be(9);
        result.RootProcessId.Should().NotBeNull();
        result.ExecutionTimedOut.Should().BeFalse();
        result.OutputCaptureIncomplete.Should().BeFalse();
        result.StandardOutputTruncated.Should().BeTrue();
        result.StandardErrorTruncated.Should().BeTrue();
        result.StandardOutput.Should().HaveLength(BoundedProcessRunner.MaxOutputCharacters);
        result.StandardError.Should().HaveLength(BoundedProcessRunner.MaxOutputCharacters);
    }

    [TestMethod]
    public async Task RunAsync_MissingExecutable_ThrowsProcessStartFailure()
    {
        string missingExecutable = Path.Join(
            Path.GetTempPath(),
            $"filtrace-missing-{Guid.NewGuid():N}.exe");

        ProcessInvocation invocation = new(
            missingExecutable,
            [],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(1),
            new Dictionary<string, string?>());

        Func<Task> action = () => new BoundedProcessRunner().RunAsync(invocation);

        await action.Should().ThrowAsync<Win32Exception>();
    }

    private static ProcessInvocation CreateProbeInvocation(
        string mode,
        IReadOnlyDictionary<string, string?>? additionalEnvironment = null,
        IReadOnlyList<string>? additionalArguments = null,
        TimeSpan? timeout = null)
    {
        Dictionary<string, string?> environment = new()
        {
            [BoundedProcessRunnerProbe.ModeVariable] = mode
        };

        if (additionalEnvironment is not null)
        {
            foreach ((string name, string? value) in additionalEnvironment)
            {
                environment.Add(name, value);
            }
        }

        List<string> arguments = [typeof(BoundedProcessRunnerTests).Assembly.Location];
        if (additionalArguments is not null)
        {
            arguments.AddRange(additionalArguments);
        }

        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new(
            dotnet,
            arguments,
            Environment.CurrentDirectory,
            timeout ?? TimeSpan.FromSeconds(20),
            environment);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<Process> WaitForProbeProcessAsync(
        string processIdPath,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (!File.Exists(processIdPath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        if (!File.Exists(processIdPath))
        {
            throw new TimeoutException("The bounded-runner child did not report readiness.");
        }

        if (!int.TryParse(File.ReadAllText(processIdPath), out int processId))
        {
            throw new InvalidDataException("The bounded-runner child reported an invalid process ID.");
        }

        Process process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
            process.Dispose();
            throw new InvalidOperationException("The bounded-runner child exited before it was pinned.");
        }

        return process;
    }

    private static async Task WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        using CancellationTokenSource exitDeadline = new(timeout);
        try
        {
            await process.WaitForExitAsync(exitDeadline.Token);
        }
        catch (OperationCanceledException) when (exitDeadline.IsCancellationRequested)
        {
        }
    }

    private static async Task StopProbeChildAsync(string processIdPath)
    {
        DateTime pidDeadline = DateTime.UtcNow.AddSeconds(2);
        while (!File.Exists(processIdPath) && DateTime.UtcNow < pidDeadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        if (!File.Exists(processIdPath)
            || !int.TryParse(File.ReadAllText(processIdPath), out int processId))
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
        }
    }
}