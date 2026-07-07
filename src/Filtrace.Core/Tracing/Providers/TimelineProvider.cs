// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Filtrace.Tracing.Readers;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using TraceProcess = Microsoft.Diagnostics.Tracing.Analysis.TraceProcess;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Builds a <see cref="TimelineResult"/> from a trace: buckets the trace's duration
///  into fixed time slices and, for each requested lane, counts the GC collections,
///  CPU samples, exception throws, allocation ticks, and JIT compilations that fall
///  in each slice.
/// </summary>
/// <remarks>
///  <para>
///   This is a structured query, not a stack source, so like the GC-stats and
///   event-query providers it returns its own result. The GC lane is reconstructed
///   from the runtime's per-collection records (a separate analysis pass), while the
///   CPU, exception, allocation, and JIT lanes are counted in a single pass over the
///   raw event stream both EventPipe and ETW carry. Bucketing by each event's
///   trace-relative timestamp is the one axis every metric shares, so one geometry
///   aligns them all.
///  </para>
/// </remarks>
public sealed partial class TimelineProvider
{
    /// <summary>The garbage-collection lane name.</summary>
    public const string GcLane = "gc";

    /// <summary>The CPU-sample lane name.</summary>
    public const string CpuLane = "cpu";

    /// <summary>The exceptions lane name.</summary>
    public const string ExceptionsLane = "exceptions";

    /// <summary>The allocation lane name.</summary>
    public const string AllocLane = "alloc";

    /// <summary>The JIT-compilation lane name.</summary>
    public const string JitLane = "jit";

    /// <summary>The default number of buckets a lane is divided into.</summary>
    public const int DefaultBucketCount = 50;

    /// <summary>The smallest number of buckets a lane may be divided into.</summary>
    public const int MinBucketCount = 5;

    /// <summary>The largest number of buckets a lane may be divided into.</summary>
    public const int MaxBucketCount = 200;

    /// <summary>
    ///  The lane names understood by <see cref="Read"/>, in render order.
    /// </summary>
    public static IReadOnlyList<string> KnownLanes { get; } =
        [GcLane, CpuLane, ExceptionsLane, AllocLane, JitLane];

    /// <summary>
    ///  The lanes read when a caller does not narrow the set - every known lane.
    /// </summary>
    public static IReadOnlyList<string> DefaultLanes => KnownLanes;

    /// <summary>
    ///  Clamps a requested bucket count into the supported
    ///  <see cref="MinBucketCount"/>..<see cref="MaxBucketCount"/> range, reporting a
    ///  warning when the value was out of range so a head can surface the adjustment.
    /// </summary>
    /// <param name="requested">The bucket count the caller asked for.</param>
    /// <param name="warning">
    ///  A human-readable note when the value was clamped, or <see langword="null"/> when
    ///  it was already in range.
    /// </param>
    /// <returns>The bucket count clamped into the supported range.</returns>
    public static int ClampBucketCount(int requested, out string? warning)
    {
        if (requested < MinBucketCount)
        {
            warning = $"buckets {requested} is below the minimum {MinBucketCount}; using {MinBucketCount}.";
            return MinBucketCount;
        }

        if (requested > MaxBucketCount)
        {
            warning = $"buckets {requested} exceeds the maximum {MaxBucketCount}; using {MaxBucketCount}.";
            return MaxBucketCount;
        }

        warning = null;
        return requested;
    }

