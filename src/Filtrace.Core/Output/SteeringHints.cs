// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Output;

/// <summary>
///  The steering-hint taxonomy: the canonical next-step nudges a verb attaches to
///  its <see cref="AnalysisResult{T}"/> so an agent mid-investigation is pointed
///  at the smallest useful follow-up rather than left to guess.
/// </summary>
/// <remarks>
///  <para>
///   The output contract reserves a hints channel; this is what fills it for the
///   ranking-family verbs. Each helper turns a verb's result into the one drill
///   that most naturally continues the investigation: an unwindowed CPU ranking
///   points at the hottest frame's callers, a non-CPU or windowed ranking stays in
///   the same metric/scope, a callers report points further up the stack, and a diff
///   points at the frame that changed most. The nudges name the engine verb and the
///   frame to pass it, matching the hint pinned by the output-contract golden.
///  </para>
///  <para>
///   The hints are advisory text, not commands; the CLI and MCP heads render them
///   verbatim. When a result is empty the nudge steers toward widening the scope
///   instead of drilling, because there is nothing to drill into.
///  </para>
/// </remarks>
public static class SteeringHints
{
    /// <summary>
    ///  The root pseudo-frame, whose presence as the dominant caller means the
    ///  focus frame is a top-level entry point.
    /// </summary>
    private const string RootFrame = "<root>";

    /// <summary>
    ///  The self-time pseudo-callee in a caller/callee view - the focus frame's own
    ///  execution, not a frame to drill into.
    /// </summary>
    private const string SelfFrame = "<self>";

    /// <summary>
    ///  The nudge emitted when a verb's scope contains no frames to drill into.
    /// </summary>
    private const string EmptyScope = "no frames in scope; widen the filter or check symbol resolution";

    /// <summary>
    ///  The next-step hints for a trace-info orientation: distinguish format support
    ///  from known capture enablement, and route symptoms only to analyses whose source
    ///  events were observed or whose recorder metadata establishes enablement.
    /// </summary>
    /// <param name="info">The trace info the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForTraceInfo(TraceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        bool hasAvailability = info.Analyses.Count > 0;
        HashSet<string> formatSupported = new(info.AvailableAnalyses, StringComparer.Ordinal);
        HashSet<string> analyses = !hasAvailability
            ? formatSupported
            : new HashSet<string>(
                info.Analyses
                    .Where(static pair => pair.Value is { FormatSupported: true, CaptureStatus: CaptureStatus.Enabled })
                    .Select(static pair => pair.Key),
                StringComparer.Ordinal);

        // Loader-produced objects route only to analyses known enabled. The fallback
        // keeps manually constructed legacy TraceInfo objects useful, but labels those
        // routes as format-supported because they carry no capture evidence.
        List<string> routes = [];
        if (analyses.Contains("cpu")) { routes.Add("CPU-bound -> cpu"); }

        List<string> blocked = [];
        if (analyses.Contains("contention")) { blocked.Add("contention"); }
        if (analyses.Contains("wait")) { blocked.Add("wait"); }
        if (analyses.Contains("threadpool")) { blocked.Add("threadpool"); }
        if (analyses.Contains("threadtime")) { blocked.Add("threadtime"); }
        if (blocked.Count > 0)
        {
            routes.Add($"slow but low CPU / does not scale -> {string.Join(", ", blocked)}");
        }

        List<string> memory = [];
        if (analyses.Contains("alloc")) { memory.Add("alloc"); }
        if (analyses.Contains("gcstats")) { memory.Add("gcstats"); }
        if (memory.Count > 0)
        {
            routes.Add($"high allocation rate or GC pauses -> {string.Join(", ", memory)}");
        }

        if (analyses.Contains("diskio"))
        {
            routes.Add("waiting on disk / heavy file I/O -> diskio");
        }

        if (analyses.Contains("exceptions"))
        {
            routes.Add("frequent exceptions -> exceptions");
        }

        if (analyses.Contains("activity"))
        {
            routes.Add("one request / endpoint / job is slow -> activity");
        }

        List<string> hints = [];

        if (routes.Count > 0)
        {
            string evidence = hasAvailability ? "known-enabled" : "format-supported";
            hints.Add($"{evidence} symptom routes - {string.Join("; ", routes)}");
        }

        if (hasAvailability)
        {
            string[] unknown =
            [
                .. info.Analyses
                    .Where(static pair => pair.Value is { FormatSupported: true, CaptureStatus: CaptureStatus.Unknown })
                    .Select(static pair => pair.Key)
            ];
            if (unknown.Length > 0)
            {
                hints.Add(
                    $"capture status unknown for: {string.Join(", ", unknown)}; "
                    + "absence of events is not proof the provider was disabled");
            }
        }

