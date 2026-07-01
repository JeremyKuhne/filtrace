// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Filtrace.Tracing;

/// <summary>
///  The metric an ETW capture is tuned for.
/// </summary>
public enum CollectMetric
{
    /// <summary>
    ///  CPU sampling (the kernel <c>Default</c> keyword set, whose <c>Profile</c> keyword
    ///  is the sampled profiler). Feeds <c>cpu</c> and <c>classify</c>.
    /// </summary>
    Cpu,

    /// <summary>
    ///  CPU sampling plus the context-switch keywords that carry blocked intervals, so
    ///  wall-clock time can be reconstructed. Feeds <c>threadtime</c> as well as <c>cpu</c>.
    /// </summary>
    ThreadTime,
}

/// <summary>
///  The inputs for an ETW capture (see <see cref="EtwCollector.Collect"/>).
/// </summary>
public sealed class EtwCollectRequest
{
    /// <summary>
    ///  The executable to launch and trace. The capture spans the process's whole
    ///  lifetime, so launching the built app directly (never <c>dotnet run</c>) keeps the
    ///  trace on the app rather than a build or launcher host.
    /// </summary>
    public required string LaunchExecutable { get; init; }

    /// <summary>
    ///  The arguments passed to <see cref="LaunchExecutable"/>, as a single command-line
    ///  string. Defaults to none.
    /// </summary>
    public string LaunchArguments { get; init; } = "";

    /// <summary>
    ///  The metric the capture is tuned for. Defaults to <see cref="CollectMetric.Cpu"/>.
    /// </summary>
    public CollectMetric Metric { get; init; } = CollectMetric.Cpu;

    /// <summary>
    ///  The CPU sample interval in milliseconds. Defaults to 1 ms (the ETW default).
    /// </summary>
    public double CpuSampleMSec { get; init; } = 1.0;

    /// <summary>
    ///  An optional cap on capture length in seconds. When set and the process is still
    ///  running at the cap, the capture stops and the process tree is terminated;
    ///  <see langword="null"/> (the default) captures until the process exits on its own.
    /// </summary>
    public int? DurationSeconds { get; init; }

    /// <summary>
    ///  The <c>.etl</c> file the capture is written to.
    /// </summary>
    public required string OutputPath { get; init; }
}

/// <summary>
///  The outcome of an ETW capture.
/// </summary>
public sealed class EtwCollectResult
{
    /// <summary>The <c>.etl</c> file the capture was written to (absolute path).</summary>
    public required string OutputPath { get; init; }

    /// <summary>The process id of the launched process.</summary>
    public required int ProcessId { get; init; }

    /// <summary>
    ///  The launched executable's base name, the value to scope analysis with
    ///  <c>--process</c> against the machine-wide capture.
    /// </summary>
    public required string ProcessName { get; init; }

    /// <summary>
    ///  The launched process's exit code, or <c>-1</c> if it was terminated at the
    ///  duration cap.
    /// </summary>
    public required int ProcessExitCode { get; init; }

    /// <summary>The size of the written <c>.etl</c> in bytes.</summary>
    public required long FileSizeBytes { get; init; }
}

/// <summary>
///  Records a Windows ETW (<c>.etl</c>) trace of a launched process - the capture step
///  the analysis verbs consume - built directly on TraceEvent's session API so no external
///  recorder (PerfView, <c>wpr</c>) is needed.
/// </summary>
/// <remarks>
///  <para>
///   A single session enables the kernel CPU (and, for thread time, context-switch) events
///   with stacks, plus the CLR <c>Jit</c> / <c>Loader</c> events that name managed methods.
///   Because a launch capture starts tracing before the process exists, every method is
///   jitted (and its name logged) after tracing begins, so the live method events resolve
///   the managed frames with no CLR rundown pass. Cross-machine native-symbol injection (the
///   PerfView "merge" step) is a deliberate follow-up; on the capture machine
///   <c>--native-symbols</c> already names native frames.
///  </para>
///  <para>
///   ETW kernel tracing is Windows-only and needs Administrator; both are checked up front
///   so the failure is a clean message rather than a native error.
///  </para>
/// </remarks>
public static class EtwCollector
{
    /// <summary>
    ///  Whether ETW capture is available on this OS (Windows only).
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    ///  Whether the current process is elevated enough to open a kernel ETW session.
    /// </summary>
    public static bool IsElevated => OperatingSystem.IsWindows() && TraceEventSession.IsElevated() == true;

