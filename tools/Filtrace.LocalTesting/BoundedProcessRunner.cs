// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;

namespace Filtrace.LocalTesting;

/// <summary>
///  Runs child processes with bounded concurrent stream draining and deadlines.
/// </summary>
internal sealed class BoundedProcessRunner : IProcessRunner
{
    /// <summary>
    ///  The maximum number of characters retained from each output stream.
    /// </summary>
    internal const int MaxOutputCharacters = 1024 * 1024;
    private readonly TimeSpan _terminationTimeout;
    private readonly TimeSpan _outputDrainTimeout;

    /// <summary>
    ///  Creates a runner with production process termination and output drain deadlines.
    /// </summary>
    public BoundedProcessRunner()
        : this(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>
    ///  Creates a runner with testable post-deadline bounds.
    /// </summary>
    /// <param name="terminationTimeout">The maximum post-kill root-process wait.</param>
    /// <param name="outputDrainTimeout">The maximum wait for redirected-stream end-of-file.</param>
    internal BoundedProcessRunner(TimeSpan terminationTimeout, TimeSpan outputDrainTimeout)
    {
        if (terminationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationTimeout));
        }

        if (outputDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(outputDrainTimeout));
        }

        _terminationTimeout = terminationTimeout;
        _outputDrainTimeout = outputDrainTimeout;
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(ProcessInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocation.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocation.WorkingDirectory);
        if (invocation.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invocation),
                invocation.Timeout,
                "The process timeout must be positive.");
        }

        using CancellationTokenSource deadline = new(invocation.Timeout);

        ProcessStartInfo startInfo = new(invocation.FileName)
        {
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (string argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (invocation.EnvironmentVariables is not null)
        {
            foreach ((string name, string? value) in invocation.EnvironmentVariables)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        using Process process = StartProcess(startInfo);
        int rootProcessId = process.Id;

        using CancellationTokenSource captureCancellation = new();
        ProcessOutputCapture standardOutputState = new(MaxOutputCharacters);
        ProcessOutputCapture standardErrorState = new(MaxOutputCharacters);
        Task<bool> standardOutput = CaptureAsync(
            process.StandardOutput,
            standardOutputState,
            captureCancellation.Token);

        Task<bool> standardError = CaptureAsync(
            process.StandardError,
            standardErrorState,
            captureCancellation.Token);

        bool executionTimedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            executionTimedOut = true;
            TryTerminate(process);
            using CancellationTokenSource terminationDeadline = new(_terminationTimeout);
            try
            {
                await process.WaitForExitAsync(terminationDeadline.Token);
            }
            catch (OperationCanceledException) when (terminationDeadline.IsCancellationRequested)
            {
            }
        }

        Task outputTask = Task.WhenAll(standardOutput, standardError);
        bool outputCompletedBeforeDeadline = await CompletesWithinAsync(outputTask, _outputDrainTimeout);
        if (!outputCompletedBeforeDeadline)
        {
            captureCancellation.Cancel();
            await CompletesWithinAsync(outputTask, _terminationTimeout);
        }

        ObserveLateFailure(outputTask);
        (string standardOutputText, bool standardOutputTruncated) = standardOutputState.Snapshot();
        (string standardErrorText, bool standardErrorTruncated) = standardErrorState.Snapshot();
        bool outputCaptureIncomplete = !outputCompletedBeforeDeadline
            || !CompletedWithEndOfFile(standardOutput)
            || !CompletedWithEndOfFile(standardError);

        return new(
            TryGetExitCode(process),
            standardOutputText,
            standardErrorText,
            standardOutputTruncated,
            standardErrorTruncated,
            executionTimedOut,
            outputCaptureIncomplete,
            rootProcessId);
    }

    private static async Task<bool> CaptureAsync(
        StreamReader reader,
        ProcessOutputCapture state,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer, cancellationToken);
                if (read is 0)
                {
                    return true;
                }

                state.Append(buffer, read);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        return await Task.WhenAny(task, Task.Delay(timeout)) == task;
    }

    private static bool CompletedWithEndOfFile(Task<bool> capture)
    {
        return capture.IsCompletedSuccessfully && capture.Result;
    }

    private static void ObserveLateFailure(Task task)
    {
        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Process StartProcess(ProcessStartInfo startInfo)
    {
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
        }
    }

}