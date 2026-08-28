// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Filtrace.Benchmarks;

internal static partial class CliProcessRunner
{
    public const string FiltracePathEnvironmentVariable = "FILTRACE_BENCHMARK_CLI_PATH";
    private const int MaximumCapturedCharacters = 10 * 1024 * 1024;
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    public static string FindFiltraceExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable(FiltracePathEnvironmentVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            string configuredPath = Path.GetFullPath(configured);
            if (!File.Exists(configuredPath))
            {
                throw new FileNotFoundException(
                    $"{FiltracePathEnvironmentVariable} does not name a built filtrace executable.",
                    configuredPath);
            }

            return configuredPath;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "filtrace.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException(
                $"Could not find the repository root above '{AppContext.BaseDirectory}'.");
        }

        string executable = Path.Join(
            directory.FullName,
            "src",
            "Filtrace",
            "bin",
            "Release",
            "net10.0",
            OperatingSystem.IsWindows() ? "filtrace.exe" : "filtrace");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The Release filtrace executable was not built.",
                executable);
        }

        return executable;
    }

    public static async Task<CliProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments)
    {
        ProcessObservation observation = await RunCoreAsync(
            executable,
            arguments,
            samplePrivateMemory: false).ConfigureAwait(false);
        return new CliProcessResult(
            observation.ExitCode,
            observation.StandardOutput.Length,
            observation.StandardError.Length);
    }

    public static async Task<CliProcessTelemetry> RunTelemetryAsync(
        string executable,
        IReadOnlyList<string> arguments,
        int iteration)
    {
        ProcessObservation observation = await RunCoreAsync(
            executable,
            arguments,
            samplePrivateMemory: true).ConfigureAwait(false);
        return new CliProcessTelemetry(
            iteration,
            [.. arguments],
            observation.TotalProcessorTime.TotalMilliseconds,
            observation.PeakWorkingSetBytes,
            observation.MaxPrivateMemoryBytes,
            observation.ExitCode,
            observation.StandardOutput.Length,
            observation.StandardError.Length,
            ComputeDigest(observation));
    }

    private static async Task<ProcessObservation> RunCoreAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool samplePrivateMemory)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{executable}'.");
        }

        Task<string> standardOutput = ReadWithLimitAsync(process.StandardOutput, "stdout");
        Task<string> standardError = ReadWithLimitAsync(process.StandardError, "stderr");
        using CancellationTokenSource timeout = new(ProcessTimeout);
        TimeSpan totalProcessorTime = TimeSpan.Zero;
        long peakWorkingSetBytes = 0;
        long maxPrivateMemoryBytes = 0;
        if (samplePrivateMemory)
        {
            // Capture short-lived startup state before the first sampling delay.
            SampleProcessCounters(
                process,
                ref totalProcessorTime,
                ref peakWorkingSetBytes,
                ref maxPrivateMemoryBytes);
        }

        Task waitForExit = process.WaitForExitAsync(timeout.Token);
        try
        {
            while (!waitForExit.IsCompleted)
            {
                if (samplePrivateMemory)
                {
                    SampleProcessCounters(
                        process,
                        ref totalProcessorTime,
                        ref peakWorkingSetBytes,
                        ref maxPrivateMemoryBytes);
                }

                await Task.WhenAny(waitForExit, Task.Delay(10)).ConfigureAwait(false);
                if (standardOutput.IsFaulted || standardError.IsFaulted)
                {
                    await StopProcessAsync(process).ConfigureAwait(false);
                    await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
                }
            }

            await waitForExit.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Exception? cleanupError = null;
            try
            {
                await StopProcessAsync(process).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }

            try
            {
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // Preserve the timeout as the controlling failure.
            }

            throw new TimeoutException(
                $"'{executable}' did not exit within {ProcessTimeout.TotalSeconds:N0} seconds.",
                cleanupError);
        }

        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string detail = error.Length <= 1_000 ? error : error[..1_000];
            throw new InvalidOperationException(
                $"'{executable}' exited with code {process.ExitCode}: {detail}");
        }

        if (output.Length == 0 || error.Length != 0)
        {
            throw new InvalidDataException(
                $"'{executable}' exited successfully with {output.Length} stdout and {error.Length} stderr characters.");
        }

        return new ProcessObservation(
            process.ExitCode,
            output,
            error,
            totalProcessorTime,
            peakWorkingSetBytes,
            maxPrivateMemoryBytes);
    }

    private static async Task<string> ReadWithLimitAsync(TextReader reader, string streamName)
    {
        ArrayBufferWriter<char> output = new();
        while (true)
        {
            int remaining = MaximumCapturedCharacters - output.WrittenCount;
            int requested = Math.Min(4_096, remaining + 1);
            Memory<char> buffer = output.GetMemory(requested)[..requested];
            int read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return new string(output.WrittenSpan);
            }

            if (read > remaining)
            {
                throw new InvalidDataException(
                    $"Child {streamName} exceeded {MaximumCapturedCharacters:N0} characters.");
            }

            output.Advance(read);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        Exception? terminationError = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            terminationError = exception;
        }

        using CancellationTokenSource cleanupTimeout = new(ProcessCleanupTimeout);
        try
        {
            await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Child process did not exit within {ProcessCleanupTimeout.TotalSeconds:N0} seconds after termination.",
                terminationError);
        }

        if (terminationError is not null && !process.HasExited)
        {
            throw new InvalidOperationException("Failed to terminate the child process tree.", terminationError);
        }
    }

    private static void SampleProcessCounters(
        Process process,
        ref TimeSpan totalProcessorTime,
        ref long peakWorkingSetBytes,
        ref long maxPrivateMemoryBytes)
    {
        try
        {
            process.Refresh();
            if (process.TotalProcessorTime > totalProcessorTime)
            {
                totalProcessorTime = process.TotalProcessorTime;
            }

            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.PeakWorkingSet64);
            maxPrivateMemoryBytes = Math.Max(maxPrivateMemoryBytes, process.PrivateMemorySize64);
        }
        catch (InvalidOperationException)
        {
            // The child exited between the wait-state check and the counter read.
        }
    }

    private static string ComputeDigest(ProcessObservation observation)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(observation.ExitCode));
        hash.AppendData(Encoding.UTF8.GetBytes(observation.StandardOutput));
        hash.AppendData(Encoding.UTF8.GetBytes(observation.StandardError));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

}
