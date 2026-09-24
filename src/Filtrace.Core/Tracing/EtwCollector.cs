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
///   CPU and thread-time profiles enable sampled CPU (plus context switches for thread
///   time) with stacks and the CLR events that name managed methods. Because a launch
///   capture starts tracing before the process exists, every method is jitted (and its name
///   logged) after tracing begins, so the live method events resolve the managed frames
///   with no CLR rundown pass. Cross-machine native-symbol injection (the PerfView "merge"
///   step) is a deliberate follow-up; on the capture machine <c>--native-symbols</c>
///   already names native frames.
///  </para>
///  <para>
///   The disk-I/O profile is separate: it enables only process/thread attribution,
///   physical disk completion/init events, and the file-name rundown. It deliberately
///   omits sampled CPU, stacks, verbose FileIO, and CLR events so a machine-wide disk
///   capture pays only for data the disk report reads.
///  </para>
///  <para>
///   ETW kernel tracing is Windows-only and needs Administrator; both are checked up front
///   so the failure is a clean message rather than a native error.
///  </para>
/// </remarks>
public static class EtwCollector
{
    // TraceEvent converts this request to ETW minimum buffers and permits the pool to
    // grow to roughly 641 MiB through its derived maximum-buffer count.
    private const int RundownBufferSizeMB = 512;
    private const int RundownProviderEnableTimeoutMSec = 10_000;
    private const int RundownMaxPolls = 15;
    private static readonly TimeSpan s_rundownPollInterval = TimeSpan.FromSeconds(2);

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
    /// <exception cref="DirectoryNotFoundException">
    ///  <see cref="EtwCollectRequest.WorkingDirectory"/> does not resolve to an existing directory.
    /// </exception>
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

        if (request.DurationSeconds is int durationSeconds
            && (durationSeconds <= 0 || durationSeconds > EtwChildProcess.MaxDurationSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.DurationSeconds), durationSeconds,
                $"The duration cap must be between 1 and {EtwChildProcess.MaxDurationSeconds} seconds when set; omit it to capture until the process exits.");
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

        if (request.Rundown && request.Profile == CollectProfile.DiskIo)
        {
            throw new ArgumentException(
                "CLR rundown cannot be combined with the diskio profile, which enables no CLR or CPU data.",
                nameof(request));
        }

        if (request.Rundown && request.MaxSizeMB is not null)
        {
            throw new ArgumentException(
                "CLR rundown cannot be combined with a circular size cap because the merged rundown would exceed that bound.",
                nameof(request));
        }

