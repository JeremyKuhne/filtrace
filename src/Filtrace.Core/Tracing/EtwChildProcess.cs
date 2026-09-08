// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Filtrace.Tracing;

/// <summary>
///  Launches one capture subject and optionally drains its standard streams.
/// </summary>
internal static class EtwChildProcess
{
    /// <summary>
    ///  The largest whole-second duration representable by the millisecond wait API.
    /// </summary>
    internal const int MaxDurationSeconds = int.MaxValue / 1000;

    private static readonly TimeSpan s_postExitDrainGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    ///  Runs one subject process, terminating it at the configured duration cap.
    /// </summary>
    /// <param name="startInfo">The process launch configuration.</param>
    /// <param name="ordinal">The launch's one-based position in the capture.</param>
    /// <param name="durationSeconds">The optional duration cap.</param>
    /// <param name="standardOutput">The optional destination for identified subject output.</param>
    /// <param name="standardError">The optional destination for identified subject errors.</param>
    /// <returns>The observed process identity, timing, and exit status.</returns>
    internal static EtwInvocation Run(
        ProcessStartInfo startInfo,
        int ordinal,
        int? durationSeconds,
        TextWriter? standardOutput = null,
        TextWriter? standardError = null)
    {
        startInfo.RedirectStandardOutput = standardOutput is not null;
        startInfo.RedirectStandardError = standardError is not null;

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to launch '{startInfo.FileName}'.");

        Lock writeLock = new();
        using CancellationTokenSource drainCancellation = new();
        Task outputDrain = Task.CompletedTask;
        if (standardOutput is not null)
        {
            outputDrain = ForwardAsync(
                process.StandardOutput,
                standardOutput,
                "stdout",
                writeLock,
                drainCancellation.Token);
        }

        Task errorDrain = Task.CompletedTask;
        if (standardError is not null)
        {
            errorDrain = ForwardAsync(
                process.StandardError,
                standardError,
                "stderr",
                writeLock,
                drainCancellation.Token);
        }

        int exitCode;
        bool exited;
        if (durationSeconds is int seconds and > 0)
        {
            exited = process.WaitForExit(seconds * 1000);
        }
        else
        {
            process.WaitForExit();
            exited = true;
        }

        if (exited)
        {
            exitCode = process.ExitCode;
        }
        else if (process.HasExited)
        {
            exitCode = process.ExitCode;
        }
        else
        {
            bool terminated = false;
            try
            {
                process.Kill(entireProcessTree: true);
                terminated = true;
            }
            catch (InvalidOperationException)
            {
                // The process exited between the HasExited check and the kill.
            }

            process.WaitForExit();
            exitCode = terminated ? -1 : process.ExitCode;
        }

        DateTimeOffset stoppedUtc = DateTimeOffset.UtcNow;
        Task drains = Task.WhenAll(outputDrain, errorDrain);
        if (!WaitForCompletion(drains, s_postExitDrainGrace))
        {
            drainCancellation.Cancel();
            if (standardOutput is not null)
            {
                process.StandardOutput.Dispose();
            }

            if (standardError is not null)
            {
                process.StandardError.Dispose();
            }

            if (!WaitForCompletion(drains, s_postExitDrainGrace))
            {
                ObserveFault(drains);
                throw new TimeoutException("The subject's redirected output did not stop after cancellation.");
            }
        }

        GetDrainResult(drains);
        return new EtwInvocation(ordinal, process.Id, exitCode, startedUtc, stoppedUtc);
    }

    private static async Task ForwardAsync(
        StreamReader reader,
        TextWriter writer,
        string streamName,
        Lock writeLock,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        ExceptionDispatchInfo? writeFailure = null;

        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false) is int count and > 0)
            {
                if (writeFailure is not null)
                {
                    continue;
                }

                try
                {
                    lock (writeLock)
                    {
                        writer.WriteLine($"[subject {streamName}]");
                        writer.Write(buffer, 0, count);
                        if (buffer[count - 1] != '\n')
                        {
                            writer.WriteLine();
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Keep draining both pipes so a writer failure cannot strand the child.
                    writeFailure = ExceptionDispatchInfo.Capture(ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }

        writeFailure?.Throw();
    }

    private static bool WaitForCompletion(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        Task completed = Task.WhenAny(task, Task.Delay(timeout)).GetAwaiter().GetResult();
        return ReferenceEquals(completed, task) || task.IsCompleted;
    }

    private static void GetDrainResult(Task drains)
    {
        try
        {
            drains.GetAwaiter().GetResult();
        }
        catch
        {
            _ = drains.Exception;
            throw;
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}