    /// <summary>
    ///  Launches <see cref="EtwCollectRequest.LaunchExecutable"/> and records an ETW trace
    ///  of it to <see cref="EtwCollectRequest.OutputPath"/>.
    /// </summary>
    /// <param name="request">The capture inputs.</param>
    /// <returns>The capture outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required field is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric field is out of range.</exception>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    /// <exception cref="UnauthorizedAccessException">Not elevated.</exception>
    public static EtwCollectResult Collect(EtwCollectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.LaunchExecutable);
        ArgumentException.ThrowIfNullOrEmpty(request.OutputPath);

        if (!double.IsFinite(request.CpuSampleMSec) || request.CpuSampleMSec <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.CpuSampleMSec), request.CpuSampleMSec,
                "The CPU sample interval must be a positive, finite number of milliseconds.");
        }

        if (request.DurationSeconds is int durationSeconds && durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.DurationSeconds), durationSeconds,
                "The duration cap must be positive when set; omit it to capture until the process exits.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "ETW capture is Windows-only. Use an EventPipe capture (dotnet-trace) on this OS.");
        }

        if (TraceEventSession.IsElevated() != true)
        {
            throw new UnauthorizedAccessException(
                "ETW capture needs Administrator. Re-run elevated.");
        }

        return CollectCore(request);
    }

    [SupportedOSPlatform("windows")]
    private static EtwCollectResult CollectCore(EtwCollectRequest request)
    {
        string outputPath = Path.GetFullPath(request.OutputPath);
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        // ThreadTime = Default | ContextSwitch | Dispatcher; Default already carries the
        // Profile (CPU sampling), Process, Thread, and ImageLoad keywords.
        KernelTraceEventParser.Keywords kernelKeywords = request.Metric == CollectMetric.ThreadTime
            ? KernelTraceEventParser.Keywords.ThreadTime
            : KernelTraceEventParser.Keywords.Default;

        // Attach stacks only to the CPU-sample (and, for thread time, context-switch)
        // events - the ones whose call stacks the rankings and thread-time attribution
        // read. Stacking every Default event would bloat the trace without helping
        // analysis. This mirrors the repo's own ETW fixture capture.
        KernelTraceEventParser.Keywords stackKeywords = request.Metric == CollectMetric.ThreadTime
            ? KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.ContextSwitch
            : KernelTraceEventParser.Keywords.Profile;

        string processName = Path.GetFileNameWithoutExtension(request.LaunchExecutable);
        string sessionName = $"filtrace-collect-{Environment.ProcessId}";

        int processId;
        int exitCode;

        using (TraceEventSession session = new(sessionName, outputPath)
        {
            // Stop (and flush the .etl) when the session is disposed, even on an exception.
            StopOnDispose = true,
            CpuSampleIntervalMSec = (float)request.CpuSampleMSec,
        })
        {
            session.EnableKernelProvider(kernelKeywords, stackKeywords);
            session.EnableProvider(
                ClrTraceEventParser.ProviderGuid,
                TraceEventLevel.Verbose,
                (ulong)ClrTraceEventParser.Keywords.Default);

            ProcessStartInfo startInfo = new(request.LaunchExecutable)
            {
                Arguments = request.LaunchArguments,
                UseShellExecute = false,
            };

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to launch '{request.LaunchExecutable}'.");
            processId = process.Id;

            bool exited;
            if (request.DurationSeconds is int seconds and > 0)
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
                // The process exited on its own in the race between the wait timing out
                // and the kill below, so report the real exit code it produced.
                exitCode = process.ExitCode;
            }
            else
            {
                // The duration cap elapsed while the process was still running; stop the
                // tree. Tolerate the process exiting in the moment before the kill lands.
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
        }

        long fileSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
        return new EtwCollectResult
        {
            OutputPath = outputPath,
            ProcessId = processId,
            ProcessName = processName,
            ProcessExitCode = exitCode,
            FileSizeBytes = fileSize,
        };
    }
}
