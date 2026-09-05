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
            await WaitForSelfExpiringProbeChildAsync(processIdPath);
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
            if (childProcess is not null)
            {
                await StopOwnedProbeChildAsync(childProcess, releasePath);
                childProcess.Dispose();
            }
            else
            {
                File.WriteAllText(releasePath, string.Empty);
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

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task RunAsync_NonpositiveTimeout_ThrowsBeforeProcessStart(int timeoutMilliseconds)
    {
        ProcessInvocation invocation = CreateMissingExecutableInvocation(
            TimeSpan.FromMilliseconds(timeoutMilliseconds));

        Func<Task> action = () => new BoundedProcessRunner().RunAsync(invocation);

        ArgumentOutOfRangeException exception = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(action);
        exception.ParamName.Should().Be("invocation");
        exception.ActualValue.Should().Be(invocation.Timeout);
    }

    [TestMethod]
    public async Task RunAsync_NullArguments_ThrowsBeforeProcessStart()
    {
        ProcessInvocation invocation = CreateMissingExecutableInvocation(TimeSpan.FromSeconds(1)) with
        {
            Arguments = null!
        };

        Func<Task> action = () => new BoundedProcessRunner().RunAsync(invocation);

        ArgumentNullException exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        exception.ParamName.Should().Be("invocation.Arguments");
    }

    [TestMethod]
    public async Task RunAsync_UnsupportedTimeout_ThrowsBeforeProcessStart()
    {
        ProcessInvocation invocation = CreateMissingExecutableInvocation(TimeSpan.MaxValue);

        Func<Task> action = () => new BoundedProcessRunner().RunAsync(invocation);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task WaitForProcessExitAsync_ExpiredDeadlineThrowsAndOwnedCleanupStopsProbe()
    {
        using TemporaryDirectory directory = new();
        string processIdPath = Path.Join(directory.Path, "child.pid");
        string releasePath = Path.Join(directory.Path, "release");
        using Process process = StartProbeProcess(
            "inherited-pipe-child",
            new Dictionary<string, string?>
            {
                [BoundedProcessRunnerProbe.ChildProcessIdPathVariable] = processIdPath,
                [BoundedProcessRunnerProbe.ChildReleasePathVariable] = releasePath
            });

        try
        {
            int processId = await WaitForProbeProcessIdAsync(processIdPath, TimeSpan.FromSeconds(2));
            processId.Should().Be(process.Id);

            Func<Task> wait = () => WaitForProcessExitAsync(process, TimeSpan.FromMilliseconds(100));

            await wait.Should().ThrowAsync<TimeoutException>();
        }
        finally
        {
            await StopOwnedProbeChildAsync(process, releasePath);
        }

        process.HasExited.Should().BeTrue();
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

    private static ProcessInvocation CreateMissingExecutableInvocation(TimeSpan timeout)
    {
        string missingExecutable = Path.Join(
            Path.GetTempPath(),
            $"filtrace-missing-{Guid.NewGuid():N}.exe");

        return new(missingExecutable, [], Environment.CurrentDirectory, timeout);
    }

    private static Process StartProbeProcess(
        string mode,
        IReadOnlyDictionary<string, string?> additionalEnvironment)
    {
        ProcessStartInfo startInfo = new(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(typeof(BoundedProcessRunnerTests).Assembly.Location);
        startInfo.Environment[BoundedProcessRunnerProbe.ModeVariable] = mode;
        foreach ((string name, string? value) in additionalEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start bounded-runner probe child.");
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
        int processId = await WaitForProbeProcessIdAsync(processIdPath, timeout);
        Process process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
            process.Dispose();
            throw new InvalidOperationException("The bounded-runner child exited before it was pinned.");
        }

        return process;
    }

    private static async Task<int> WaitForProbeProcessIdAsync(
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

        return processId;
    }

    private static async Task WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        await process.WaitForExitAsync().WaitAsync(timeout);
    }

    private static async Task StopOwnedProbeChildAsync(Process process, string releasePath)
    {
        File.WriteAllText(releasePath, string.Empty);
        try
        {
            await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(2));
            return;
        }
        catch (TimeoutException)
        {
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
        }

        await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForSelfExpiringProbeChildAsync(string processIdPath)
    {
        int processId = await WaitForProbeProcessIdAsync(processIdPath, TimeSpan.FromSeconds(2));
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(17));
        }
    }
}