    /// <summary>
    ///  Parses and validates a comma-separated lane selector against <see cref="KnownLanes"/>.
    ///  An empty or whitespace selector - or one of only separators - resolves to
    ///  <see cref="DefaultLanes"/>; an unrecognized name fails with a message naming the
    ///  valid set rather than silently dropping the lane. Names are case-insensitive and
    ///  de-duplicated, preserving first-seen order.
    /// </summary>
    /// <param name="lanes">The raw selector, or <see langword="null"/>/empty for every lane.</param>
    /// <param name="resolved">The resolved lane names on success; an empty list on failure.</param>
    /// <param name="error">The validation error on failure, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the selector resolved; otherwise <see langword="false"/>.</returns>
    public static bool TryResolveLanes(string? lanes, out IReadOnlyList<string> resolved, out string? error)
    {
        if (string.IsNullOrWhiteSpace(lanes))
        {
            resolved = DefaultLanes;
            error = null;
            return true;
        }

        List<string> parsed = [];
        foreach (string raw in lanes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string lane = raw.ToLowerInvariant();
            if (!KnownLanes.Contains(lane))
            {
                resolved = [];
                error = $"Unknown lane '{raw}'. Valid lanes: {string.Join(", ", KnownLanes)}.";
                return false;
            }

            if (!parsed.Contains(lane))
            {
                parsed.Add(lane);
            }
        }

        // A selector of only separators (for example ",,") parses to nothing; treat it as
        // "every lane" rather than an empty timeline.
        resolved = parsed.Count > 0 ? parsed : DefaultLanes;
        error = null;
        return true;
    }

    /// <summary>
    ///  Reads the timeline from the trace at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> or <c>.etl</c> file path.</param>
    /// <param name="window">
    ///  Optional time window bounding the timeline; when a bound is open it is filled
    ///  from the trace (start of trace for the lower bound, end for the upper).
    ///  <see langword="null"/> spans the whole trace.
    /// </param>
    /// <param name="lanes">
    ///  The lanes to build (case-insensitive; unknown names are ignored). Pass
    ///  <see langword="null"/> or an empty set for <see cref="DefaultLanes"/>.
    /// </param>
    /// <param name="bucketCount">
    ///  The number of buckets each lane is divided into; clamped to
    ///  <see cref="MinBucketCount"/>..<see cref="MaxBucketCount"/>.
    /// </param>
    /// <param name="scope">
    ///  The process scope; <see langword="null"/> applies the automatic busiest-process
    ///  default. Only a multi-process <c>.etl</c> is narrowed; a single-process
    ///  <c>.nettrace</c> is a no-op.
    /// </param>
    /// <returns>The timeline, with one aligned array per requested lane.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public TimelineResult Read(
        string path,
        TimeWindow? window = null,
        IReadOnlyCollection<string>? lanes = null,
        int bucketCount = DefaultBucketCount,
        ScopeRequest? scope = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        HashSet<string> requested = lanes is null || lanes.Count == 0
            ? new(DefaultLanes, StringComparer.OrdinalIgnoreCase)
            : new(lanes, StringComparer.OrdinalIgnoreCase);

        int buckets = ClampBucketCount(bucketCount, out _);

        using Etlx.TraceLog traceLog = OpenTrace(fullPath);

        // Scope to a process tree: an explicit --process name, or the automatic busiest
        // process on a multi-process .etl. A null pid set means every process (a
        // single-process .nettrace, or the all-processes opt-out), so the lanes cover the
        // whole capture. appliedProcessName is the tree the scope resolved to, or null.
        HashSet<int>? scopePids = ProcessTree.ResolveScope(
            traceLog, scope ?? ScopeRequest.Auto, out string? appliedProcessName);

        // Resolve the window against the trace: an open bound is filled from the trace,
        // and a start at or past the end degenerates to a single bucket's width rather
        // than dividing by zero.
        double traceEnd = traceLog.SessionDuration.TotalMilliseconds;
        double startMs = window?.StartMSec ?? 0.0;
        double endMs = window?.EndMSec ?? traceEnd;
        if (endMs <= startMs)
        {
            endMs = startMs + 1.0;
        }

        double bucketSizeMs = (endMs - startMs) / buckets;

        // Every requested lane is built in a single pass over the trace: the GC lane from
        // the .NET runtime analysis and the CPU, exception, allocation, and JIT lanes from
        // the raw event stream, both driven off one Process() so a gc+cpu timeline scans
        // the trace once rather than once per mechanism.
        (IReadOnlyList<GcBucket>? gc, EventLanes eventLanes) = BuildLanes(
            traceLog, scopePids, requested, startMs, endMs, bucketSizeMs, buckets);