        int[] rundownProcessIds = ValidateRundownProcessIds(request);
        string workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "ETW capture is Windows-only. Use an EventPipe capture (dotnet-trace) on this OS.");
        }

        EnsureRundownProcessFilteringSupported(
            rundownProcessIds.Length,
            TraceEventProviderOptions.FilteringSupported);

        if (TraceEventSession.IsElevated() != true)
        {
            throw new UnauthorizedAccessException(
                "ETW capture needs Administrator. Re-run elevated.");
        }

        return CollectCore(request, workingDirectory, rundownProcessIds, standardOutput, standardError);
    }

    private static int[] ValidateRundownProcessIds(EtwCollectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.RundownProcessIds);
        if (request.RundownProcessIds.Count == 0)
        {
            return [];
        }

        if (!request.Rundown)
        {
            throw new ArgumentException(
                "Rundown process ids require CLR rundown to be enabled.",
                nameof(request));
        }

        if (request.RundownProcessIds.Count > EtwCollectRequest.MaximumRundownProcessIds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.RundownProcessIds),
                request.RundownProcessIds.Count,
                $"At most {EtwCollectRequest.MaximumRundownProcessIds} rundown process ids may be specified.");
        }

        int[] processIds = new int[request.RundownProcessIds.Count];
        HashSet<int> seen = [];
        for (int index = 0; index < processIds.Length; index++)
        {
            int processId = request.RundownProcessIds[index];
            if (processId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request.RundownProcessIds),
                    processId,
                    "Rundown process ids must be positive.");
            }

            if (!seen.Add(processId))
            {
                throw new ArgumentException(
                    $"Rundown process id {processId} was specified more than once.",
                    nameof(request));
            }

            processIds[index] = processId;
        }

        return processIds;
    }

    /// <summary>
    ///  Rejects a requested PID filter when the host ETW version cannot apply it.
    /// </summary>
    /// <param name="processIdCount">Number of requested exact process ids.</param>
    /// <param name="filteringSupported">Whether the host supports ETW provider filters.</param>
    /// <exception cref="PlatformNotSupportedException">
    ///  One or more process ids were requested on a host without ETW provider filtering.
    /// </exception>
    internal static void EnsureRundownProcessFilteringSupported(
        int processIdCount,
        bool filteringSupported)
    {
        if (processIdCount > 0 && !filteringSupported)
        {
            throw new PlatformNotSupportedException(
                "PID-scoped CLR rundown requires ETW provider filtering, available on "
                    + "Windows 8.1 / Windows Server 2012 R2 or later.");
        }
    }

    /// <summary>
    ///  Resolves and validates the directory inherited by capture subjects.
    /// </summary>
    /// <param name="workingDirectory">The requested directory, or empty to inherit the collector directory.</param>
    /// <returns>The validated absolute directory.</returns>
    /// <exception cref="DirectoryNotFoundException">The resolved directory does not exist.</exception>
    internal static string ResolveWorkingDirectory(string? workingDirectory)
    {
        string requested = workingDirectory ?? "";
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = Environment.CurrentDirectory;
        }

        string resolved = Path.GetFullPath(requested);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: '{resolved}'.");
        }

        return resolved;
    }

    [SupportedOSPlatform("windows")]
    private static EtwCollectResult CollectCore(
        EtwCollectRequest request,
        string workingDirectory,
        IReadOnlyList<int> rundownProcessIds,
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

        CaptureProviders providers = CaptureProviders.For(request.Profile);

        string processName = Path.GetFileNameWithoutExtension(request.LaunchExecutable);
        string sessionName = $"filtrace-collect-{Environment.ProcessId}";

        List<EtwInvocation> invocations = new(request.Iterations);
        RundownCaptureInfo? rundown = null;

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
                WorkingDirectory = workingDirectory,
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

        if (request.Rundown)
        {
            rundown = CaptureRundownAndMerge(outputPath, sessionName, rundownProcessIds);
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
            WorkingDirectory = workingDirectory,
            Rundown = rundown,
            ProcessExitCode = reported.ExitCode,
            Invocations = invocations,
            FileSizeBytes = fileSize,
            Profile = request.Profile,
            KernelKeywords = providers.KernelKeywords.ToString(),
            ClrKeywords = providers.EnablesClr ? providers.ClrKeywords.ToString() : "none",
            CpuSample = cpuSample,
        };
    }

    [SupportedOSPlatform("windows")]
    private static RundownCaptureInfo CaptureRundownAndMerge(
        string outputPath,
        string sessionName,
        IReadOnlyList<int> processIds)
    {
        string token = Guid.NewGuid().ToString("N");
        string rundownPath = $"{outputPath}.rundown-{token}.etl";
        string mergedPath = $"{outputPath}.merged-{token}.etl";
        Stopwatch stopwatch = Stopwatch.StartNew();
        int polls = 0;
        int eventsLost = 0;
        long rundownSize = 0;

        try
        {
            long previousLength = -1;
            bool quiet = false;
            using (TraceEventSession rundown = new($"{sessionName}-rundown", rundownPath)
            {
                StopOnDispose = true,
                BufferSizeMB = RundownBufferSizeMB,
                // Keep provider activation synchronous before file-size stability polling.
                EnableProviderTimeoutMSec = RundownProviderEnableTimeoutMSec,
            })
            {
                if (processIds.Count == 0)
                {
                    rundown.EnableProvider(
                        ClrRundownTraceEventParser.ProviderGuid,
                        TraceEventLevel.Verbose,
                        (ulong)CaptureProviders.NamingRundownClrKeywords);
                }
                else
                {
                    TraceEventProviderOptions options = new()
                    {
                        ProcessIDFilter = processIds.ToArray(),
                    };

                    rundown.EnableProvider(
                        ClrRundownTraceEventParser.ProviderGuid,
                        TraceEventLevel.Verbose,
                        (ulong)CaptureProviders.NamingRundownClrKeywords,
                        options);
                }

                while (polls < RundownMaxPolls)
                {
                    polls++;
                    Thread.Sleep(s_rundownPollInterval);
                    rundown.Flush();
                    long length = new FileInfo(rundownPath).Length;
                    if (length == previousLength)
                    {
                        quiet = true;
                        break;
                    }

                    previousLength = length;
                }

                rundown.Stop(noThrow: false);
                using ETWTraceEventSource rundownSource = new(rundownPath);
                eventsLost = rundownSource.EventsLost;
                rundownSize = new FileInfo(rundownPath).Length;
            }

            if (!quiet)
            {
                double timeoutSeconds = RundownMaxPolls * s_rundownPollInterval.TotalSeconds;
                throw new InvalidOperationException($"CLR rundown did not become quiet within {timeoutSeconds:0} seconds.");
            }

            TraceEventSession.Merge(
                [outputPath, rundownPath],
                mergedPath,
                TraceEventMergeOptions.None);

            File.Replace(mergedPath, outputPath, destinationBackupFileName: null);
            stopwatch.Stop();
            return new RundownCaptureInfo(
                rundownSize,
                eventsLost,
                polls,
                stopwatch.Elapsed.TotalMilliseconds)
            {
                ProcessIds = processIds,
            };
        }
        finally
        {
            File.Delete(rundownPath);
            File.Delete(mergedPath);
        }
    }

}
