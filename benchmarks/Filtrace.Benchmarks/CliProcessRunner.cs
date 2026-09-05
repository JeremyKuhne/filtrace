// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Filtrace.Benchmarks;

/// <summary>
///  Launches filtrace out of process while draining bounded output and, for telemetry
///  campaigns, sampling child CPU and memory counters.
/// </summary>
internal static partial class CliProcessRunner
{
    /// <summary>
    ///  The environment variable that overrides Release-build executable discovery.
    /// </summary>
    public const string FiltracePathEnvironmentVariable = "FILTRACE_BENCHMARK_CLI_PATH";
    private const int MaximumCapturedCharacters = 10 * 1024 * 1024;
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///  Resolves an explicit executable override or walks to the repository root and
    ///  selects the Release filtrace executable for the current platform.
    /// </summary>
    /// <returns>The full path to an existing filtrace executable.</returns>
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

    /// <summary>
    ///  Runs one measured child process and reduces its bounded redirected streams to
    ///  the values consumed by BenchmarkDotNet.
    /// </summary>
    /// <param name="executable">The filtrace executable to launch.</param>
    /// <param name="arguments">The argument tokens passed without shell parsing.</param>
    /// <returns>A task containing the exit code and stdout and stderr character counts.</returns>
    public static async Task<CliProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments)
    {
        ProcessObservation observation = await RunCoreAsync(
            executable,
            arguments,
                samplePrivateMemory: false).ConfigureAwait(continueOnCapturedContext: false);

        ThrowIfUnsuccessful(observation, executable);

        return new CliProcessResult(
            observation.ExitCode,
            observation.StandardOutput.Length,
            observation.StandardError.Length);
    }

    /// <summary>
    ///  Runs one telemetry launch and records output identity together with final CPU,
    ///  peak working-set, and sampled private-memory measurements.
    /// </summary>
    /// <param name="executable">The filtrace executable to launch.</param>
    /// <param name="arguments">The argument tokens passed without shell parsing.</param>
    /// <param name="iteration">The one-based launch number within the campaign.</param>
    /// <returns>A task containing the serialized telemetry for the completed launch.</returns>
    public static async Task<CliProcessTelemetry> RunTelemetryAsync(
        string executable,
        IReadOnlyList<string> arguments,
        int iteration)
    {
        ProcessObservation observation = await RunCoreAsync(
            executable,
            arguments,
                samplePrivateMemory: true).ConfigureAwait(continueOnCapturedContext: false);

        CliProcessTelemetry telemetry = new(
            iteration,
            [.. arguments],
            observation.Elapsed.TotalMilliseconds,
            observation.TotalProcessorTime.TotalMilliseconds,
            observation.PeakWorkingSetBytes,
            observation.MaxPrivateMemoryBytes,
            observation.ExitCode,
            observation.StandardOutput.Length,
            observation.StandardError.Length,
            ComputeDigest(observation));

        Exception? failure = GetFailure(observation, executable);
        if (failure is not null)
        {
            throw new CliProcessTelemetryException(telemetry, failure);
        }

        try
        {
            string normalizedOutput = NormalizeComparisonOutput(observation.StandardOutput, arguments);
            return telemetry with { ComparisonOutputSha256 = ComputeDigest(observation, normalizedOutput) };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new CliProcessTelemetryException(telemetry, exception);
        }
    }

    /// <summary>
    ///  Normalizes validated scenario paths without discarding result or diagnostic fields.
    /// </summary>
    /// <param name="standardOutput">The complete child stdout.</param>
    /// <param name="arguments">The exact arguments used to launch the child.</param>
    /// <returns>Path-normalized info or batch JSON, or unchanged output for other commands.</returns>
    internal static string NormalizeComparisonOutput(string standardOutput, IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2 || arguments[0] is not ("info" or "batch"))
        {
            return standardOutput;
        }

        JsonNode? document = JsonNode.Parse(standardOutput);
        if (document is not JsonObject envelope
            || envelope["result"] is not JsonObject result)
        {
            throw new InvalidDataException("The scenario response must contain a JSON result object.");
        }

        if (arguments[0] == "info")
        {
            NormalizeComparisonPath(result, "path", arguments[1], "$trace");
            for (int index = 2; index + 1 < arguments.Count; index++)
            {
                if (arguments[index] == "--symbols"
                    && result["sourceResolution"] is JsonObject sourceResolution
                    && sourceResolution["searchedDirectories"] is JsonArray directories)
                {
                    for (int directoryIndex = 0; directoryIndex < directories.Count; directoryIndex++)
                    {
                        ValidateComparisonPath(directories[directoryIndex], arguments[index + 1]);
                        directories[directoryIndex] = "$symbols";
                    }
                }
            }
        }
        else
        {
            NormalizeComparisonPath(result, "manifestPath", arguments[1], "$manifest");
            string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(arguments[1]))!;
            if (result["cases"] is not JsonArray cases)
            {
                throw new InvalidDataException("The batch response must contain a cases array.");
            }

            foreach (JsonNode? item in cases)
            {
                if (item is not JsonObject captureCase
                    || captureCase["tracePath"] is not JsonValue tracePath
                    || !tracePath.TryGetValue(out string? reportedPath)
                    || reportedPath is null
                    || !Path.IsPathFullyQualified(reportedPath))
                {
                    throw new InvalidDataException("Each batch case must identify an absolute tracePath.");
                }

                string relative = Path.GetRelativePath(manifestDirectory, reportedPath);
                if (Path.IsPathFullyQualified(relative)
                    || relative == ".."
                    || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The batch tracePath is outside the scenario's manifest directory.");
                }

                captureCase["tracePath"] = $"$manifest/{relative.Replace(Path.DirectorySeparatorChar, '/')}";
            }

            if (envelope["hints"] is JsonArray hints)
            {
                foreach (JsonNode? hint in hints)
                {
                    if (hint is JsonObject nextStep
                        && nextStep["arguments"] is JsonObject nextArguments
                        && nextArguments.ContainsKey("manifestPath"))
                    {
                        NormalizeComparisonPath(nextArguments, "manifestPath", arguments[1], "$manifest");
                    }
                }
            }
        }

        return document.ToJsonString();
    }

    private static void NormalizeComparisonPath(JsonObject owner, string property, string expectedPath, string replacement)
    {
        ValidateComparisonPath(owner[property], expectedPath);
        owner[property] = replacement;
    }

    private static void ValidateComparisonPath(JsonNode? value, string expectedPath)
    {
        StringComparison pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (value is not JsonValue path
            || !path.TryGetValue(out string? reportedPath)
            || !string.Equals(reportedPath, Path.GetFullPath(expectedPath), pathComparison))
        {
            throw new InvalidDataException("The scenario response contains an unverified input path.");
        }
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
        long startedTimestamp = Stopwatch.GetTimestamp();
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

                await Task.WhenAny(waitForExit, Task.Delay(10)).ConfigureAwait(continueOnCapturedContext: false);
                if (standardOutput.IsFaulted || standardError.IsFaulted)
                {
                    await StopProcessAsync(process).ConfigureAwait(continueOnCapturedContext: false);
                    await Task.WhenAll(standardOutput, standardError).ConfigureAwait(continueOnCapturedContext: false);
                }
            }

            await waitForExit.ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Exception? cleanupError = null;
            try
            {
                await StopProcessAsync(process).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }

            try
            {
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (InvalidDataException)
            {
                // Preserve the timeout as the controlling failure.
            }

            throw new TimeoutException(
                $"'{executable}' did not exit within {ProcessTimeout.TotalSeconds:N0} seconds.",
                cleanupError);
        }

        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(continueOnCapturedContext: false);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        string output = await standardOutput.ConfigureAwait(continueOnCapturedContext: false);
        string error = await standardError.ConfigureAwait(continueOnCapturedContext: false);

        return new ProcessObservation(
            process.ExitCode,
            output,
            error,
            elapsed,
            totalProcessorTime,
            peakWorkingSetBytes,
            maxPrivateMemoryBytes);
    }

    private static void ThrowIfUnsuccessful(ProcessObservation observation, string executable)
    {
        Exception? failure = GetFailure(observation, executable);
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static Exception? GetFailure(ProcessObservation observation, string executable)
    {
        if (observation.ExitCode != 0)
        {
            string detail = observation.StandardError.Length <= 1_000
                ? observation.StandardError
                : observation.StandardError[..1_000];

            return new InvalidOperationException(
                $"'{executable}' exited with code {observation.ExitCode}: {detail}");
        }

        if (observation.StandardOutput.Length == 0 || observation.StandardError.Length != 0)
        {
            return new InvalidDataException(
                $"'{executable}' exited successfully with {observation.StandardOutput.Length} stdout "
                    + $"and {observation.StandardError.Length} stderr characters.");
        }

        return null;
    }

    private static async Task<string> ReadWithLimitAsync(TextReader reader, string streamName)
    {
        ArrayBufferWriter<char> output = new();
        while (true)
        {
            int remaining = MaximumCapturedCharacters - output.WrittenCount;
            int requested = Math.Min(4_096, remaining + 1);
            Memory<char> buffer = output.GetMemory(requested)[..requested];
            int read = await reader.ReadAsync(buffer).ConfigureAwait(continueOnCapturedContext: false);
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
            await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(continueOnCapturedContext: false);
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

    private static string ComputeDigest(ProcessObservation observation, string? standardOutput = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(observation.ExitCode));
        hash.AppendData(Encoding.UTF8.GetBytes(standardOutput ?? observation.StandardOutput));
        hash.AppendData(Encoding.UTF8.GetBytes(observation.StandardError));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

}