        if (info.SourceResolution is SourceResolutionInfo
            {
                SampledManagedFrameCount: > 0,
                SourceResolutionRate: < SymbolGate.MinimumResolutionRate
            } source)
        {
            string affected = source.HighestUnmappedModules.Count == 0
                ? "sampled managed modules"
                : string.Join(", ", source.HighestUnmappedModules.Take(3));
            hints.Add($"method-name resolution ({info.SymbolResolutionRate:P0}) is separate from source mapping ({source.SourceResolutionRate:P0}); affected: {affected}; source lines require exact matching PDBs - retry with --symbols pointing at the recorded build output (for BenchmarkDotNet, the generated child output)");
        }

        return hints;
    }

    /// <summary>
    ///  The next-step hints for a self-time or inclusive-time ranking: drill into
    ///  the hottest frame's callers.
    /// </summary>
    /// <param name="ranking">The ranking the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ranking"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForRanking(RankingResult ranking) =>
        ForRanking(ranking, MetricInfo.Cpu);

    /// <summary>
    ///  The next-step hints for a ranking, constrained to follow-ups that preserve
    ///  the ranking's metric and scope.
    /// </summary>
    /// <param name="ranking">The ranking the hints steer from.</param>
    /// <param name="metric">The metric the ranking carries.</param>
    /// <param name="scope">Optional process, activity, and time scope used to build the ranking.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">
    ///  <paramref name="ranking"/> or <paramref name="metric"/> is <see langword="null"/>.
    /// </exception>
    public static IReadOnlyList<string> ForRanking(
        RankingResult ranking,
        MetricInfo metric,
        ScopeRequest? scope = null)
    {
        ArgumentNullException.ThrowIfNull(ranking);
        ArgumentNullException.ThrowIfNull(metric);

        if (ranking.Rows.Count == 0)
        {
            return [EmptyScope];
        }

        if (metric != MetricInfo.Cpu)
        {
            return
            [
                $"refine the {metric.Name} ranking with self/inclusive measure, root, or time; callers, lines, heatmap, and tree analyze CPU only"
            ];
        }

        if (scope?.ActivityName is not null || scope?.Window is not null)
        {
            return
            [
                "this CPU ranking is activity/time-scoped; callers, lines, heatmap, and tree cannot preserve that slice - refine it with self/inclusive measure or root in rank"
            ];
        }

        string hint = $"drill into the hot frame with: callers {ranking.Rows[0].Frame}";
        return [PreserveCpuScope(hint, ranking.RootFrame, scope)];
    }

    /// <summary>
    ///  The next-step hints for a callers report: continue up the stack toward the
    ///  dominant caller, or note that the focus frame is a top-level entry point.
    /// </summary>
    /// <param name="callers">The callers report the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callers"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForCallers(CallersResult callers) =>
        ForCallers(callers, "");

    /// <summary>
    ///  The next-step hints for a callers report, preserving its root and process
    ///  scope on every actionable follow-up.
    /// </summary>
    /// <param name="callers">The callers report the hints steer from.</param>
    /// <param name="root">The root frame the callers analysis was scoped to, or empty for none.</param>
    /// <param name="scope">Optional process scope used to build the callers report.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callers"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForCallers(
        CallersResult callers,
        string root,
        ScopeRequest? scope = null)
    {
        ArgumentNullException.ThrowIfNull(callers);

        if (callers.Callers.Count == 0)
        {
            return [EmptyScope];
        }

        List<string> hints = [];

        string topCaller = callers.Callers[0].Caller;
        if (string.Equals(topCaller, RootFrame, StringComparison.Ordinal))
        {
            hints.Add("the focus frame is called directly from the root; it is a top-level entry point");
        }
        else
        {
            hints.Add(PreserveCpuScope($"continue up the stack with: callers {topCaller}", root, scope));
        }

        // With a caller/callee view, also point down into the heaviest real callee, skipping
        // the <self> self-time pseudo-callee since it is not a frame to drill into.
        if (callers.Callees is { } callees)
        {
            foreach (CalleeRow callee in callees)
            {
                if (!string.Equals(callee.Callee, SelfFrame, StringComparison.Ordinal))
                {
                    hints.Add(PreserveCpuScope(
                        $"continue down into the callee with: callers {callee.Callee} --callees",
                        root,
                        scope));
                    break;
                }
            }
        }

        return hints;
    }

    private static string PreserveCpuScope(string hint, string root, ScopeRequest? scope)
    {
        if (!string.IsNullOrEmpty(root))
        {
            hint = $"{hint} --root {QuotePowerShellArgument(root)}";
        }

        if (scope?.ProcessName is string processName)
        {
            return $"{hint} --process {QuotePowerShellArgument(processName)}";
        }

        return scope?.IncludeAll == true ? $"{hint} --all-processes" : hint;
    }

    // Hints use PowerShell command syntax throughout the shipped Windows-first docs.
    // Single-quoted arguments preserve whitespace, double quotes, dollar signs, and
    // backticks; PowerShell represents an embedded apostrophe by doubling it.
    private static string QuotePowerShellArgument(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    ///  The next-step hints for a ranking diff: drill into the frame whose weight
    ///  changed most between the two runs.
    /// </summary>
    /// <param name="diff">The ranking diff the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForDiff(RankingDiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (diff.Rows.Count == 0)
        {
            return ["the two rankings match in scope; no frames changed"];
        }

        string top = diff.Rows[0].Frame;
        return [$"the largest change is {top}; drill into it with: callers {top}"];
    }

    /// <summary>
    ///  The next-step hints for a timeline: name the busiest window and the scoped
    ///  ranking that drills it, turning the orientation view into the next command.
    /// </summary>
    /// <param name="timeline">The timeline the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="timeline"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForTimeline(TimelineResult timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        // Prefer the CPU lane - the canonical "find the window, then rank it" loop - then
        // the other rankable lanes, and finally GC activity. A null or all-zero lane has
        // nothing to point at, so it is skipped.
        if (TryPeakBucket(timeline.Cpu, static bucket => bucket.SampleCount, out int cpuIndex))
        {
            return [DrillWindowHint("CPU", "cpu", timeline, cpuIndex)];
        }

        if (TryPeakBucket(timeline.Alloc, static bucket => bucket.Count, out int allocIndex))
        {
            return [DrillWindowHint("allocation", "alloc", timeline, allocIndex)];
        }

        if (TryPeakBucket(timeline.Exceptions, static bucket => bucket.Count, out int exceptionIndex))
        {
            return [DrillWindowHint("exception", "exceptions", timeline, exceptionIndex)];
        }

        if (TryPeakBucket(timeline.Gc, static bucket => bucket.Count, out int gcIndex))
        {
            (double start, double end) = WindowOf(timeline, gcIndex);
            return [$"busiest GC window is bucket {gcIndex} ({FormatMs(start)}-{FormatMs(end)} ms); inspect collections with: gcstats"];
        }

        return ["the timeline is empty in every requested lane; widen the window or check the capture carries these events"];
    }

    // The index of the highest-weight bucket in a lane, or false when the lane is absent
    // or every bucket is empty.
    private static bool TryPeakBucket<T>(IReadOnlyList<T>? lane, Func<T, long> weight, out int index)
    {
        index = -1;
        if (lane is null)
        {
            return false;
        }

        long best = 0;
        for (int i = 0; i < lane.Count; i++)
        {
            long value = weight(lane[i]);
            if (value > best)
            {
                best = value;
                index = i;
            }
        }

        return index >= 0;
    }

    // The [start, end] millisecond bounds of a bucket. Kept as doubles so a sub-millisecond
    // bucket (a short capture divided into many buckets) is not rounded to a degenerate or
    // shifted window in the drill command.
    private static (double Start, double End) WindowOf(TimelineResult timeline, int index)
    {
        double start = timeline.FromMs + (index * timeline.BucketSizeMs);
        double end = timeline.FromMs + ((index + 1) * timeline.BucketSizeMs);
        return (start, end);
    }

    // Formats a millisecond bound for a hint: invariant culture (the form the --time parser
    // reads back) with trailing zeros trimmed, so a whole-millisecond bound stays "60" while a
    // sub-millisecond bound keeps its precision.
    private static string FormatMs(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // The drill hint for a rankable lane: name the busy window and the scoped ranking
    // that continues the investigation into it, carrying the timeline's process scope so
    // the follow-up ranking stays on the same process tree the timeline was read from.
    private static string DrillWindowHint(string laneLabel, string metric, TimelineResult timeline, int index)
    {
        (double start, double end) = WindowOf(timeline, index);
        string from = FormatMs(start);
        string to = FormatMs(end);
        return $"busiest {laneLabel} window is bucket {index} ({from}-{to} ms); "
            + $"scope a ranking with: rank --metric {metric} --time {from},{to}{ProcessScope(timeline)}";
    }

    // The " --process <name>" suffix a scoped timeline's drill hint carries so the
    // follow-up ranking stays on the same process tree, or empty when the timeline
    // spanned every process. A name with whitespace is quoted so it survives as one
    // argument.
    private static string ProcessScope(TimelineResult timeline)
    {
        if (timeline.Process is not { Length: > 0 } process)
        {
            return string.Empty;
        }

        return process.Contains(' ') || process.Contains('\t')
            ? $" --process \"{process}\""
            : $" --process {process}";
    }
}
