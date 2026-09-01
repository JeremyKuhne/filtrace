// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Security.Cryptography;
using Filtrace.Output;
using Filtrace.Tracing.Readers;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

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
        SnapshotGcCollector gcCollector = new(startMs, endMs);
        bool detailTruncated = false;
        bool gcPauseDataIncomplete = false;
        bool unknownPauseDataIncomplete = false;

        using Etlx.TraceLogEventSource source = traceLog.Events.GetSource();
        source.AllEvents += Accumulate;
        source.Process();
        gcPauseDataIncomplete |= pauseStarts.Values.Any(static start => start.IsGc);

        GcPauseAggregate gcPauses = AggregateGcPauses(pauseIntervals, startMs, endMs);
        SnapshotGcSummary gc = gcCollector.Build(gcPauses, out bool gcNamesTruncated);
        detailTruncated |= gcCollector.DetailTruncated;
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
                Gc: null,
                Cpu: null,
                Exceptions: null,
                Alloc: null,
                Jit: null)
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

            gcCollector.Observe(data);

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

    /// <summary>
    ///  Resolves a centered snapshot window, clamping its start to zero and rounding an overrun end up to trace precision.
    /// </summary>
    /// <param name="atMs">The snapshot center in trace-relative milliseconds.</param>
    /// <param name="halfWindowMs">The requested duration on each side of the center.</param>
    /// <param name="traceEndMs">The trace's final timestamp in milliseconds.</param>
    /// <returns>The inclusive snapshot bounds at the shared output precision.</returns>
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

    /// <summary>
    ///  Determines whether an unmatched EE restart occurs inside the requested window and therefore leaves unknown pause evidence.
    /// </summary>
    /// <param name="restartResult">The result of matching the restart to a retained suspension.</param>
    /// <param name="timestamp">The restart timestamp in trace-relative milliseconds.</param>
    /// <param name="windowStartMs">The inclusive snapshot start.</param>
    /// <param name="windowEndMs">The inclusive snapshot end.</param>
    /// <returns><see langword="true"/> only for a missing start whose restart lies within the window.</returns>
    internal static bool IsUnknownPauseEvidence(
        PauseRestartResult restartResult,
        double timestamp,
        double windowStartMs,
        double windowEndMs) =>
            restartResult == PauseRestartResult.MissingStart
                && timestamp >= windowStartMs
                && timestamp <= windowEndMs;

    /// <summary>
    ///  Merges overlapping pauses independently per process instance and computes their overlap with a snapshot window.
    /// </summary>
    /// <param name="intervals">The completed GC pause intervals to aggregate.</param>
    /// <param name="windowStartMs">The inclusive snapshot start.</param>
    /// <param name="windowEndMs">The inclusive snapshot end.</param>
    /// <returns>Merged intervals plus total and longest in-window pause durations.</returns>
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

    /// <summary>
    ///  Coalesces overlapping or touching pauses from one process into start-ordered disjoint intervals.
    /// </summary>
    /// <param name="intervals">Pause intervals belonging to the same process instance.</param>
    /// <returns>The merged intervals in ascending start order.</returns>
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

    /// <summary>
    ///  Produces a terminal-safe bounded name, preserving identity with a stable hash when text is escaped or shortened.
    /// </summary>
    /// <param name="value">The untrusted trace-derived name.</param>
    /// <param name="truncated">Whether escaping or length bounding changed the representation.</param>
    /// <returns>The original value when safe, otherwise an escaped prefix and SHA-256-derived suffix.</returns>
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

    /// <summary>
    ///  Gets or caches bounded provider and event names without exceeding the per-family identity budget.
    /// </summary>
    /// <typeparam name="TKey">The stable metadata identity type used by the trace reader.</typeparam>
    /// <param name="cache">The bounded identity-to-name cache.</param>
    /// <param name="metadataIdentity">The event metadata identity.</param>
    /// <param name="provider">The raw provider name used on a cache miss.</param>
    /// <param name="name">The raw event name used on a cache miss.</param>
    /// <param name="boundedProvider">The cached or newly bounded provider name.</param>
    /// <param name="boundedName">The cached or newly bounded event name.</param>
    /// <param name="truncated">Whether either returned name was escaped or shortened.</param>
    /// <returns>
    ///  <see langword="false"/> when a new identity would exceed the cache budget; otherwise <see langword="true"/>.
    /// </returns>
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

    /// <summary>
    ///  Resolves and caches a bounded CPU method name once per code-address identity.
    /// </summary>
    /// <typeparam name="TKey">The stable code-address identity type.</typeparam>
    /// <typeparam name="TState">The state passed to the deferred method-name resolver.</typeparam>
    /// <param name="cache">The bounded identity-to-name cache.</param>
    /// <param name="codeAddressIdentity">The code address whose method name is requested.</param>
    /// <param name="state">State required to resolve the name on a cache miss.</param>
    /// <param name="resolveMethod">The deferred method-name resolver.</param>
    /// <param name="boundedMethod">The cached or newly bounded method name.</param>
    /// <param name="truncated">Whether the returned method name was escaped or shortened.</param>
    /// <returns>
    ///  <see langword="false"/> when a new identity would exceed the cache budget; otherwise <see langword="true"/>.
    /// </returns>
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

    /// <summary>
    ///  Increments an existing key or admits a new key while the per-family identity budget has room.
    /// </summary>
    /// <typeparam name="TKey">The counted identity type.</typeparam>
    /// <param name="counts">The bounded count table.</param>
    /// <param name="key">The identity to count.</param>
    /// <returns>
    ///  <see langword="false"/> when a new key was dropped at capacity; otherwise <see langword="true"/>.
    /// </returns>
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

    /// <summary>
    ///  Adds one allocation tick to a type while bounding the number of distinct retained types.
    /// </summary>
    /// <param name="allocations">The allocation count and byte totals keyed by bounded type name.</param>
    /// <param name="type">The bounded allocation type name.</param>
    /// <param name="bytes">The positive bytes represented by this tick.</param>
    /// <returns>
    ///  <see langword="false"/> when a new type was dropped at capacity; otherwise <see langword="true"/>.
    /// </returns>
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

    /// <summary>
    ///  Adds allocation bytes with checked arithmetic so a malformed trace cannot wrap the reported total.
    /// </summary>
    /// <param name="current">The accumulated byte total.</param>
    /// <param name="bytes">The bytes to add.</param>
    /// <returns>The checked sum.</returns>
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

    /// <summary>
    ///  Retains an EE suspension start when valid, relevant, unique, and within the pause-state budget.
    /// </summary>
    /// <param name="pauseStarts">Pending suspensions keyed by process and thread instance.</param>
    /// <param name="key">The suspension's process-thread identity.</param>
    /// <param name="timestamp">The suspension timestamp in trace-relative milliseconds.</param>
    /// <param name="windowEndMs">The snapshot's inclusive upper bound.</param>
    /// <param name="reason">The runtime suspension reason used to identify GC provenance.</param>
    /// <param name="gcStateIncomplete">
    ///  Whether rejecting or replacing this start makes GC pause evidence incomplete.
    /// </param>
    /// <returns>The reason the start was retained or rejected.</returns>
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

    /// <summary>
    ///  Matches an EE restart to pending suspension state and classifies its GC relevance to the snapshot.
    /// </summary>
    /// <param name="pauseStarts">Pending suspensions keyed by process and thread instance.</param>
    /// <param name="key">The restart's process-thread identity.</param>
    /// <param name="timestamp">The restart timestamp in trace-relative milliseconds.</param>
    /// <param name="windowStartMs">The snapshot's inclusive lower bound.</param>
    /// <param name="windowEndMs">The snapshot's inclusive upper bound.</param>
    /// <param name="pauseStart">The matching pending start when present, otherwise the default value.</param>
    /// <returns>The pairing, provenance, and window-overlap result.</returns>
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

    /// <summary>
    ///  Verifies all type, provider, and name signals required to recognize an EE restart-stop event.
    /// </summary>
    /// <param name="expectedType">Whether TraceEvent supplied the expected no-payload CLR event type.</param>
    /// <param name="providerGuid">The event provider identity.</param>
    /// <param name="eventName">The TraceEvent event name.</param>
    /// <returns><see langword="true"/> only when all restart identity signals match.</returns>
    internal static bool IsEeRestartEventIdentity(bool expectedType, Guid providerGuid, string eventName) =>
        expectedType
            && providerGuid == ClrTraceEventParser.ProviderGuid
            && string.Equals(eventName, "GC/RestartEEStop", StringComparison.Ordinal);

    private static bool TryGetPauseIdentity(TraceEvent data, out PauseIdentity identity)
    {
        // ETLX process/thread indexes distinguish OS id reuse within the trace.
        Etlx.TraceProcess? process = TraceLogExtensions.Process(data);
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

    /// <summary>
    ///  Determines whether a GC suspension lacking process-thread identity can affect the requested snapshot.
    /// </summary>
    /// <param name="reason">The runtime suspension reason.</param>
    /// <param name="timestamp">The suspension timestamp, which may be malformed.</param>
    /// <param name="windowEndMs">The snapshot's inclusive upper bound.</param>
    /// <returns>
    ///  <see langword="true"/> for GC provenance at or before the window end, including invalid timestamps.
    /// </returns>
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
}
