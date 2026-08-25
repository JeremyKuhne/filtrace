// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing.Readers;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using TraceProcess = Microsoft.Diagnostics.Tracing.Analysis.TraceProcess;

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>The default half-window on either side of a snapshot center, in milliseconds.</summary>
    public const double DefaultSnapshotHalfWindowMs = 100.0;

    /// <summary>The smallest half-window accepted for a snapshot, in milliseconds.</summary>
    public const double MinSnapshotHalfWindowMs = 0.01;

    /// <summary>The largest half-window accepted for a snapshot, in milliseconds.</summary>
    public const double MaxSnapshotHalfWindowMs = 60_000.0;

    /// <summary>The maximum rows retained for each snapshot evidence family.</summary>
    public const int SnapshotDetailLimit = 5;

    /// <summary>The maximum characters retained from one trace-derived snapshot name.</summary>
    public const int MaxSnapshotNameChars = 256;

    /// <summary>
    ///  Reads bounded cross-lane evidence around one timestamp from a single scoped
    ///  pass over a <c>.nettrace</c> or <c>.etl</c> trace.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> or <c>.etl</c> file path.</param>
    /// <param name="atMs">Center timestamp, in milliseconds from trace start.</param>
    /// <param name="halfWindowMs">
    ///  Milliseconds retained on either side of <paramref name="atMs"/>; must be from
    ///  <see cref="MinSnapshotHalfWindowMs"/> through <see cref="MaxSnapshotHalfWindowMs"/>.
    /// </param>
    /// <param name="scope">The process scope; <see langword="null"/> applies the automatic default.</param>
    /// <returns>A one-window timeline carrying a bounded snapshot.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///  A timestamp is non-finite, negative, or outside the trace, or the half-window
    ///  is non-finite or outside the supported minimum/maximum range.
    /// </exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public TimelineResult ReadSnapshot(
        string path,
        double atMs,
        double halfWindowMs = DefaultSnapshotHalfWindowMs,
        ScopeRequest? scope = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!double.IsFinite(atMs) || atMs < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(atMs), atMs, "Snapshot center must be a finite, non-negative timestamp.");
        }

        if (!double.IsFinite(halfWindowMs)
            || halfWindowMs < MinSnapshotHalfWindowMs
            || halfWindowMs > MaxSnapshotHalfWindowMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(halfWindowMs),
                halfWindowMs,
                $"Snapshot half-window must be finite and from {MinSnapshotHalfWindowMs:N2} through {MaxSnapshotHalfWindowMs:N0} ms.");
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

        double startMs = Math.Max(0.0, atMs - halfWindowMs);
        double endMs = Math.Min(traceEnd, atMs + halfWindowMs);

        ScopeResolution resolved = ProcessTree.ResolveScope(traceLog, scope ?? ScopeRequest.Auto);
        HashSet<int>? scopePids = resolved.ProcessIds;

        long eventCount = 0;
        long cpuSampleCount = 0;
        long exceptionCount = 0;
        long allocationTickCount = 0;
        long allocationBytes = 0;
        long jitCompilationCount = 0;
        Dictionary<string, long> cpuMethods = new(StringComparer.Ordinal);
        Dictionary<string, long> exceptionTypes = new(StringComparer.Ordinal);
        Dictionary<string, (long Count, long Bytes)> allocationTypes = new(StringComparer.Ordinal);
        Dictionary<string, long> jitMethods = new(StringComparer.Ordinal);
        Dictionary<(string Provider, string Name), long> eventTypes = [];
        Dictionary<(int ProcessId, int ThreadId), double> pauseStarts = [];
        List<GcPauseInterval> pauseIntervals = [];
        bool namesTruncated = false;

        using Etlx.TraceLogEventSource source = traceLog.Events.GetSource();
        source.NeedLoadedDotNetRuntimes();
        source.AllEvents += Accumulate;
        source.Process();

        SnapshotGcSummary gc = BuildSnapshotGc(
            source,
            scopePids,
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
            namesTruncated);

        return new TimelineResult(
            startMs,
            endMs,
            endMs - startMs,
            1,
            resolved.Label,
            null,
            null,
            null,
            null,
            null)
        {
            Mode = "snapshot",
            Snapshot = snapshot
        };

        void Accumulate(TraceEvent data)
        {
            double timestamp = data.TimeStampRelativeMSec;
            if (scopePids is not null && !scopePids.Contains(data.ProcessID))
            {
                return;
            }

            (int ProcessId, int ThreadId) pauseKey = (data.ProcessID, data.ThreadID);
            if (data is GCSuspendEETraceData suspend
                && suspend.Reason is GCSuspendEEReason.SuspendForGC or GCSuspendEEReason.SuspendForGCPrep)
            {
                pauseStarts[pauseKey] = timestamp;
            }
            else if (data.EventName.EndsWith("RestartEEStop", StringComparison.Ordinal)
                && pauseStarts.Remove(pauseKey, out double pauseStart)
                && timestamp >= pauseStart
                && timestamp >= startMs
                && pauseStart <= endMs)
            {
                pauseIntervals.Add(new GcPauseInterval(data.ProcessID, pauseStart, timestamp));
            }

            if (timestamp < startMs || timestamp > endMs)
            {
                return;
            }

            eventCount++;
            Tally(eventTypes, (data.ProviderName, data.EventName));

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
                    string? method = LeafMethod(stack);
                    if (method is not null)
                    {
                        Tally(cpuMethods, method);
                    }

                    break;
                }

                case ExceptionTraceData exception:
                    exceptionCount++;
                    Tally(
                        exceptionTypes,
                        string.IsNullOrEmpty(exception.ExceptionType) ? "(unknown exception type)" : exception.ExceptionType);
                    break;

                case GCAllocationTickTraceData allocation when allocation.AllocationAmount64 > 0:
                {
                    long bytes = allocation.AllocationAmount64;
                    string type = string.IsNullOrEmpty(allocation.TypeName) ? "(unknown allocation type)" : allocation.TypeName;
                    allocationTickCount++;
                    allocationBytes += bytes;
                    allocationTypes.TryGetValue(type, out (long Count, long Bytes) current);
                    allocationTypes[type] = (current.Count + 1, current.Bytes + bytes);
                    break;
                }

                case MethodJittingStartedTraceData jit:
                    jitCompilationCount++;
                    Tally(jitMethods, JitMethodName(jit));
                    break;
            }
        }
    }

    private static SnapshotGcSummary BuildSnapshotGc(
        Etlx.TraceLogEventSource source,
        HashSet<int>? scopePids,
        IReadOnlyList<GcPauseInterval> pauseIntervals,
        double startMs,
        double endMs,
        out bool namesTruncated)
    {
        int collectionCount = 0;
        List<SnapshotGcRecord> longest = [];
        double totalPauseMs = pauseIntervals.Sum(interval => interval.OverlapMs(startMs, endMs));
        double maxPauseMs = pauseIntervals.Count == 0
            ? 0.0
            : pauseIntervals.Max(interval => interval.OverlapMs(startMs, endMs));
        namesTruncated = false;

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
                bool startsInWindow = collection.StartRelativeMSec >= startMs && collection.StartRelativeMSec <= endMs;
                bool pauseOverlaps = pauseIntervals.Any(interval =>
                    interval.ProcessId == process.ProcessID
                    && interval.OverlapMs(startMs, endMs) > 0.0
                    && PauseBelongsToCollection(interval, collection));
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
            totalPauseMs,
            maxPauseMs,
            top);
    }

    private static bool PauseBelongsToCollection(GcPauseInterval interval, TraceGC collection)
    {
        if (interval.Contains(collection.StartRelativeMSec))
        {
            return true;
        }

        double collectionEndMs = collection.StartRelativeMSec + collection.DurationMSec;
        return collection.Type == GCType.BackgroundGC && interval.Contains(collectionEndMs);
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
        truncated = value.Length > MaxSnapshotNameChars;
        return truncated ? $"{value[..(MaxSnapshotNameChars - 3)]}..." : value;
    }

    private static string JitMethodName(MethodJittingStartedTraceData jit)
    {
        string method = string.IsNullOrEmpty(jit.MethodName) ? "(unknown method)" : jit.MethodName;
        return string.IsNullOrEmpty(jit.MethodNamespace) ? method : $"{jit.MethodNamespace}.{method}";
    }

    private static void Tally<TKey>(Dictionary<TKey, long> counts, TKey key) where TKey : notnull
    {
        counts.TryGetValue(key, out long current);
        counts[key] = current + 1;
    }

    private readonly record struct GcPauseInterval(int ProcessId, double StartMs, double EndMs)
    {
        public bool Contains(double timestampMs) => timestampMs >= StartMs && timestampMs <= EndMs;

        public double OverlapMs(double windowStartMs, double windowEndMs) =>
            Math.Max(0.0, Math.Min(EndMs, windowEndMs) - Math.Max(StartMs, windowStartMs));
    }
}