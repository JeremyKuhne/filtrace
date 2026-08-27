// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Security.Cryptography;
using Filtrace.Output;
using Filtrace.Tracing.Readers;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using TraceProcess = Microsoft.Diagnostics.Tracing.Analysis.TraceProcess;

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  The default half-window on either side of a snapshot center, in milliseconds.
    /// </summary>
    public const double DefaultSnapshotHalfWindowMs = 100.0;

    /// <summary>
    ///  The smallest half-window accepted for a snapshot, in milliseconds.
    /// </summary>
    public const double MinSnapshotHalfWindowMs = 0.01;

    /// <summary>
    ///  The largest half-window accepted for a snapshot, in milliseconds.
    /// </summary>
    public const double MaxSnapshotHalfWindowMs = 60_000.0;

    /// <summary>
    ///  The maximum rows retained for each snapshot evidence family.
    /// </summary>
    public const int SnapshotDetailLimit = 5;

    /// <summary>
    ///  The maximum characters retained from one trace-derived snapshot name.
    /// </summary>
    public const int MaxSnapshotNameChars = 256;

    private const int SnapshotNameHashCharacters = 32;

    /// <summary>
    ///  The maximum distinct keys retained by each snapshot evidence family.
    /// </summary>
    public const int MaxSnapshotRetainedKeysPerFamily = 1_024;

    /// <summary>
    ///  Reads bounded cross-lane evidence around one timestamp from a single scoped
    ///  pass over a <c>.nettrace</c> or <c>.etl</c> trace.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> or <c>.etl</c> file path.</param>
    /// <param name="atMs">Center timestamp, in 0.01 millisecond increments from trace start.</param>
    /// <param name="halfWindowMs">
    ///  Milliseconds retained on either side of <paramref name="atMs"/>; must be in
    ///  0.01 millisecond increments from <see cref="MinSnapshotHalfWindowMs"/> through
    ///  <see cref="MaxSnapshotHalfWindowMs"/>.
    /// </param>
    /// <param name="scope">The process scope; <see langword="null"/> applies the automatic default.</param>
    /// <returns>A one-window timeline carrying a bounded snapshot.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///  A timestamp is non-finite, negative, outside the trace, or not representable at
    ///  the wire format's precision, or the half-window is non-finite, outside the
    ///  supported minimum/maximum range, or not representable at that precision.
    /// </exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public TimelineResult ReadSnapshot(
        string path,
        double atMs,
        double halfWindowMs = DefaultSnapshotHalfWindowMs,
        ScopeRequest? scope = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (atMs < 0.0 || !IsSnapshotGeometryRepresentable(atMs))
        {
            throw new ArgumentOutOfRangeException(
                nameof(atMs),
                atMs,
                "Snapshot center must be a finite, non-negative timestamp in 0.01 millisecond increments.");
        }

        if (halfWindowMs < MinSnapshotHalfWindowMs
            || halfWindowMs > MaxSnapshotHalfWindowMs
            || !IsSnapshotGeometryRepresentable(halfWindowMs))
        {
            throw new ArgumentOutOfRangeException(
                nameof(halfWindowMs),
                halfWindowMs,
                $"Snapshot half-window must be finite, in 0.01 millisecond increments, and from "
                    + $"{MinSnapshotHalfWindowMs:N2} through {MaxSnapshotHalfWindowMs:N0} ms.");
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        using Etlx.TraceLog traceLog = OpenTrace(fullPath);
        double traceEnd = traceLog.SessionDuration.TotalMilliseconds;
        if (atMs > traceEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(atMs), atMs, $"Snapshot center exceeds the {traceEnd:N2} ms trace duration.");
        }

        (double startMs, double endMs) = ResolveSnapshotBounds(atMs, halfWindowMs, traceEnd);

        ScopeResolution resolved = ProcessTree.ResolveScope(traceLog, scope ?? ScopeRequest.Auto);
        HashSet<int>? scopedAnalysisProcessIndexes = resolved.ProcessInstanceIndexes is null ? null : [];
        bool namesTruncated = resolved.ProcessNameBounded;
        string? appliedProcessName = resolved.Label;
        if (appliedProcessName is not null)
        {
            appliedProcessName = BoundSnapshotName(appliedProcessName, out bool processNameTruncated);
            namesTruncated |= processNameTruncated;
        }

        long eventCount = 0;
        long cpuSampleCount = 0;
        long exceptionCount = 0;
        long allocationTickCount = 0;
        long allocationBytes = 0;
        long jitCompilationCount = 0;
        Dictionary<string, long> cpuMethods = new(StringComparer.Ordinal);
        Dictionary<CodeAddressIndex, (string Name, bool Truncated)> cpuMethodCache = [];
        Dictionary<string, long> exceptionTypes = new(StringComparer.Ordinal);
        Dictionary<string, (long Count, long Bytes)> allocationTypes = new(StringComparer.Ordinal);
        Dictionary<string, long> jitMethods = new(StringComparer.Ordinal);
        Dictionary<(string Provider, string Name), long> eventTypes = [];
        Dictionary<TraceEvent, (string Provider, string Name, bool Truncated)> eventNameCache =
            new(ReferenceEqualityComparer.Instance);
        Dictionary<PauseIdentity, PendingPauseStart> pauseStarts = [];
        List<GcPauseInterval> pauseIntervals = [];
        bool detailTruncated = false;
        bool gcPauseDataIncomplete = false;
        bool unknownPauseDataIncomplete = false;

        using Etlx.TraceLogEventSource source = traceLog.Events.GetSource();
        source.NeedLoadedDotNetRuntimes();
        source.AllEvents += Accumulate;
        source.Process();
        gcPauseDataIncomplete |= pauseStarts.Values.Any(static start => start.IsGc);

        SnapshotGcSummary gc = BuildSnapshotGc(
            source,
            scopedAnalysisProcessIndexes,
            pauseIntervals,
            startMs,
            endMs,
            out bool gcNamesTruncated);
        namesTruncated |= gcNamesTruncated;
        SnapshotCpuMethod[] topCpu = [.. cpuMethods
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(SnapshotDetailLimit)
            .Select(pair =>
            {
                string name = BoundSnapshotName(pair.Key, out bool truncated);
                namesTruncated |= truncated;
                return new SnapshotCpuMethod(
                    name,
                    pair.Value,
                    cpuSampleCount > 0 ? Math.Round(100.0 * pair.Value / cpuSampleCount, 2) : 0.0);
            })];
        SnapshotCountRow[] topExceptions = TopCounts(exceptionTypes, out bool exceptionNamesTruncated);
        namesTruncated |= exceptionNamesTruncated;
        SnapshotAllocationType[] topAllocations = [.. allocationTypes
            .OrderByDescending(static pair => pair.Value.Bytes)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(SnapshotDetailLimit)
            .Select(pair =>
            {
                string name = BoundSnapshotName(pair.Key, out bool truncated);
                namesTruncated |= truncated;
                return new SnapshotAllocationType(name, pair.Value.Count, pair.Value.Bytes);
            })];
        SnapshotCountRow[] topJit = TopCounts(jitMethods, out bool jitNamesTruncated);
        namesTruncated |= jitNamesTruncated;
        SnapshotEventType[] topEvents = [.. eventTypes
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key.Provider, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Name, StringComparer.Ordinal)
            .Take(SnapshotDetailLimit)
            .Select(pair =>
            {
                string provider = BoundSnapshotName(pair.Key.Provider, out bool providerTruncated);
                string name = BoundSnapshotName(pair.Key.Name, out bool nameTruncated);
                namesTruncated |= providerTruncated || nameTruncated;
                return new SnapshotEventType(provider, name, pair.Value);
            })];

        TimelineSnapshot snapshot = new(
            atMs,
            gc,
            new SnapshotCpuSummary(cpuSampleCount, cpuMethods.Count, topCpu),
            new SnapshotExceptionSummary(exceptionCount, exceptionTypes.Count, topExceptions),
            new SnapshotAllocationSummary(allocationTickCount, allocationBytes, allocationTypes.Count, topAllocations),
            new SnapshotJitSummary(jitCompilationCount, jitMethods.Count, topJit),
            new SnapshotEventSummary(eventCount, eventTypes.Count, topEvents),
            namesTruncated)
        {
            DetailTruncated = detailTruncated,
            GcPauseDataIncomplete = gcPauseDataIncomplete,
            UnknownPauseDataIncomplete = unknownPauseDataIncomplete
        };

        return new TimelineResult(
            startMs,
            endMs,
            endMs - startMs,
            1,
            appliedProcessName,
            null,
            null,
            null,
            null,
            null)
        {
            Mode = "snapshot",
            Snapshot = snapshot,
            AppliedProcessScope = FollowUpProcessScope(resolved),
            ScopeWarnings = resolved.Warnings
        };

        void Accumulate(TraceEvent data)
        {
            double timestamp = data.TimeStampRelativeMSec;
            if (!resolved.Includes(data))
            {
                return;
            }

            if (scopedAnalysisProcessIndexes is not null
                && TraceProcessesExtensions.Process(data) is TraceProcess scopedProcess)
            {
                // Includes() admitted the exact ETLX instance; this second process model
                // owns TraceGC, so retain its corresponding index for reconstruction.
                scopedAnalysisProcessIndexes.Add((int)scopedProcess.ProcessIndex);
            }

            if (data is GCSuspendEETraceData suspend)
            {
                if (TryGetPauseIdentity(data, out PauseIdentity pauseIdentity))
                {
                    BoundedPauseStartResult addResult = AddPauseStartBounded(
                        pauseStarts,
                        pauseIdentity,
                        timestamp,
                        endMs,
                        suspend.Reason,
                        out bool gcStateIncomplete);
                    gcPauseDataIncomplete |= gcStateIncomplete;
                    if (addResult == BoundedPauseStartResult.CapacityExceeded && gcStateIncomplete)
                    {
                        detailTruncated = true;
                    }
                }
                else
                {
                    gcPauseDataIncomplete |= IsMissingPauseIdentityGcIncomplete(
                        suspend.Reason,
                        timestamp,
                        endMs);
                }
            }
            else if (IsEeRestartEvent(data))
            {
                if (!TryGetPauseIdentity(data, out PauseIdentity pauseIdentity))
                {
                    unknownPauseDataIncomplete |= IsUnknownPauseEvidence(
                        PauseRestartResult.MissingStart,
                        timestamp,
                        startMs,
                        endMs);
                }
                else
                {
                    PauseRestartResult restartResult = MatchPauseRestart(
                        pauseStarts,
                        pauseIdentity,
                        timestamp,
                        startMs,
                        endMs,
                        out PendingPauseStart pauseStart);
                    if (restartResult == PauseRestartResult.MissingStart)
                    {
                        unknownPauseDataIncomplete |= IsUnknownPauseEvidence(
                            restartResult,
                            timestamp,
                            startMs,
                            endMs);
                    }
                    else if (restartResult == PauseRestartResult.InvalidPair && pauseStart.IsGc)
                    {
                        gcPauseDataIncomplete = true;
                    }
                    else if (restartResult == PauseRestartResult.CompletedGc)
                    {
                        GcPauseInterval interval = new(
                            pauseIdentity.ProcessInstanceIndex,
                            pauseStart.TimestampMs,
                            timestamp);
                        if (pauseIntervals.Count < MaxSnapshotRetainedKeysPerFamily)
                        {
                            pauseIntervals.Add(interval);
                        }
                        else
                        {
                            detailTruncated = true;
                        }
                    }
                }
            }

            if (!IsTimelineTimestampInWindow(timestamp, startMs, endMs))
            {
                return;
            }

            eventCount++;
            if (TryGetBoundedEventNames(
                eventNameCache,
                data,
                data.ProviderName,
                data.EventName,
                out string provider,
                out string eventName,
                out bool eventNamesTruncated))
            {
                namesTruncated |= eventNamesTruncated;
                detailTruncated |= !TallyBounded(eventTypes, (provider, eventName));
            }
            else
            {
                detailTruncated = true;
            }

            switch (data)
            {
                case SampledProfileTraceData:
                case ClrThreadSampleTraceData { Type: not ClrThreadSampleType.Error }:
                    {
                        TraceCallStack? stack = data.CallStack();
                        if (stack is null)
                        {
                            break;
                        }

                        cpuSampleCount++;
                        TraceCodeAddress? leafAddress = LeafCodeAddress(stack);
                        if (leafAddress is not null
                            && TryGetBoundedCpuMethod(
                                cpuMethodCache,
                                leafAddress.CodeAddressIndex,
                                leafAddress,
                                static address => FrameNames.Short(QualifyFrame(address)),
                                out string method,
                                out bool methodTruncated))
                        {
                            namesTruncated |= methodTruncated;
                            detailTruncated |= !TallyBounded(cpuMethods, method);
                        }
                        else if (leafAddress is not null)
                        {
                            detailTruncated = true;
                        }

                        break;
                    }

                case ExceptionTraceData exception:
                    exceptionCount++;
                    string exceptionType = string.IsNullOrEmpty(exception.ExceptionType)
                        ? "(unknown exception type)"
                        : exception.ExceptionType;
                    exceptionType = BoundSnapshotName(exceptionType, out bool exceptionTruncated);
                    namesTruncated |= exceptionTruncated;
                    detailTruncated |= !TallyBounded(exceptionTypes, exceptionType);
                    break;

                case GCAllocationTickTraceData allocation when allocation.AllocationAmount64 > 0:
                    {
                        long bytes = allocation.AllocationAmount64;
                        string type = string.IsNullOrEmpty(allocation.TypeName) ? "(unknown allocation type)" : allocation.TypeName;
                        type = BoundSnapshotName(type, out bool allocationTypeTruncated);
                        namesTruncated |= allocationTypeTruncated;
                        allocationTickCount++;
                        allocationBytes = AddAllocationBytes(allocationBytes, bytes);
                        detailTruncated |= !TallyAllocationBounded(allocationTypes, type, bytes);
                        break;
                    }

                case MethodJittingStartedTraceData jit:
                    jitCompilationCount++;
                    string jitMethod = BoundSnapshotName(JitMethodName(jit), out bool jitMethodTruncated);
                    namesTruncated |= jitMethodTruncated;
                    detailTruncated |= !TallyBounded(jitMethods, jitMethod);
                    break;
            }
        }
    }

    /// <summary>
    ///  Returns whether a snapshot geometry value is finite and exactly representable
    ///  by the shared serialized-output precision.
    /// </summary>
    /// <param name="value">The trace-relative millisecond value to inspect.</param>
    /// <returns><see langword="true"/> when the value is supported; otherwise <see langword="false"/>.</returns>
    public static bool IsSnapshotGeometryRepresentable(double value) =>
        double.IsFinite(value)
        && value == Math.Round(value, OutputJson.DoublePrecision, MidpointRounding.AwayFromZero);

    internal static (double StartMs, double EndMs) ResolveSnapshotBounds(
        double atMs,
        double halfWindowMs,
        double traceEndMs)
    {
        double requestedStartMs = Math.Round(
            atMs - halfWindowMs,
            OutputJson.DoublePrecision,
            MidpointRounding.AwayFromZero);
        double requestedEndMs = Math.Round(
            atMs + halfWindowMs,
            OutputJson.DoublePrecision,
            MidpointRounding.AwayFromZero);
        double startMs = Math.Max(0.0, requestedStartMs);
        if (requestedEndMs <= traceEndMs)
        {
            return (startMs, requestedEndMs);
        }

        double scale = Math.Pow(10.0, OutputJson.DoublePrecision);
        double endMs = Math.Ceiling(traceEndMs * scale) / scale;
        return (startMs, endMs);
    }

    /// <summary>
    ///  Returns the warning for a snapshot whose bounded aggregation state dropped
    ///  detail, or <see langword="null"/> when every key and interval was retained.
    /// </summary>
    /// <param name="result">The timeline result to inspect.</param>
    /// <returns>The detail warning, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static string? GetSnapshotDetailWarning(TimelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Snapshot?.DetailTruncated == true
            ? $"Snapshot detail was truncated at the {MaxSnapshotRetainedKeysPerFamily}-key-per-family aggregation budget. "
                + "Aggregate event, CPU-sample, exception, allocation-tick/byte, and JIT-compilation totals remain "
                + "complete; retained distinct-name counts are lower bounds, retained CPU-method and raw-event-type "
                + "row counts and percentages may be undercounted, top rows may omit later keys, and GC pause/collection "
                + "detail may be incomplete."
            : null;
    }

    /// <summary>
    ///  Returns the warning for incomplete GC suspend/restart evidence, or
    ///  <see langword="null"/> when every tracked pair was complete.
    /// </summary>
    /// <param name="result">The timeline result to inspect.</param>
    /// <returns>The GC evidence warning, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static string? GetSnapshotGcPauseWarning(TimelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Snapshot?.GcPauseDataIncomplete == true
            ? "Snapshot GC pause evidence is incomplete because the trace contained unmatched, duplicate, or malformed "
                + "GC suspend/restart state; pause totals and overlap-based collection detail may be inaccurate."
            : null;
    }

    /// <summary>
    ///  Returns the warning for reasonless in-window EE restart evidence, or
    ///  <see langword="null"/> when every in-window restart had a retained start.
    /// </summary>
    /// <param name="result">The timeline result to inspect.</param>
    /// <returns>The unknown-suspension warning, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static string? GetSnapshotUnknownPauseWarning(TimelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Snapshot?.UnknownPauseDataIncomplete == true
            ? "Snapshot pause evidence is incomplete because an in-window EE restart had no retained suspension start; "
                + "its reason and pause contribution are unknown."
            : null;
    }

    internal static bool IsUnknownPauseEvidence(
        PauseRestartResult restartResult,
        double timestamp,
        double windowStartMs,
        double windowEndMs) =>
        restartResult == PauseRestartResult.MissingStart
        && timestamp >= windowStartMs
        && timestamp <= windowEndMs;

    private static SnapshotGcSummary BuildSnapshotGc(
        Etlx.TraceLogEventSource source,
        HashSet<int>? scopedProcessInstanceIndexes,
        IReadOnlyList<GcPauseInterval> pauseIntervals,
        double startMs,
        double endMs,
        out bool namesTruncated)
    {
        int collectionCount = 0;
        List<SnapshotGcRecord> longest = [];
        namesTruncated = false;
        GcPauseAggregate pauseAggregate = AggregateGcPauses(pauseIntervals, startMs, endMs);
        IReadOnlyDictionary<int, GcPauseInterval[]> intervalsByProcessInstance =
            pauseAggregate.IntervalsByProcessInstance;

        foreach (TraceProcess process in source.Processes())
        {
            if (scopedProcessInstanceIndexes is not null
                && !scopedProcessInstanceIndexes.Contains((int)process.ProcessIndex))
            {
                continue;
            }

            TraceLoadedDotNetRuntime? runtime = process.LoadedDotNetRuntime();
            if (runtime is null)
            {
                continue;
            }

            intervalsByProcessInstance.TryGetValue(
                (int)process.ProcessIndex,
                out GcPauseInterval[]? processIntervals);
            processIntervals ??= [];
            foreach (TraceGC collection in runtime.GC.GCs)
            {
                bool startsInWindow = IsTimelineTimestampInWindow(collection.StartRelativeMSec, startMs, endMs);
                bool pauseOverlaps = PauseBelongsToCollection(processIntervals, collection);
                if (!startsInWindow && !pauseOverlaps)
                {
                    continue;
                }

                collectionCount++;
                string kind = BoundSnapshotName(collection.Type.ToString(), out bool kindTruncated);
                string reason = BoundSnapshotName(collection.Reason.ToString(), out bool reasonTruncated);
                namesTruncated |= kindTruncated || reasonTruncated;
                SnapshotGcRecord record = new(
                    collection.Number,
                    Math.Round(collection.StartRelativeMSec, 2),
                    collection.Generation,
                    kind,
                    reason,
                    Math.Round(collection.PauseDurationMSec, 2));
                longest.Add(record);
                if (longest.Count > SnapshotDetailLimit)
                {
                    SnapshotGcRecord drop = longest
                        .OrderBy(static candidate => candidate.PauseMs)
                        .ThenByDescending(static candidate => candidate.Number)
                        .First();
                    longest.Remove(drop);
                }
            }
        }

        SnapshotGcRecord[] top = [.. longest
            .OrderByDescending(static collection => collection.PauseMs)
            .ThenBy(static collection => collection.Number)];

        return new SnapshotGcSummary(
            collectionCount,
            Math.Round(pauseAggregate.TotalPauseMs, 2),
            Math.Round(pauseAggregate.MaxPauseMs, 2),
            top);
    }

    internal static GcPauseAggregate AggregateGcPauses(
        IEnumerable<GcPauseInterval> intervals,
        double windowStartMs,
        double windowEndMs)
    {
        Dictionary<int, GcPauseInterval[]> intervalsByProcessInstance = intervals
            .GroupBy(static interval => interval.ProcessInstanceIndex)
            .ToDictionary(
                static group => group.Key,
                static group => MergeOverlapping(group));
        double totalPauseMs = 0.0;
        double maxPauseMs = 0.0;
        foreach (GcPauseInterval[] processIntervals in intervalsByProcessInstance.Values)
        {
            foreach (GcPauseInterval interval in processIntervals)
            {
                double overlapMs = interval.OverlapMs(windowStartMs, windowEndMs);
                totalPauseMs += overlapMs;
                maxPauseMs = Math.Max(maxPauseMs, overlapMs);
            }
        }

        return new GcPauseAggregate(intervalsByProcessInstance, totalPauseMs, maxPauseMs);
    }

    // ContainsTimestamp binary-searches a single candidate, so the intervals it reads must
    // be disjoint; concurrent suspends on two threads of one process would otherwise hide
    // the enclosing pause.
    internal static GcPauseInterval[] MergeOverlapping(IEnumerable<GcPauseInterval> intervals)
    {
        List<GcPauseInterval> merged = [];
        foreach (GcPauseInterval interval in intervals.OrderBy(static candidate => candidate.StartMs))
        {
            if (merged.Count > 0 && interval.StartMs <= merged[^1].EndMs)
            {
                if (interval.EndMs > merged[^1].EndMs)
                {
                    merged[^1] = merged[^1] with { EndMs = interval.EndMs };
                }

                continue;
            }

            merged.Add(interval);
        }

        return [.. merged];
    }

    private static bool PauseBelongsToCollection(IReadOnlyList<GcPauseInterval> intervals, TraceGC collection)
    {
        if (ContainsTimestamp(intervals, collection.StartRelativeMSec))
        {
            return true;
        }

        double collectionEndMs = collection.StartRelativeMSec + collection.DurationMSec;
        return collection.Type == GCType.BackgroundGC && ContainsTimestamp(intervals, collectionEndMs);
    }

    private static bool ContainsTimestamp(IReadOnlyList<GcPauseInterval> intervals, double timestampMs)
    {
        int low = 0;
        int high = intervals.Count - 1;
        int candidate = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (intervals[middle].StartMs <= timestampMs)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate >= 0 && intervals[candidate].Contains(timestampMs);
    }

    private static SnapshotCountRow[] TopCounts(Dictionary<string, long> counts, out bool namesTruncated)
    {
        namesTruncated = false;
        List<SnapshotCountRow> top = [];
        foreach ((string name, long count) in counts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(SnapshotDetailLimit))
        {
            string bounded = BoundSnapshotName(name, out bool truncated);
            namesTruncated |= truncated;
            top.Add(new SnapshotCountRow(bounded, count));
        }

        return [.. top];
    }

    internal static string BoundSnapshotName(string value, out bool truncated)
    {
        bool requiresEscaping = RequiresSnapshotNameEscaping(value);
        truncated = value.Length > MaxSnapshotNameChars || requiresEscaping;
        if (!truncated)
        {
            return value;
        }

        const string separator = "...#";
        int prefixLimit = MaxSnapshotNameChars - separator.Length - SnapshotNameHashCharacters;
        string prefix = SnapshotNamePrefix(value, prefixLimit);
        string hash = SnapshotNameHash(value);
        return $"{prefix}{separator}{hash}";
    }

    internal static bool TryGetBoundedEventNames<TKey>(
        Dictionary<TKey, (string Provider, string Name, bool Truncated)> cache,
        TKey metadataIdentity,
        string provider,
        string name,
        out string boundedProvider,
        out string boundedName,
        out bool truncated) where TKey : notnull
    {
        if (cache.TryGetValue(metadataIdentity, out (string Provider, string Name, bool Truncated) cached))
        {
            (boundedProvider, boundedName, truncated) = cached;
            return true;
        }

        if (cache.Count >= MaxSnapshotRetainedKeysPerFamily)
        {
            boundedProvider = "";
            boundedName = "";
            truncated = false;
            return false;
        }

        boundedProvider = BoundSnapshotName(provider, out bool providerTruncated);
        boundedName = BoundSnapshotName(name, out bool nameTruncated);
        truncated = providerTruncated || nameTruncated;
        cache.Add(metadataIdentity, (boundedProvider, boundedName, truncated));
        return true;
    }

    internal static bool TryGetBoundedCpuMethod<TKey, TState>(
        Dictionary<TKey, (string Name, bool Truncated)> cache,
        TKey codeAddressIdentity,
        TState state,
        Func<TState, string> resolveMethod,
        out string boundedMethod,
        out bool truncated) where TKey : notnull
    {
        if (cache.TryGetValue(codeAddressIdentity, out (string Name, bool Truncated) cached))
        {
            (boundedMethod, truncated) = cached;
            return true;
        }

        if (cache.Count >= MaxSnapshotRetainedKeysPerFamily)
        {
            boundedMethod = "";
            truncated = false;
            return false;
        }

        boundedMethod = BoundSnapshotName(resolveMethod(state), out truncated);
        cache.Add(codeAddressIdentity, (boundedMethod, truncated));
        return true;
    }

    private static bool RequiresSnapshotNameEscaping(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (char.IsHighSurrogate(character)
                && i + 1 < value.Length
                && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsControl(character) || char.IsSurrogate(character))
            {
                return true;
            }
        }

        return false;
    }

    private static string SnapshotNamePrefix(string value, int maxLength)
    {
        const string hex = "0123456789ABCDEF";
        Span<char> prefix = stackalloc char[maxLength];
        int written = 0;
        for (int i = 0; i < value.Length && written < prefix.Length; i++)
        {
            char character = value[i];
            if (char.IsHighSurrogate(character)
                && i + 1 < value.Length
                && char.IsLowSurrogate(value[i + 1]))
            {
                if (prefix.Length - written < 2)
                {
                    break;
                }

                prefix[written++] = character;
                prefix[written++] = value[++i];
                continue;
            }

            if (char.IsControl(character) || char.IsSurrogate(character))
            {
                if (prefix.Length - written < 6)
                {
                    break;
                }

                prefix[written++] = '\\';
                prefix[written++] = 'u';
                prefix[written++] = hex[(character >> 12) & 0xF];
                prefix[written++] = hex[(character >> 8) & 0xF];
                prefix[written++] = hex[(character >> 4) & 0xF];
                prefix[written++] = hex[character & 0xF];
                continue;
            }

            prefix[written++] = character;
        }

        return new string(prefix[..written]);
    }

    private static string SnapshotNameHash(string value)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[256];
        int offset = 0;
        while (offset < value.Length)
        {
            int characterCount = Math.Min(buffer.Length / 2, value.Length - offset);
            for (int i = 0; i < characterCount; i++)
            {
                char character = value[offset + i];
                buffer[i * 2] = (byte)character;
                buffer[(i * 2) + 1] = (byte)(character >> 8);
            }

            hash.AppendData(buffer[..(characterCount * 2)]);
            offset += characterCount;
        }

        byte[] digest = hash.GetHashAndReset();
        return Convert.ToHexString(digest.AsSpan(0, SnapshotNameHashCharacters / 2));
    }

    private static string JitMethodName(MethodJittingStartedTraceData jit)
    {
        string method = string.IsNullOrEmpty(jit.MethodName) ? "(unknown method)" : jit.MethodName;
        return string.IsNullOrEmpty(jit.MethodNamespace) ? method : $"{jit.MethodNamespace}.{method}";
    }

    internal static bool TallyBounded<TKey>(Dictionary<TKey, long> counts, TKey key) where TKey : notnull
    {
        if (counts.TryGetValue(key, out long current))
        {
            counts[key] = current + 1;
            return true;
        }

        if (counts.Count >= MaxSnapshotRetainedKeysPerFamily)
        {
            return false;
        }

        counts.Add(key, 1);
        return true;
    }

    internal static bool TallyAllocationBounded(
        Dictionary<string, (long Count, long Bytes)> allocations,
        string type,
        long bytes)
    {
        if (allocations.TryGetValue(type, out (long Count, long Bytes) current))
        {
            allocations[type] = (current.Count + 1, AddAllocationBytes(current.Bytes, bytes));
            return true;
        }

        if (allocations.Count >= MaxSnapshotRetainedKeysPerFamily)
        {
            return false;
        }

        allocations.Add(type, (1, bytes));
        return true;
    }

    internal static long AddAllocationBytes(long current, long bytes)
    {
        try
        {
            return checked(current + bytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Snapshot allocation bytes exceed the supported 64-bit total; the trace is malformed.",
                exception);
        }
    }

    internal static BoundedPauseStartResult AddPauseStartBounded(
        Dictionary<PauseIdentity, PendingPauseStart> pauseStarts,
        PauseIdentity key,
        double timestamp,
        double windowEndMs,
        GCSuspendEEReason reason,
        out bool gcStateIncomplete)
    {
        gcStateIncomplete = false;
        bool isGc = IsGcPauseReason(reason);
        if (!double.IsFinite(timestamp))
        {
            gcStateIncomplete = isGc;
            return BoundedPauseStartResult.InvalidTimestamp;
        }

        if (timestamp > windowEndMs)
        {
            return BoundedPauseStartResult.AfterWindow;
        }

        if (pauseStarts.TryGetValue(key, out PendingPauseStart existing))
        {
            gcStateIncomplete = existing.IsGc || isGc;
            return BoundedPauseStartResult.Duplicate;
        }

        if (pauseStarts.Count >= MaxSnapshotRetainedKeysPerFamily)
        {
            // Only dropped GC state can make this report's pause evidence incomplete.
            gcStateIncomplete = isGc;
            return BoundedPauseStartResult.CapacityExceeded;
        }

        pauseStarts.Add(key, new PendingPauseStart(timestamp, isGc));
        return BoundedPauseStartResult.Added;
    }

    internal static PauseRestartResult MatchPauseRestart(
        Dictionary<PauseIdentity, PendingPauseStart> pauseStarts,
        PauseIdentity key,
        double timestamp,
        double windowStartMs,
        double windowEndMs,
        out PendingPauseStart pauseStart)
    {
        if (!double.IsFinite(timestamp))
        {
            pauseStarts.TryGetValue(key, out pauseStart);
            return PauseRestartResult.InvalidPair;
        }

        if (!pauseStarts.TryGetValue(key, out pauseStart))
        {
            return PauseRestartResult.MissingStart;
        }

        if (!double.IsFinite(pauseStart.TimestampMs) || timestamp < pauseStart.TimestampMs)
        {
            return PauseRestartResult.InvalidPair;
        }

        pauseStarts.Remove(key);
        if (!pauseStart.IsGc)
        {
            return PauseRestartResult.CompletedNonGc;
        }

        return timestamp >= windowStartMs && pauseStart.TimestampMs <= windowEndMs
            ? PauseRestartResult.CompletedGc
            : PauseRestartResult.OutsideWindow;
    }

    private static bool IsEeRestartEvent(TraceEvent data) =>
        IsEeRestartEventIdentity(
            data is GCNoUserDataTraceData,
            data.ProviderGuid,
            data.EventName);

    internal static bool IsEeRestartEventIdentity(bool expectedType, Guid providerGuid, string eventName) =>
        expectedType
        && providerGuid == ClrTraceEventParser.ProviderGuid
        && string.Equals(eventName, "GC/RestartEEStop", StringComparison.Ordinal);

    private static bool TryGetPauseIdentity(TraceEvent data, out PauseIdentity identity)
    {
        // The analysis ProcessIndex matches the process model that owns TraceGC;
        // ETLX ThreadIndex distinguishes OS thread-id reuse within that instance.
        TraceProcess? process = TraceProcessesExtensions.Process(data);
        Etlx.TraceThread? thread = data.Thread();
        if (process is null || thread is null)
        {
            identity = default;
            return false;
        }

        identity = new PauseIdentity((int)process.ProcessIndex, (int)thread.ThreadIndex);
        return true;
    }

    private static bool IsGcPauseReason(GCSuspendEEReason reason) =>
        reason is GCSuspendEEReason.SuspendForGC or GCSuspendEEReason.SuspendForGCPrep;

    internal static bool IsMissingPauseIdentityGcIncomplete(
        GCSuspendEEReason reason,
        double timestamp,
        double windowEndMs) =>
        IsGcPauseReason(reason)
        && (!double.IsFinite(timestamp) || timestamp <= windowEndMs);

    private static AppliedProcessScope? FollowUpProcessScope(ScopeResolution resolved) =>
        resolved.AppliedScope.Mode == "automatic" && resolved.Label is null
            ? null
            : resolved.AppliedScope;

    internal readonly record struct PauseIdentity(int ProcessInstanceIndex, int ThreadInstanceIndex);

    internal readonly record struct GcPauseInterval(int ProcessInstanceIndex, double StartMs, double EndMs)
    {
        public bool Contains(double timestampMs) => timestampMs >= StartMs && timestampMs <= EndMs;

        public double OverlapMs(double windowStartMs, double windowEndMs) =>
            Math.Max(0.0, Math.Min(EndMs, windowEndMs) - Math.Max(StartMs, windowStartMs));
    }

    internal readonly record struct PendingPauseStart(double TimestampMs, bool IsGc);

    internal sealed record GcPauseAggregate(
        IReadOnlyDictionary<int, GcPauseInterval[]> IntervalsByProcessInstance,
        double TotalPauseMs,
        double MaxPauseMs);

    /// <summary>
    ///  The outcome of adding one pending GC pause start to bounded state.
    /// </summary>
    internal enum BoundedPauseStartResult
    {
        /// <summary>
        ///  The start was retained.
        /// </summary>
        Added,

        /// <summary>
        ///  The same process/thread already had a pending start.
        /// </summary>
        Duplicate,

        /// <summary>
        ///  The pending-start budget was full.
        /// </summary>
        CapacityExceeded,

        /// <summary>
        ///  The start occurred after the selected window.
        /// </summary>
        AfterWindow,

        /// <summary>
        ///  The start carried a non-finite timestamp and was not retained.
        /// </summary>
        InvalidTimestamp
    }

    /// <summary>
    ///  The outcome of matching one EE restart to pending suspension state.
    /// </summary>
    internal enum PauseRestartResult
    {
        /// <summary>
        ///  A valid GC pair overlaps the selected window.
        /// </summary>
        CompletedGc,

        /// <summary>
        ///  A valid non-GC pair was consumed without contributing pause evidence.
        /// </summary>
        CompletedNonGc,

        /// <summary>
        ///  No pending start exists, so the reason for this restart is unknown.
        /// </summary>
        MissingStart,

        /// <summary>
        ///  The retained pair contains a non-finite or non-monotonic timestamp.
        /// </summary>
        InvalidPair,

        /// <summary>
        ///  The restart cannot establish a pause overlapping the selected window.
        /// </summary>
        OutsideWindow
    }
}
