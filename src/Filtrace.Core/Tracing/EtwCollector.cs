// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Runtime.Versioning;

namespace Filtrace.Tracing;

/// <summary>
///  Records a Windows ETW (<c>.etl</c>) trace of a launched process - the capture step
///  the analysis verbs consume - built directly on TraceEvent's session API so no external
///  recorder (PerfView, <c>wpr</c>) is needed.
/// </summary>
/// <remarks>
///  <para>
///   A single session enables the kernel CPU (and, for thread time, context-switch) events
///   with stacks, plus the CLR events that name managed methods. Because a launch capture
///   starts tracing before the process exists, every method is jitted (and its name logged)
///   after tracing begins, so the live method events resolve the managed frames with no CLR
///   rundown pass. Cross-machine native-symbol injection (the PerfView "merge" step) is a
///   deliberate follow-up; on the capture machine <c>--native-symbols</c> already names
///   native frames.
///  </para>
///  <para>
///   Which providers a capture enables is chosen by <see cref="CollectProfile"/>. No profile
///   enables the disk, network, or memory keywords: ETW is machine-wide, so those are paid
///   for by the whole box, and no analysis of a <c>collect</c> capture reads them. A
///   <c>diskio</c> capture therefore has to come from a recorder that asks for them
///   explicitly.
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
    ///  The most launches one session will wrap. Matches what the <c>collect</c> verb and
    ///  the capture manifest accept, so a capture taken through any entry point can be
    ///  described by a manifest the reader will take.
    /// </summary>
    public const int MaxIterations = 1000;

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
    public static EtwCollectResult Collect(EtwCollectRequest request) =>
        Collect(request, standardOutput: null, standardError: null);

    /// <summary>
    ///  Launches and captures the requested process while forwarding identified subject
    ///  streams to explicit writers.
    /// </summary>
    /// <param name="request">The capture inputs.</param>
    /// <param name="standardOutput">The destination for identified subject output.</param>
    /// <param name="standardError">The destination for identified subject errors.</param>
    /// <returns>The capture outcome.</returns>
    internal static EtwCollectResult Collect(
        EtwCollectRequest request,
        TextWriter? standardOutput,
        TextWriter? standardError)
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

        if (request.MaxSizeMB is int maxSizeMB && maxSizeMB <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxSizeMB), maxSizeMB,
                "The size cap must be positive when set; omit it to write an unbounded sequential file.");
        }

        if (request.Iterations is <= 0 or > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Iterations), request.Iterations,
                $"The iteration count must be between 1 and {MaxIterations}.");
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

        return CollectCore(request, standardOutput, standardError);
    }

    [SupportedOSPlatform("windows")]
    private static EtwCollectResult CollectCore(
        EtwCollectRequest request,
        TextWriter? standardOutput,
        TextWriter? standardError)
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
        CaptureProviders providers = CaptureProviders.For(request.Profile);

        string processName = Path.GetFileNameWithoutExtension(request.LaunchExecutable);
        string sessionName = $"filtrace-collect-{Environment.ProcessId}";

        List<EtwInvocation> invocations = new(request.Iterations);

        // The applied rate cannot be read back: TraceEventSession.CpuSampleIntervalMSec
        // returns the field it was assigned, and the OS echoes any value it was given
        // whether or not it honors it. Resolving against the reported bounds is what makes
        // a clamp visible.
        CpuSampleInterval cpuSample = CpuSampleBounds.Resolve(request.CpuSampleMSec);

        using (TraceEventSession session = new(sessionName, outputPath)
        {
            // Stop (and flush the .etl) when the session is disposed, even on an exception.
            StopOnDispose = true,
            CpuSampleIntervalMSec = (float)cpuSample.EffectiveMSec,
        })
        {
            // A size cap records into a fixed-size ring so an open-ended capture is bounded
            // to the last MaxSizeMB megabytes on disk. Set before enabling the providers so
            // the session starts circular.
            if (request.MaxSizeMB is int maxSizeMB)
            {
                session.CircularBufferMB = maxSizeMB;
            }

            session.EnableKernelProvider(providers.KernelKeywords, providers.StackKeywords);
            if (providers.EnablesClr)
            {
                session.EnableProvider(
                    ClrTraceEventParser.ProviderGuid,
                    providers.ClrLevel,
                    (ulong)providers.ClrKeywords);
            }

            ProcessStartInfo startInfo = new(request.LaunchExecutable)
            {
                Arguments = request.LaunchArguments,
                UseShellExecute = false,
            };

            // Sequential, inside the one session: the point is to amortize session startup
            // and flush over several runs, and overlapping them would make the per-run
            // windows useless for attributing time.
            for (int ordinal = 1; ordinal <= request.Iterations; ordinal++)
            {
                invocations.Add(EtwChildProcess.Run(
                    startInfo,
                    ordinal,
                    request.DurationSeconds,
                    standardOutput,
                    standardError));
            }
        }

        long fileSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;

        // Surface a failure rather than the last result, so a caller reading the single
        // exit code still learns that a run failed somewhere in the middle.
        EtwInvocation reported = invocations.Find(static invocation => invocation.ExitCode != 0)
            ?? invocations[^1];

        return new EtwCollectResult
        {
            OutputPath = outputPath,
            ProcessId = invocations[0].ProcessId,
            ProcessName = processName,
            ProcessExitCode = reported.ExitCode,
            Invocations = invocations,
            FileSizeBytes = fileSize,
            Profile = request.Profile,
            KernelKeywords = providers.KernelKeywords.ToString(),
            ClrKeywords = providers.EnablesClr ? providers.ClrKeywords.ToString() : "none",
            CpuSample = cpuSample,
        };
    }

}