        return new TimelineResult(
            Math.Round(startMs, 2),
            Math.Round(endMs, 2),
            Math.Round(bucketSizeMs, 2),
            buckets,
            appliedProcessName,
            gc,
            eventLanes.Cpu,
            eventLanes.Exceptions,
            eventLanes.Alloc,
            eventLanes.Jit);
    }

    // Maps a trace-relative time to its bucket index, clamped into range so a sample
    // exactly on the upper bound lands in the last bucket rather than one past it.
    private static int BucketIndex(double timeMs, double startMs, double bucketSizeMs, int buckets) =>
        Math.Clamp((int)((timeMs - startMs) / bucketSizeMs), 0, buckets - 1);

    // Builds every requested lane in a single pass over the trace. The GC lane is
    // reconstructed from the .NET runtime analysis (NeedLoadedDotNetRuntimes) and the
    // CPU, exception, allocation, and JIT lanes from the raw event stream; registering
    // both on one source and calling Process() once keeps a gc+cpu timeline to a single
    // scan instead of one pass per mechanism.
    private static (IReadOnlyList<GcBucket>? Gc, EventLanes Events) BuildLanes(
        Etlx.TraceLog traceLog,
        HashSet<int>? scopePids,
        HashSet<string> requested,
        double startMs,
        double endMs,
        double bucketSizeMs,
        int buckets)
    {
        bool wantGc = requested.Contains(GcLane);
        bool wantCpu = requested.Contains(CpuLane);
        bool wantExceptions = requested.Contains(ExceptionsLane);
        bool wantAlloc = requested.Contains(AllocLane);
        bool wantJit = requested.Contains(JitLane);

        bool wantEvents = wantCpu || wantExceptions || wantAlloc || wantJit;
        if (!wantGc && !wantEvents)
        {
            return (null, default);
        }

        int[]? cpuCount = wantCpu ? new int[buckets] : null;
        Dictionary<string, int>[]? cpuTop = wantCpu ? NewCounters(buckets) : null;
        int[]? exCount = wantExceptions ? new int[buckets] : null;
        Dictionary<string, int>[]? exTop = wantExceptions ? NewCounters(buckets) : null;
        long[]? allocCount = wantAlloc ? new long[buckets] : null;
        long[]? allocBytes = wantAlloc ? new long[buckets] : null;
        int[]? jitCount = wantJit ? new int[buckets] : null;

        using Etlx.TraceLogEventSource source = traceLog.Events.GetSource();

        // The GC lane needs the .NET runtime analysis registered before the pass runs;
        // its per-collection records are read back from the processed source afterward.
        if (wantGc)
        {
            source.NeedLoadedDotNetRuntimes();
        }

        // The raw-event lanes are tallied as the same pass dispatches every event.
        if (wantEvents)
        {
            source.AllEvents += Accumulate;
        }

        source.Process();

        IReadOnlyList<GcBucket>? gc = wantGc
            ? BuildGcLane(source, scopePids, startMs, endMs, bucketSizeMs, buckets)
            : null;

        EventLanes events = wantEvents
            ? new EventLanes(
                wantCpu ? BuildCpuLane(cpuCount!, cpuTop!, buckets) : null,
                wantExceptions ? BuildExceptionLane(exCount!, exTop!, buckets) : null,
                wantAlloc ? BuildAllocLane(allocCount!, allocBytes!, buckets) : null,
                wantJit ? BuildJitLane(jitCount!, buckets) : null)
            : default;

        return (gc, events);

        // Buckets one raw event into whichever requested lane it belongs to, skipping
        // events outside the window or the process scope.
        void Accumulate(TraceEvent data)
        {
            double time = data.TimeStampRelativeMSec;
            if (time < startMs || time > endMs)
            {
                return;
            }

            if (scopePids is not null && !scopePids.Contains(data.ProcessID))
            {
                return;
            }

            switch (data)
            {
                case SampledProfileTraceData when wantCpu:
                case ClrThreadSampleTraceData { Type: not ClrThreadSampleType.Error } when wantCpu:
                {
                    // Match the CPU reader's sample selection so the lane's count agrees with
                    // the cpu ranking: a sample counts only if it carries a call stack, which
                    // drops the stackless idle-CPU samples the reader also excludes. EventPipe
                    // surfaces CPU samples as the SampleProfiler's ClrThreadSampleTraceData, ETW
                    // as SampledProfileTraceData.
                    TraceCallStack? stack = data.CallStack();
                    if (stack is null)
                    {
                        break;
                    }

                    int idx = BucketIndex(time, startMs, bucketSizeMs, buckets);
                    cpuCount![idx]++;
                    string? leaf = LeafMethod(stack);
                    if (leaf is not null)
                    {
                        Tally(cpuTop![idx], leaf);
                    }

                    break;
                }

                case ExceptionTraceData exception when wantExceptions:
                {
                    int idx = BucketIndex(time, startMs, bucketSizeMs, buckets);
                    exCount![idx]++;
                    string type = string.IsNullOrEmpty(exception.ExceptionType)
                        ? "(unknown exception type)"
                        : exception.ExceptionType;
                    Tally(exTop![idx], type);
                    break;
                }

                case GCAllocationTickTraceData alloc when wantAlloc:
                {
                    long bytes = alloc.AllocationAmount64;
                    if (bytes <= 0)
                    {
                        break;
                    }

                    int idx = BucketIndex(time, startMs, bucketSizeMs, buckets);
                    allocCount![idx]++;
                    allocBytes![idx] += bytes;
                    break;
                }

                case MethodJittingStartedTraceData when wantJit:
                {
                    int idx = BucketIndex(time, startMs, bucketSizeMs, buckets);
                    jitCount![idx]++;
                    break;
                }
            }
        }
    }

    // Reconstructs the GC lane from the runtime's per-collection records that the .NET
    // runtime analysis gathered during the shared pass. The source must already have
    // been processed (with NeedLoadedDotNetRuntimes registered) before this reads it.
    private static GcBucket[] BuildGcLane(
        Etlx.TraceLogEventSource source, HashSet<int>? scopePids, double startMs, double endMs, double bucketSizeMs, int buckets)
    {
        int[] count = new int[buckets];
        double[] totalPause = new double[buckets];
        double[] maxPause = new double[buckets];
        bool[] hasGen2 = new bool[buckets];

        foreach (TraceProcess process in source.Processes())
        {
            if (scopePids is not null && !scopePids.Contains(process.ProcessID))
            {
                continue;
            }

            TraceLoadedDotNetRuntime? runtime = process.LoadedDotNetRuntime();
            if (runtime is null)
            {
                continue;
            }

            foreach (TraceGC collection in runtime.GC.GCs)
            {
                double time = collection.StartRelativeMSec;
                if (time < startMs || time > endMs)
                {
                    continue;
                }

                int idx = BucketIndex(time, startMs, bucketSizeMs, buckets);
                count[idx]++;
                totalPause[idx] += collection.PauseDurationMSec;
                maxPause[idx] = Math.Max(maxPause[idx], collection.PauseDurationMSec);
                hasGen2[idx] |= collection.Generation >= 2;
            }
        }

        GcBucket[] lane = new GcBucket[buckets];
        for (int i = 0; i < buckets; i++)
        {
            lane[i] = new GcBucket(count[i], Math.Round(totalPause[i], 2), Math.Round(maxPause[i], 2), hasGen2[i]);
        }

        return lane;
    }

    private static CpuBucket[] BuildCpuLane(int[] count, Dictionary<string, int>[] top, int buckets)
    {
        CpuBucket[] lane = new CpuBucket[buckets];
        for (int i = 0; i < buckets; i++)
        {
            lane[i] = new CpuBucket(count[i], Top(top[i]));
        }

        return lane;
    }

    private static ExceptionBucket[] BuildExceptionLane(int[] count, Dictionary<string, int>[] top, int buckets)
    {
        ExceptionBucket[] lane = new ExceptionBucket[buckets];
        for (int i = 0; i < buckets; i++)
        {
            lane[i] = new ExceptionBucket(count[i], Top(top[i]));
        }

        return lane;
    }

    private static AllocBucket[] BuildAllocLane(long[] count, long[] bytes, int buckets)
    {
        AllocBucket[] lane = new AllocBucket[buckets];
        for (int i = 0; i < buckets; i++)
        {
            lane[i] = new AllocBucket(count[i], bytes[i]);
        }

        return lane;
    }

    private static JitBucket[] BuildJitLane(int[] count, int buckets)
    {
        JitBucket[] lane = new JitBucket[buckets];
        for (int i = 0; i < buckets; i++)
        {
            lane[i] = new JitBucket(count[i]);
        }

        return lane;
    }

    // Resolves the shortened innermost resolved method of a CPU sample's stack: the
    // leaf if it resolved, else the first caller up the stack that did. Null when no
    // frame resolved to a managed method (an all-native or broken stack), so an
    // unresolved leaf does not drown the top-method tally in "?".
    private static string? LeafMethod(TraceCallStack callStack)
    {
        for (TraceCallStack? frame = callStack; frame is not null; frame = frame.Caller)
        {
            if (!string.IsNullOrEmpty(frame.CodeAddress.FullMethodName))
            {
                return FrameNames.Short(QualifyFrame(frame.CodeAddress));
            }
        }

        return null;
    }

    // Builds the "module!Method(sig)" frame name FrameNames.Short expects, matching the
    // CPU reader's naming so the shortened leaf reads the same as a ranking's rows.
    private static string QualifyFrame(Etlx.TraceCodeAddress address)
    {
        string method = address.FullMethodName;
        string module = address.ModuleName;
        if (string.IsNullOrEmpty(method))
        {
            return $"{(string.IsNullOrEmpty(module) ? "?" : module)}!?";
        }

        return string.IsNullOrEmpty(module) ? method : $"{module}!{method}";
    }

    private static Dictionary<string, int>[] NewCounters(int buckets)
    {
        Dictionary<string, int>[] counters = new Dictionary<string, int>[buckets];
        for (int i = 0; i < buckets; i++)
        {
            counters[i] = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        return counters;
    }

    private static void Tally(Dictionary<string, int> counter, string key)
    {
        counter.TryGetValue(key, out int current);
        counter[key] = current + 1;
    }

    // The most frequent key in a bucket's counter, breaking ties by ordinal name so the
    // choice is deterministic across runs. Null for an empty bucket.
    private static string? Top(Dictionary<string, int> counter)
    {
        string? top = null;
        int best = 0;
        foreach ((string key, int value) in counter)
        {
            if (value > best || (value == best && top is not null && string.CompareOrdinal(key, top) < 0))
            {
                best = value;
                top = key;
            }
        }

        return top;
    }

    // Opens a trace of either supported format as a TraceLog: an ETW .etl via
    // OpenOrConvert (the ETW -> ETLX conversion is Windows-only), or an EventPipe
    // .nettrace via CreateFromEventPipeDataFile. Mirrors the event-query provider so the
    // timeline spans EventPipe and ETW alike.
    private static Etlx.TraceLog OpenTrace(string fullPath)
    {
        if (fullPath.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            return Etlx.TraceLog.OpenOrConvert(
                fullPath,
                new Etlx.TraceLogOptions { ContinueOnError = true });
        }

        string etlxPath = Etlx.TraceLog.CreateFromEventPipeDataFile(
            fullPath,
            null,
            new Etlx.TraceLogOptions { ContinueOnError = true });

        return new Etlx.TraceLog(etlxPath);
    }
}
