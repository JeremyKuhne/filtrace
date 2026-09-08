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

        object writeLock = new();
        Task outputDrain = Task.CompletedTask;
        if (standardOutput is not null)
        {
            outputDrain = ForwardAsync(process.StandardOutput, standardOutput, "stdout", writeLock);
        }

        Task errorDrain = Task.CompletedTask;
        if (standardError is not null)
        {
            errorDrain = ForwardAsync(process.StandardError, standardError, "stderr", writeLock);
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
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the HasExited check and the kill.
            }

            process.WaitForExit();
            exitCode = -1;
        }

        Task.WhenAll(outputDrain, errorDrain).GetAwaiter().GetResult();
        return new EtwInvocation(ordinal, process.Id, exitCode, startedUtc, DateTimeOffset.UtcNow);
    }

    private static async Task ForwardAsync(
        StreamReader reader,
        TextWriter writer,
        string streamName,
        object writeLock)
    {
        char[] buffer = new char[4096];
        ExceptionDispatchInfo? writeFailure = null;

        while (await reader.ReadAsync(buffer).ConfigureAwait(continueOnCapturedContext: false) is int count and > 0)
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

        writeFailure?.Throw();
    }
}