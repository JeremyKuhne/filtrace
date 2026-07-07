// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Filtrace.Output;
using Filtrace.Server;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Mcp;

/// <summary>
///  The curated MCP tool surface over the Filtrace analysis core: load a trace and
///  query its quality signals, the process inventory, folded self/inclusive rankings,
///  the top-down call tree, immediate callers, line-level attribution, a runtime
///  work-category breakdown, two-trace diffs, the garbage-collection and JIT reports,
///  and a raw event query across speedscope, EventPipe (<c>.nettrace</c>), and ETW
///  (<c>.etl</c>) inputs, plus export a flame graph to a file. Every tool but
///  <c>trace_export</c> is read-only; <c>trace_export</c> writes a file. Three tools -
///  <c>trace_rank</c>, <c>trace_classify</c>, and <c>trace_export</c> - reach the
///  Microsoft public symbol server and write a local symbol cache when their opt-in
///  <c>nativeSymbols</c> flag is set, so all three carry the open-world hint; with
///  the flag off (the default) they stay offline (and, but for <c>trace_export</c>,
///  read-only).
/// </summary>
/// <remarks>
///  <para>
///   Each tool returns a typed <see cref="AnalysisResult{T}"/> envelope - the same
///   shape the CLI emits - rather than a pre-serialized string, so the server can
///   advertise an <c>outputSchema</c> per tool and return both structured content
///   (the parsed object) and a text mirror. Every tool shares one shape: a schema
///   version, warnings, hints, and the typed result. The host registers the tools
///   with <see cref="OutputJson.SerializerOptions"/>, so that envelope is serialized
///   with the same deterministic naming, encoding, and double-rounding the CLI uses.
///   The single injected <see cref="TraceStore"/> caches parsed traces, so repeated
///   queries against the same path reuse one parse.
///  </para>
/// </remarks>
[McpServerToolType]
public sealed class TraceTools
{
    /// <summary>
    ///  Loads a trace and returns its format, total weight, sample count, symbol
    ///  resolution rate, per-thread sample counts, and quality warnings.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <param name="process">Optional process-name substring scoping a multi-process .etl capture to one process tree.</param>
    /// <returns>The trace summary envelope.</returns>
    [McpServerTool(Name = "trace_info", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Load a trace (.speedscope.json, .nettrace, or .etl) and return its format, total weight, sample count, "
        + "symbol-resolution rate, per-thread sample counts, the analyses the trace's format can answer "
        + "(availableAnalyses, with a hint routing each symptom to one), and quality warnings. Call this first: a "
        + "resolution rate below 0.8 means symbols are missing and the rankings should not be trusted.")]
    public static AnalysisResult<TraceInfoView> Info(
        TraceStore store,
        [Description("Path to a .speedscope.json, .nettrace, or .etl trace file.")] string path,
        [Description(
            "Optional build-output directory (e.g. artifacts/.../touki.perf/net10.0) whose assemblies' "
            + "embedded portable PDBs are extracted so managed frames resolve to source lines.")]
        string symbols = "",
        [Description(
            "Optional process-name substring scoping a multi-process .etl capture to one process tree; omit "
            + "to auto-scope to the busiest. Ignored for single-process .nettrace/speedscope traces.")]
        string process = "")
    {
        TraceInfo info = Load(store, path, NullIfEmpty(symbols), scope: ResolveScope(process)).Info;
        TraceInfoView view = new(
            info.Path,
            info.Format.ToString(),
            info.TotalWeight,
            info.SampleCount,
            info.SymbolResolutionRate,
            info.Threads,
            info.AvailableAnalyses);
        return new AnalysisResult<TraceInfoView>(view, info.Warnings, SteeringHints.ForTraceInfo(info));
    }

    /// <summary>
    ///  Ranks the hottest frames over a chosen provider metric by self or inclusive
    ///  time, folding JIT-helper sampling artifacts back into the real methods.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="metric">The provider view to rank.</param>
    /// <param name="measure">Whether to report self time or inclusive time.</param>
    /// <param name="root">Optional substring scoping the ranking to a subtree.</param>
    /// <param name="top">Maximum rows to return.</param>
    /// <param name="fold">Optional fold patterns; defaults to the built-in JIT-helper list.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <param name="process">Optional process-name substring scoping a multi-process .etl capture to one process tree.</param>
    /// <param name="activity">Optional start-stop activity task name scoping the CPU ranking to that request/job.</param>
    /// <param name="time">Optional time window 'start,end' in ms scoping the ranking to that slice; any metric.</param>
    /// <param name="nativeSymbols">Resolve native runtime frames from the public symbol server (opt-in, network); cpu/.etl only.</param>
    /// <returns>The ranking envelope.</returns>
    [McpServerTool(Name = "trace_rank", ReadOnly = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Rank the hottest frames over a chosen provider metric. measure=self credits the executing leaf "
        + "(JIT helpers folded in); measure=inclusive credits a frame and all it calls. One tool spans every "
        + "family - metric=cpu (sampled ms, any format), threadtime (wall-clock, .etl only), alloc (bytes), "
        + "exceptions (throws by type), contention (ms on locks), wait (ms on a handle), or activity (ms per "
        + "request); all but cpu and threadtime need a .nettrace. Scope with root; for a BenchmarkDotNet "
        + "capture set root to the workload to skip the harness.")]
    public static AnalysisResult<RankingResult> Rank(
        TraceStore store,
        [Description("Path to a .speedscope.json, .nettrace, or .etl trace file.")] string path,
        [Description("Provider view to rank: cpu, threadtime, alloc, exceptions, contention, wait, or activity.")] string metric = "cpu",
        [Description("Which measure to report: self or inclusive.")] string measure = "self",
        [Description("Optional substring of a frame name to scope the ranking to its subtree.")] string root = "",
        [Description("Maximum number of ranked rows to return.")] int top = 25,
        [Description("Optional regex fold patterns; omit to use the built-in JIT-helper defaults.")] string[]? fold = null,
        [Description(
            "Optional build-output directory whose assemblies' embedded portable PDBs are extracted so "
            + "managed frames resolve to source lines (cpu metric only).")]
        string symbols = "",
        [Description(
            "Optional process-name substring scoping a multi-process .etl capture to one process tree; omit "
            + "to auto-scope to the busiest.")]
        string process = "",
        [Description(
            "Optional activity task name scoping the CPU ranking to that request/job (cpu only).")]
        string activity = "",
        [Description(
            "Optional time window 'start,end' in ms scoping the ranking to that slice; either bound optional "
            + "(e.g. 1000, or ,5000). Any metric.")]
        string time = "",
        [Description(
            "Resolve native runtime frames (GC, JIT, memcpy) from the Microsoft public symbol server; opt-in "
            + "(network, cached); cpu over .etl only.")]
        bool nativeSymbols = false)
    {
        TraceMetric resolved = ResolveMetric(metric);
        bool inclusive = ResolveMeasure(measure);
        RequirePositiveTop(top);
        IReadOnlyList<string> foldPatterns = ResolveFold(fold);

        // The activity scope filters CPU samples by the request/job they were taken in, so
        // it applies to the cpu metric only; reject the combination rather than ignore it.
        if (!string.IsNullOrEmpty(activity) && resolved != TraceMetric.Cpu)
        {
            throw new McpException(
                "The activity scope applies to the cpu metric only. Use metric=cpu (or omit metric) to scope to an activity.");
        }

        ScopeRequest? scope = ResolveScope(process);
        if (!string.IsNullOrEmpty(activity))
        {
            scope = (scope ?? ScopeRequest.Auto).WithActivity(activity);
        }

        // The time window scopes every metric, so it is not gated on the metric like the
        // activity scope; a malformed value is a clean tool error, not an exception.
        if (!TimeWindow.TryParse(NullIfEmpty(time), out double? startMSec, out double? endMSec, out string? timeError))
        {
            throw new McpException(timeError);
        }

        if (startMSec is not null || endMSec is not null)
        {
            scope = (scope ?? ScopeRequest.Auto).WithTimeWindow(startMSec, endMSec);
        }

        LoadedTrace trace = Load(store, path, NullIfEmpty(symbols), resolved, scope, ResolveSymbols(nativeSymbols));
        TraceInfo info = trace.Info;
        RankingResult ranking = inclusive
            ? trace.Aggregator.InclusiveTime(root, foldPatterns, top)
            : trace.Aggregator.SelfTime(root, foldPatterns, top);

        return new AnalysisResult<RankingResult>(ranking, info.Warnings, SteeringHints.ForRanking(ranking));
    }

    /// <summary>
    ///  Reports the immediate callers of the frame matching <paramref name="frame"/>,
    ///  with the CPU time each contributes.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="frame">Substring identifying the focus frame whose callers to report.</param>
    /// <param name="root">Optional substring scoping the analysis to a subtree.</param>
    /// <param name="top">Maximum caller rows to return.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <param name="process">Optional process-name substring scoping a multi-process .etl capture to one process tree.</param>
    /// <returns>The caller-breakdown envelope.</returns>
    [McpServerTool(Name = "trace_callers", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Immediate callers of the frame matching 'frame', with the CPU time each contributes. Use it to learn "
        + "what a JIT-helper or shared-utility frame (e.g. BulkMoveWithWriteBarrier) is really attributable to, "
        + "or to walk up a hot stack one level at a time. Set callees for a caller/callee view (what it calls too). "
        + "Scope to a subtree with root.")]
    public static AnalysisResult<CallersResult> Callers(
        TraceStore store,
        [Description("Path to a .speedscope.json, .nettrace, or .etl trace file.")] string path,
        [Description("Substring identifying the focus frame whose callers to report.")] string frame,
        [Description("Optional substring of a frame name to scope the analysis to its subtree.")] string root = "",
        [Description("Maximum number of caller rows to return.")] int top = 25,
        [Description(
            "Optional build-output directory whose assemblies' embedded portable PDBs are extracted so "
            + "managed frames resolve to source lines.")]
        string symbols = "",
        [Description(
            "Optional process-name substring scoping a multi-process .etl capture to one process tree; omit "
            + "to auto-scope to the busiest. Ignored for single-process .nettrace/speedscope traces.")]
        string process = "",
        [Description("Also return the frame's immediate callees (a caller/callee view); off by default.")]
        bool callees = false)
    {
        RequirePositiveTop(top);
        LoadedTrace trace = Load(store, path, NullIfEmpty(symbols), scope: ResolveScope(process));
        TraceInfo info = trace.Info;
        CallersResult callers = trace.Aggregator.CallersOf(frame, root, top, callees);

        return new AnalysisResult<CallersResult>(callers, info.Warnings, SteeringHints.ForCallers(callers));
    }

    /// <summary>
    ///  Returns the line-level self-time ranking, attributing leaf samples to the
    ///  source line that was executing, scoped to the matching methods.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="method">Optional substring scoping to matching methods.</param>
    /// <param name="top">Maximum rows to return.</param>
    /// <param name="fold">Optional fold patterns; defaults to the built-in JIT-helper list.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <param name="process">Optional process-name substring scoping a multi-process .etl capture to one process tree.</param>
    /// <returns>The line-level self-time envelope.</returns>
    [McpServerTool(Name = "trace_lines", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Line-level self time: each leaf sample (JIT-helper leaves folded into their caller) attributed to the "
        + "source file:line that was executing, scoped to methods whose name contains 'method'. Requires a "
        + ".nettrace or .etl trace whose modules have portable PDBs; pass 'symbols' pointing at the build-output "
        + "directory. Speedscope inputs carry no line data and return an empty ranking. A '<no source>' row is "
        + "time in matching methods whose PDB was not found.")]
    public static AnalysisResult<LineRankingResult> Lines(
        TraceStore store,
        [Description("Path to a .nettrace or .etl trace file (speedscope carries no line data).")] string path,
        [Description("Optional substring of a method name to scope the ranking; omit for every method.")] string method = "",
        [Description("Maximum number of rows to return.")] int top = 25,
        [Description("Optional regex fold patterns; omit to use the built-in JIT-helper defaults.")] string[]? fold = null,
        [Description(
            "Optional build-output directory (e.g. artifacts/.../touki.perf/net10.0) whose assemblies' embedded "
            + "portable PDBs are extracted so managed frames resolve to source lines.")]
        string symbols = "",
        [Description(
            "Optional process-name substring scoping a multi-process .etl capture to one process tree; omit "
            + "to auto-scope to the busiest. Ignored for single-process .nettrace/speedscope traces.")]
        string process = "")
    {
        RequirePositiveTop(top);
        IReadOnlyList<string> foldPatterns = ResolveFold(fold);
        LoadedTrace trace = Load(store, path, NullIfEmpty(symbols), scope: ResolveScope(process));
        TraceInfo info = trace.Info;
        LineRankingResult lines = trace.Aggregator.HotLines(method, foldPatterns, top);

        return new AnalysisResult<LineRankingResult>(lines, info.Warnings);
    }

    /// <summary>
    ///  Builds a per-line self-time heat map for one source file, ordered by line
    ///  number for overlaying onto the source.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="file">Path or file name of the source file to map.</param>
    /// <param name="fold">Optional fold patterns; defaults to the built-in JIT-helper list.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <param name="process">Optional process-name substring scoping a multi-process .etl capture to one process tree.</param>
    /// <returns>The heat-map envelope.</returns>
    [McpServerTool(Name = "trace_heatmap", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Per-line self-time heat map for one source file: each leaf sample (JIT-helper leaves folded) attributed "
        + "to the executing line, ordered by line number to overlay onto the source. Matched by file name. Requires "
        + "a .nettrace or .etl trace whose modules have portable PDBs (pass 'symbols'). Speedscope returns an empty "
        + "map.")]
    public static AnalysisResult<SourceHeatmapResult> Heatmap(
        TraceStore store,
        [Description("Path to a .nettrace or .etl trace file (speedscope carries no line data).")] string path,
        [Description("Path or bare name of the source file to map, e.g. ExtGlob.cs.")] string file,
        [Description("Optional regex fold patterns; omit to use the built-in JIT-helper defaults.")] string[]? fold = null,
        [Description(
            "Optional build-output directory (e.g. artifacts/.../touki.perf/net10.0) whose assemblies' embedded "
            + "portable PDBs are extracted so managed frames resolve to source lines.")]
        string symbols = "",
        [Description(
            "Optional process-name substring scoping a multi-process .etl capture to one process tree; omit "
            + "to auto-scope to the busiest. Ignored for single-process .nettrace/speedscope traces.")]
        string process = "")
    {
        IReadOnlyList<string> foldPatterns = ResolveFold(fold);
        LoadedTrace trace = Load(store, path, NullIfEmpty(symbols), scope: ResolveScope(process));
        TraceInfo info = trace.Info;

        // The trace records the build-time file name, not its full path, so match on the file name.
        string fileName = Path.GetFileName(file);
        SourceHeatmapResult heatmap = trace.Aggregator.SourceHeatmap(fileName, foldPatterns);

        return new AnalysisResult<SourceHeatmapResult>(heatmap, info.Warnings);
    }

    /// <summary>
    ///  Compares two CPU traces and reports the per-frame change, largest absolute
    ///  change first, so an agent can see what got slower or faster between runs.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="beforePath">The baseline trace file path.</param>
    /// <param name="afterPath">The current trace file path.</param>
    /// <param name="measure">Whether to compare self time or inclusive time.</param>
    /// <param name="root">Optional substring scoping both rankings to a subtree.</param>
    /// <param name="top">Maximum changed rows to return.</param>
    /// <param name="fold">Optional fold patterns; defaults to the built-in JIT-helper list.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <returns>The diff envelope.</returns>
    [McpServerTool(Name = "trace_diff", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Compare two CPU traces and report the per-frame change (regressions and improvements), largest absolute "
        + "change first. Both sides are ranked fully (no cap) before diffing, so a frame hot on one side is not "
        + "misreported. measure=self credits the executing leaf (JIT-helper leaves folded); measure=inclusive "
        + "credits a frame and all it calls. Finds what got slower or faster between two runs.")]
    public static AnalysisResult<RankingDiffResult> Diff(
        TraceStore store,
        [Description("Path to the baseline (before) .speedscope.json, .nettrace, or .etl trace file.")] string beforePath,
        [Description("Path to the current (after) .speedscope.json, .nettrace, or .etl trace file.")] string afterPath,
        [Description("Which measure to compare: self or inclusive.")] string measure = "self",
        [Description("Optional substring of a frame name to scope both rankings to its subtree.")] string root = "",
        [Description("Maximum number of changed rows to return.")] int top = 25,
        [Description("Optional regex fold patterns; omit to use the built-in JIT-helper defaults.")] string[]? fold = null,
        [Description(
            "Optional build-output directory whose assemblies' embedded portable PDBs are extracted so "
            + "managed frames resolve to source lines.")]
        string symbols = "")
    {
        bool inclusive = ResolveMeasure(measure);
        RequirePositiveTop(top);
        IReadOnlyList<string> foldPatterns = ResolveFold(fold);
        string? resolvedSymbols = NullIfEmpty(symbols);

        LoadedTrace before = Load(store, beforePath, resolvedSymbols);
        LoadedTrace after = Load(store, afterPath, resolvedSymbols);

        // Rank every frame (no row cap) so the diff is not skewed by per-side truncation;
        // RankingDiff applies the requested top to the changed rows instead.
        RankingResult beforeRanking = inclusive
            ? before.Aggregator.InclusiveTime(root, foldPatterns, int.MaxValue)
            : before.Aggregator.SelfTime(root, foldPatterns, int.MaxValue);
        RankingResult afterRanking = inclusive
            ? after.Aggregator.InclusiveTime(root, foldPatterns, int.MaxValue)
            : after.Aggregator.SelfTime(root, foldPatterns, int.MaxValue);

        RankingDiffResult diff = RankingDiff.Diff(beforeRanking, afterRanking, top);

        return new AnalysisResult<RankingDiffResult>(diff, DiffWarnings(before.Info, after.Info), SteeringHints.ForDiff(diff));
    }

    /// <summary>
    ///  Returns the garbage-collection report for a <c>.nettrace</c> EventPipe trace:
    ///  the aggregate pause and heap summary plus the hottest per-collection records.
    /// </summary>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="top">Maximum per-collection records to return, ranked by pause time.</param>
    /// <returns>The GC-report envelope.</returns>
    [McpServerTool(Name = "trace_gc", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Garbage-collection report for a .nettrace EventPipe trace: aggregate counts (gen 0/1/2), total/max/mean "
        + "pause time, peak heap size, promoted bytes, and per-collection records capped to the 'top' hottest "
        + "pauses. Frequent gen-2 collections or long pauses point at allocation problems. Requires a .nettrace "
        + "trace; .etl and speedscope are rejected.")]
    public static AnalysisResult<GcStatsResult> Gc(
        [Description("Path to a .nettrace EventPipe trace file.")] string path,
        [Description("Maximum number of per-collection records to return, ranked by pause time.")] int top = 25)
    {
        RequirePositiveTop(top);
        GcStatsResult full = ReadGcStats(path);

        // Keep the full aggregate summary, but cap the per-collection detail to the
        // hottest pauses so a long trace cannot blow the output budget.
        List<string> warnings = [];
        IReadOnlyList<GcRecord> shown = full.Gcs;
        if (shown.Count > top)
        {
            shown = [.. shown.OrderByDescending(static g => g.PauseMs).Take(top)];
            warnings.Add($"Showing the top {top} of {full.GcCount} collections by pause time.");
        }

        GcStatsResult report = full with { Gcs = shown };
        return new AnalysisResult<GcStatsResult>(report, warnings);
    }

    /// <summary>
    ///  Returns a time-bucketed correlation of what a <c>.nettrace</c> or <c>.etl</c>
    ///  trace was doing over its duration: per-bucket GC, CPU, exception, allocation,
    ///  and JIT activity, aligned on one time axis.
    /// </summary>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="lanes">Comma-separated lanes to include; empty means every lane.</param>
    /// <param name="buckets">Number of equal time buckets to divide the window into.</param>
    /// <param name="time">Optional time window (<c>start,end</c> ms) scoping the timeline.</param>
    /// <returns>The timeline envelope.</returns>
    [McpServerTool(Name = "trace_timeline", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Time-bucketed activity across a .nettrace or .etl trace: per-bucket GC, CPU (top method), exception, "
        + "allocation, and JIT counts on one time axis. An orientation view, not a ranking - find the busy window, "
        + "then scope a ranking to it (the hint gives the exact rank --time call). A speedscope export is rejected.")]
    public static AnalysisResult<TimelineResult> Timeline(
        [Description("Path to a .nettrace or .etl trace file.")] string path,
        [Description("Lanes to include: gc, cpu, exceptions, alloc, jit; omit for all.")] string lanes = "",
        [Description("Number of time buckets (clamped to 5-200).")] int buckets = TimelineProvider.DefaultBucketCount,
        [Description("Optional time window 'start,end' in ms; either bound may be omitted.")] string time = "",
        [Description("Process-name substring scoping a multi-process .etl to one tree; omit to auto-scope to the busiest.")] string process = "")
    {
        if (!TimeWindow.TryParse(NullIfEmpty(time), out double? startMSec, out double? endMSec, out string? timeError))
        {
            throw new McpException(timeError ?? "Invalid time window.");
        }

        if (!TimelineProvider.TryResolveLanes(lanes, out IReadOnlyList<string> resolvedLanes, out string? laneError))
        {
            throw new McpException(laneError!);
        }

        List<string> warnings = [];
        int resolvedBuckets = TimelineProvider.ClampBucketCount(buckets, out string? bucketWarning);
        if (bucketWarning is not null)
        {
            warnings.Add(bucketWarning);
        }

        TimeWindow? window = startMSec is null && endMSec is null
            ? null
            : new TimeWindow(startMSec, endMSec);

        TimelineResult result = ReadTimeline(path, window, resolvedLanes, resolvedBuckets, ResolveScope(process));

        // Surface the process the scope resolved to (an explicit name or the automatic
        // busiest) so a narrowed machine-wide capture is not silently one process's view.
        if (result.Process is not null)
        {
            warnings.Add($"Scoped to process '{result.Process}'.");
        }

        return new AnalysisResult<TimelineResult>(result, warnings, SteeringHints.ForTimeline(result));
    }

    /// <summary>
    ///  Returns the thread-pool report for a <c>.nettrace</c> EventPipe trace: how often
    ///  the runtime adjusted the worker-thread count and how often because it detected
    ///  starvation.
    /// </summary>
    /// <param name="path">Path to the trace file.</param>
    /// <returns>The thread-pool report envelope.</returns>
    [McpServerTool(Name = "trace_threadpool", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Thread-pool report for a .nettrace EventPipe trace: worker-thread adjustment counts, how many were "
        + "Starvation (the pool injecting threads because queued work is not completing - the sync-over-async hang "
        + "signal), the worker-thread range vs the configured min/max, and adjustments by reason. Use it when a "
        + "service is slow under load but the CPU is idle. Requires a .nettrace trace; .etl and speedscope are "
        + "rejected.")]
    public static AnalysisResult<ThreadPoolResult> ThreadPool(
        [Description("Path to a .nettrace EventPipe trace file.")] string path)
    {
        ThreadPoolResult report = ReadThreadPool(path);

        List<string> warnings = [];
        if (report.AdjustmentCount == 0)
        {
            warnings.Add("The trace carries no thread-pool worker-thread adjustment events.");
        }

        return new AnalysisResult<ThreadPoolResult>(report, warnings);
    }

    /// <summary>
    ///  Returns the disk I/O report for a Windows ETW <c>.etl</c> trace: physical disk
    ///  reads and writes aggregated by file, ranked by disk service time.
    /// </summary>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="top">Maximum per-file rows to return, ranked by disk time.</param>
    /// <returns>The disk-I/O report envelope.</returns>
    [McpServerTool(Name = "trace_diskio", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Disk I/O report for a Windows ETW .etl trace: physical disk reads and writes by file (bytes, op counts, "
        + "disk service time), capped to the 'top' heaviest files. Physical disk events are after the file-system "
        + "cache, so they show real disk pressure that logical file calls hide. Requires a .etl trace; .nettrace "
        + "and speedscope are rejected.")]
    public static AnalysisResult<DiskIoResult> DiskIo(
        [Description("Path to a Windows ETW .etl trace file.")] string path,
        [Description("Maximum number of per-file rows to return, ranked by disk service time.")] int top = 25)
    {
        RequirePositiveTop(top);
        DiskIoResult full = ReadDiskIo(path);

        // Keep the full aggregate summary, but cap the per-file detail to the heaviest
        // files so a broad capture cannot blow the output budget. An empty report is
        // conveyed by the empty file list, like the other reports.
        List<string> warnings = [];
        IReadOnlyList<DiskIoFileRecord> shown = full.Files;
        if (shown.Count > top)
        {
            shown = [.. shown.Take(top)];
            warnings.Add($"Showing the top {top} of {full.Files.Count} files by disk time.");
        }

        DiskIoResult report = full with { Files = shown };
        return new AnalysisResult<DiskIoResult>(report, warnings);
    }

    /// <summary>
    ///  The largest page <see cref="QueryEvents"/> returns. A larger <c>take</c> is
    ///  clamped to this with a warning, so one call cannot accumulate an unbounded
    ///  page into memory or push the response past the token budget.
    /// </summary>
    private const int MaxEventsPage = 1000;

    /// <summary>
    ///  The largest per-event payload cap <see cref="QueryEvents"/> honors. A larger
    ///  <c>maxPayload</c> is clamped to this with a warning.
    /// </summary>
    private const int MaxEventPayloadChars = 4000;

    /// <summary>
    ///  Queries the raw events of a <c>.nettrace</c> EventPipe or <c>.etl</c> ETW trace by
    ///  name, paged and with each event's payload truncated, so an agent can inspect
    ///  arbitrary events.
    /// </summary>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="name">Substring matched against <c>Provider/EventName</c>; empty matches every event.</param>
    /// <param name="skip">The number of matches to skip, for paging.</param>
    /// <param name="take">The maximum number of matches to return on this page.</param>
    /// <param name="maxPayload">The per-event payload character cap.</param>
    /// <returns>The events-page envelope.</returns>
    [McpServerTool(Name = "trace_query_events", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Query the raw events of a .nettrace EventPipe or Windows ETW .etl trace - the escape hatch for events the "
        + "structured reports do not cover. 'name' is a case-insensitive substring matched against Provider/EventName "
        + "(empty matches all); narrow further with 'payload' (a substring of the event's payload values), 'pid', and "
        + "'tid'. Paged with 'skip'/'take', each payload truncated to 'maxPayload' chars; a hint gives the next page's "
        + "skip when more remain. A speedscope export is rejected.")]
    public static AnalysisResult<EventQueryResult> QueryEvents(
        [Description("Path to a .nettrace EventPipe or Windows ETW .etl trace file.")] string path,
        [Description("Substring matched against Provider/EventName; omit to match every event.")] string name = "",
        [Description("The number of matches to skip, for paging.")] int skip = 0,
        [Description("The maximum number of matches to return on this page.")] int take = 100,
        [Description("The per-event payload character cap.")] int maxPayload = 200,
        [Description("Case-insensitive substring matched against payload values; omit for no payload filter.")] string payload = "",
        [Description("Keep only events from this OS process id; -1 (default) keeps every process.")] int pid = -1,
        [Description("Keep only events on this OS thread id; -1 (default) keeps every thread.")] int tid = -1)
    {
        if (skip < 0)
        {
            throw new McpException("skip must be 0 or greater.");
        }

        if (take < 0)
        {
            throw new McpException("take must be 0 or greater.");
        }

        if (maxPayload < 0)
        {
            throw new McpException("maxPayload must be 0 or greater.");
        }

        if (pid < -1)
        {
            throw new McpException("pid must be -1 (unset) or a non-negative process id.");
        }

        if (tid < -1)
        {
            throw new McpException("tid must be -1 (unset) or a non-negative thread id.");
        }

        // Clamp the page and payload sizes to a ceiling so a caller cannot request a
        // page large enough to exhaust memory or push the response past the token
        // budget; a clamp is a warning rather than an error, so the query still runs.
        List<string> warnings = [];
        if (take > MaxEventsPage)
        {
            warnings.Add($"take {take} exceeds the {MaxEventsPage} maximum; clamped to {MaxEventsPage}.");
            take = MaxEventsPage;
        }

        if (maxPayload > MaxEventPayloadChars)
        {
            warnings.Add(
                $"maxPayload {maxPayload} exceeds the {MaxEventPayloadChars} maximum; clamped to {MaxEventPayloadChars}.");
            maxPayload = MaxEventPayloadChars;
        }

        EventQueryResult result = ReadEvents(
            path, name, skip, take, maxPayload, payload, pid >= 0 ? pid : null, tid >= 0 ? tid : null);

        // When matches remain beyond this page, steer toward the next one rather than
        // leaving the agent to work out the skip arithmetic.
        List<string> hints = [];
        int shownThrough = result.Skipped + result.Events.Count;
        if (shownThrough < result.TotalMatched)
        {
            hints.Add($"{result.TotalMatched - shownThrough} more match; page with skip {shownThrough}.");
        }

        return new AnalysisResult<EventQueryResult>(result, warnings, hints);
    }

    /// <summary>
    ///  Exports a trace's CPU sample source to a speedscope or Chrome-trace flame-graph
    ///  file an agent can hand a human to open in a viewer.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="output">The file path to write the flame graph to.</param>
    /// <param name="format">The flame-graph format: <c>speedscope</c> or <c>chromium</c>.</param>
    /// <param name="name">The profile name embedded in the flame graph, shown in the viewer.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <param name="process">Optional process-name substring scoping a multi-process <c>.etl</c> to one process tree.</param>
    /// <param name="root">Optional substring of a frame name to scope the export to its subtree.</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); mutually exclusive with <paramref name="root"/>.</param>
    /// <param name="nativeSymbols">Resolve native runtime frames from the public symbol server (opt-in, network); .etl captures only.</param>
    /// <returns>The export-confirmation envelope.</returns>
    [McpServerTool(Name = "trace_export", ReadOnly = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Export a trace's CPU samples to a flame-graph file for a human to open in a viewer. format=speedscope "
        + "(the default) opens at speedscope.app; format=chromium writes the Chrome Trace Event Format for "
        + "chrome://tracing or Perfetto. 'output' is the file path to write (required; this writes a file and "
        + "overwrites an existing one). No folding or ranking is applied. Pass 'process' to scope a machine-wide "
        + ".etl to one process tree (omit to auto-scope to the busiest), 'root' to scope to a frame's subtree, or "
        + "'benchmark' to preset the BenchmarkDotNet measured-workload frame (mutually exclusive with 'root'; "
        + "default it for a BenchmarkDotNet capture). Pass 'nativeSymbols' to resolve unmanaged GC/JIT frames "
        + "(opt-in, network). The response confirms the path, format, and byte count.")]
    public static AnalysisResult<ExportResult> Export(
        TraceStore store,
        [Description("Path to a .speedscope.json, .nettrace, or .etl trace file.")] string path,
        [Description("The file path to write the flame graph to (it is overwritten if it exists).")] string output,
        [Description("The flame-graph format: speedscope or chromium.")] string format = "speedscope",
        [Description("The profile name embedded in the flame graph, shown in the viewer.")] string name = "filtrace",
        [Description(
            "Optional build-output directory whose assemblies' embedded portable PDBs are extracted so "
            + "managed frames resolve to source lines.")]
        string symbols = "",
        [Description(
            "Optional process-name substring scoping a multi-process .etl capture to one process tree; omit "
            + "to auto-scope to the busiest. Ignored for single-process .nettrace/speedscope traces.")]
        string process = "",
        [Description(
            "Optional substring of a frame name to scope the exported flame graph to its subtree; for a "
            + "BenchmarkDotNet capture prefer 'benchmark' instead. Mutually exclusive with 'benchmark'.")]
        string root = "",
        [Description(
            "Scope the exported flame graph to the BenchmarkDotNet measured-workload subtree (a preset root); "
            + "default to this for any BenchmarkDotNet capture so the harness/warmup does not dominate the "
            + "graph. Mutually exclusive with 'root'.")]
        bool benchmark = false,
        [Description(
            "Resolve native runtime frames (GC, JIT, memset/memcpy) from the Microsoft public symbol server. "
            + "Opt-in - it fetches over the network and caches locally. .etl captures only; managed frames "
            + "already resolve without it.")]
        bool nativeSymbols = false)
    {
        bool chromium = ResolveExportFormat(format);

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new McpException("output is required: the file path to write the flame graph to.");
        }

        string resolvedRoot = ResolveRoot(root, benchmark);
        LoadedTrace trace = Load(store, path, NullIfEmpty(symbols), scope: ResolveScope(process), symbolOptions: ResolveSymbols(nativeSymbols));
        TraceInfo info = trace.Info;

        StackSampleSource scoped = RootScope.Apply(trace.Source, resolvedRoot);

        string exported = chromium
            ? ChromiumExporter.Export(scoped, name)
            : SpeedscopeExporter.Export(scoped, name);

        string outputPath = WriteExport(output, exported);
        long byteCount = new FileInfo(outputPath).Length;

        ExportResult result = new(chromium ? "chromium" : "speedscope", outputPath, byteCount, name);
        string hint = chromium
            ? $"open {outputPath} in chrome://tracing or the Perfetto UI (https://ui.perfetto.dev)"
            : $"open {outputPath} at https://speedscope.app";

        return new AnalysisResult<ExportResult>(result, info.Warnings, [hint]);
    }

    /// <summary>
    ///  Lists the processes a trace contains, ranked by CPU-sample weight, so a
    ///  multi-process capture can be scoped to the right one before ranking.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <returns>The process-inventory envelope.</returns>
    [McpServerTool(Name = "trace_processes", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "List the processes a trace contains, ranked by CPU-sample weight. This is the first move on a "
        + "machine-wide .etl capture: see who is in it, then scope the ranking and drill-down tools to one with "
        + "their 'process' parameter. Reads every process - it does not auto-scope to the busiest. A single-process "
        + ".nettrace or speedscope trace lists just its one process.")]
    public static AnalysisResult<ProcessListResult> Processes(
        TraceStore store,
        [Description("Path to a .speedscope.json, .nettrace, or .etl trace file.")] string path)
    {
        // The inventory's whole purpose is to reveal every process, so it opts out of
        // the busiest-process auto-scope the ranking tools default to.
        LoadedTrace trace = Load(store, path, symbols: null, TraceMetric.Cpu, ScopeRequest.AllProcesses);
        TraceInfo info = trace.Info;
        ProcessListResult processes = trace.Aggregator.Processes();

        return new AnalysisResult<ProcessListResult>(processes, info.Warnings);
    }

    /// <summary>
    ///  Returns the top-down call tree from the root into its callees, following the hot
    ///  path down to the work that dominates it.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="root">Optional substring scoping the tree to a frame's subtree.</param>
    /// <param name="maxDepth">Maximum frame levels below the root to expand.</param>
    /// <param name="minPercent">Minimum share of the scoped total, in percent, a node must have to appear.</param>
    /// <param name="fold">Optional fold patterns; defaults to the built-in JIT-helper list.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <param name="process">Optional process-name substring scoping a multi-process .etl capture to one process tree.</param>
    /// <returns>The call-tree envelope.</returns>
    [McpServerTool(Name = "trace_tree", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Top-down CPU call tree from a root frame into its callees, following the hot path down to the work that "
        + "dominates it - the complement to trace_callers, which walks up. Each node carries its inclusive share; "
        + "maxDepth caps how far below the root it expands and minPercent prunes nodes below a share of the scoped "
        + "total so the tree stays to the meaningful paths. Scope to a subtree with root; omit it for the whole "
        + "trace. JIT-helper leaves are folded into their caller.")]
    public static AnalysisResult<CallTreeResult> Tree(
        TraceStore store,
        [Description("Path to a .speedscope.json, .nettrace, or .etl trace file.")] string path,
        [Description("Optional substring of a frame name to scope the tree to its subtree; omit for the whole trace.")] string root = "",
        [Description("Maximum number of frame levels below the root to expand.")] int maxDepth = 10,
        [Description("Minimum share of the scoped total, in percent, a node must have to appear.")] double minPercent = 1.0,
        [Description("Optional regex fold patterns; omit to use the built-in JIT-helper defaults.")] string[]? fold = null,
        [Description(
            "Optional build-output directory whose assemblies' embedded portable PDBs are extracted so "
            + "managed frames resolve to source lines.")]
        string symbols = "",
        [Description(
            "Optional process-name substring scoping a multi-process .etl capture to one process tree; omit "
            + "to auto-scope to the busiest. Ignored for single-process .nettrace/speedscope traces.")]
        string process = "")
    {
        if (maxDepth < 0 || maxDepth > FoldingAggregator.MaxTreeDepth)
        {
            throw new McpException($"maxDepth must be in [0, {FoldingAggregator.MaxTreeDepth}].");
        }

        if (minPercent < 0)
        {
            throw new McpException("minPercent must be 0 or greater.");
        }

        IReadOnlyList<string> foldPatterns = ResolveFold(fold);
        LoadedTrace trace = Load(store, path, NullIfEmpty(symbols), scope: ResolveScope(process));
        TraceInfo info = trace.Info;
        CallTreeResult tree = trace.Aggregator.CallTree(root, foldPatterns, maxDepth, minPercent);

        return new AnalysisResult<CallTreeResult>(tree, info.Warnings);
    }

    /// <summary>
    ///  Buckets CPU self-time by runtime work category - zeroing, copying, write-barrier,
    ///  GC, JIT, or other - to answer where the time went at the machine level.
    /// </summary>
    /// <param name="store">The trace cache (injected).</param>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="root">Optional substring scoping the classification to a subtree.</param>
    /// <param name="symbols">Optional build-output directory supplying embedded PDBs for line resolution.</param>
    /// <param name="process">Optional process-name substring scoping a multi-process .etl capture to one process tree.</param>
    /// <param name="nativeSymbols">Resolve native runtime frames from the public symbol server (opt-in, network); cpu/.etl only.</param>
    /// <returns>The classification envelope.</returns>
    [McpServerTool(Name = "trace_classify", ReadOnly = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Bucket CPU self-time by runtime work category - zeroing, copying, write-barrier, GC, JIT, or other - to "
        + "answer 'where did the time go: zeroing memory? copying? in the GC?'. The categories are recognized from "
        + "native runtime frames, so pair this with nativeSymbols=true on an .etl capture; without resolved native "
        + "symbols the runtime leaves fall in 'other' and the breakdown understates the real cost. Scope to a "
        + "subtree with root.")]
    public static AnalysisResult<ClassifyResult> Classify(
        TraceStore store,
        [Description("Path to a .speedscope.json, .nettrace, or .etl trace file.")] string path,
        [Description("Optional substring of a frame name to scope the classification to its subtree.")] string root = "",
        [Description(
            "Optional build-output directory whose assemblies' embedded portable PDBs are extracted so "
            + "managed frames resolve to source lines.")]
        string symbols = "",
        [Description(
            "Optional process-name substring scoping a multi-process .etl capture to one process tree; omit "
            + "to auto-scope to the busiest. Ignored for single-process .nettrace/speedscope traces.")]
        string process = "",
        [Description(
            "Resolve native runtime frames (GC, JIT, memset/memcpy) from the Microsoft public symbol server so "
            + "they classify instead of falling in 'other'. Opt-in - it fetches over the network and caches "
            + "locally. cpu over an .etl capture only.")]
        bool nativeSymbols = false)
    {
        LoadedTrace trace = Load(store, path, NullIfEmpty(symbols), TraceMetric.Cpu, ResolveScope(process), ResolveSymbols(nativeSymbols));
        TraceInfo info = trace.Info;
        ClassifyResult classification = trace.Aggregator.Classify(root);

        return new AnalysisResult<ClassifyResult>(classification, info.Warnings);
    }

    /// <summary>
    ///  Returns the JIT-compilation report for a <c>.nettrace</c> EventPipe trace:
    ///  the aggregate compile summary plus the costliest per-method records.
    /// </summary>
    /// <param name="path">Path to the trace file.</param>
    /// <param name="top">Maximum per-method records to return, ranked by compile time.</param>
    /// <returns>The JIT-report envelope.</returns>
    [McpServerTool(Name = "trace_jit", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "JIT-compilation report for a .nettrace EventPipe trace: the method count, total and mean compile time, "
        + "and the per-method records capped to the 'top' costliest compiles. Use it to judge startup or first-call "
        + "JIT cost - a startup trace can compile thousands of methods. The aggregate summary always reflects every "
        + "method; only the per-method detail is capped. Requires a .nettrace trace; .etl and speedscope inputs are "
        + "rejected.")]
    public static AnalysisResult<JitStatsResult> Jit(
        [Description("Path to a .nettrace EventPipe trace file.")] string path,
        [Description("Maximum number of per-method records to return, ranked by compile time.")] int top = 25)
    {
        RequirePositiveTop(top);
        JitStatsResult full = ReadJitStats(path);

        // Keep the full aggregate summary, but cap the per-method detail to the
        // costliest compiles so a startup trace's thousands of methods cannot blow
        // the output budget.
        List<string> warnings = [];
        IReadOnlyList<JitMethodRecord> shown = full.Methods;
        if (shown.Count > top)
        {
            shown = [.. shown.OrderByDescending(static m => m.CompileMs).Take(top)];
            warnings.Add($"Showing the top {top} of {full.MethodCount} methods by compile time.");
        }

        JitStatsResult report = full with { Methods = shown };
        return new AnalysisResult<JitStatsResult>(report, warnings);
    }

    /// <summary>
    ///  Loads the <paramref name="metric"/> view of the trace, mapping the loader's
    ///  failure modes to a clean <see cref="McpException"/> rather than letting an
    ///  opaque exception propagate to the client.
    /// </summary>
    private static LoadedTrace Load(
        TraceStore store,
        string path,
        string? symbols,
        TraceMetric metric = TraceMetric.Cpu,
        ScopeRequest? scope = null,
        SymbolOptions? symbolOptions = null)
    {
        try
        {
            return store.Get(path, symbols, metric, scope, symbolOptions);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException
            or FormatException
            or ArgumentException)
        {
            // Missing, unreadable, or malformed trace input - including a format that
            // does not carry the selected metric's data (NotSupportedException) -
            // surfaces as a clean tool error rather than an unhandled exception.
            throw new McpException(ex.Message);
        }
    }

    private static TraceMetric ResolveMetric(string metric) =>
        TraceMetricSelector.TryResolve(metric, out TraceMetric resolved)
            ? resolved
            : throw new McpException(
                $"Unknown metric '{metric}'. Valid metrics: {string.Join(", ", TraceMetricSelector.Selectors)}.");

    private static bool ResolveMeasure(string measure)
    {
        if (string.Equals(measure, "self", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(measure, "inclusive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new McpException($"Unknown measure '{measure}'. Valid measures: self, inclusive.");
    }

    /// <summary>
    ///  Resolves the export <c>format</c> selector to whether the Chrome-trace exporter
    ///  is used (otherwise speedscope).
    /// </summary>
    private static bool ResolveExportFormat(string format)
    {
        if (string.Equals(format, "speedscope", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(format, "chromium", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new McpException($"Unknown format '{format}'. Valid formats: speedscope, chromium.");
    }

    /// <summary>
    ///  Writes the exported flame graph to <paramref name="output"/>, mapping a bad or
    ///  unwritable path to a clean <see cref="McpException"/>, and returns the absolute
    ///  path written.
    /// </summary>
    private static string WriteExport(string output, string content)
    {
        try
        {
            string fullPath = Path.GetFullPath(output);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException
            or ArgumentException)
        {
            // A bad or unwritable output path (missing directory, permission denied,
            // invalid characters) surfaces as a clean tool error rather than an
            // unhandled exception.
            throw new McpException($"Could not write '{output}': {ex.Message}");
        }
    }

    private static void RequirePositiveTop(int top)
    {
        if (top < 1)
        {
            throw new McpException("top must be 1 or greater.");
        }
    }

    /// <summary>
    ///  Reads the timeline for a <c>.nettrace</c> or <c>.etl</c> trace, applying the
    ///  format guardrail and mapping the provider's failure modes to a clean
    ///  <see cref="McpException"/>.
    /// </summary>
    private static TimelineResult ReadTimeline(string path, TimeWindow? window, IReadOnlyList<string> lanes, int buckets, ScopeRequest? scope)
    {
        RequireNetTraceOrEtl(path, "timeline");

        try
        {
            return new TimelineProvider().Read(path, window, lanes, buckets, scope);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or FormatException
            or ArgumentException)
        {
            // A missing, unreadable, or malformed trace - or an .etl read attempted off
            // Windows (PlatformNotSupportedException derives from NotSupportedException) -
            // surfaces as a clean tool error rather than an unhandled exception.
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    ///  Resolves the fold patterns to use - the caller's patterns or the built-in
    ///  JIT-helper defaults - and validates them up front so a malformed user-supplied
    ///  regex surfaces as a clean, actionable tool error rather than escaping the
    ///  aggregator as a framework-level exception.
    /// </summary>
    private static IReadOnlyList<string> ResolveFold(string[]? fold)
    {
        IReadOnlyList<string> patterns = fold is { Length: > 0 } ? fold : FrameNames.DefaultFoldPatterns;

        try
        {
            // Compile only to validate; the aggregator compiles its own copy when it runs.
            // CompileFoldPatterns also caps each match with a timeout, so a pathological
            // user pattern cannot hang the server.
            _ = FrameNames.CompileFoldPatterns(patterns);
        }
        catch (ArgumentException ex)
        {
            // A malformed fold regex is a usage error; surface the message (which names the
            // offending pattern) as a clean tool error instead of an internal failure.
            throw new McpException(ex.Message);
        }

        return patterns;
    }

    /// <summary>
    ///  Reads the GC report for a <c>.nettrace</c> trace, applying the format guardrail
    ///  and mapping the provider's failure modes to a clean <see cref="McpException"/>.
    /// </summary>
    private static GcStatsResult ReadGcStats(string path)
    {
        RequireNetTrace(path, "GC report");

        try
        {
            return new GcStatsProvider().Read(path);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or FormatException
            or ArgumentException)
        {
            // A missing, unreadable, or malformed .nettrace surfaces as a clean tool
            // error rather than an unhandled exception.
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    ///  Reads the thread-pool report for a <c>.nettrace</c> trace, applying the format
    ///  guardrail and mapping the provider's failure modes to a clean <see cref="McpException"/>.
    /// </summary>
    private static ThreadPoolResult ReadThreadPool(string path)
    {
        RequireNetTrace(path, "thread-pool report");

        try
        {
            return new ThreadPoolProvider().Read(path);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or FormatException
            or ArgumentException)
        {
            // A missing, unreadable, or malformed .nettrace surfaces as a clean tool
            // error rather than an unhandled exception.
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    ///  Reads the JIT report for a <c>.nettrace</c> trace, applying the format guardrail
    ///  and mapping the provider's failure modes to a clean <see cref="McpException"/>.
    /// </summary>
    private static JitStatsResult ReadJitStats(string path)
    {
        RequireNetTrace(path, "JIT report");

        try
        {
            return new JitStatsProvider().Read(path);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or FormatException
            or ArgumentException)
        {
            // A missing, unreadable, or malformed .nettrace surfaces as a clean tool
            // error rather than an unhandled exception.
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    ///  Reads an events page for a <c>.nettrace</c> or <c>.etl</c> trace, applying the
    ///  format guardrail and mapping the provider's failure modes to a clean
    ///  <see cref="McpException"/>.
    /// </summary>
    private static EventQueryResult ReadEvents(
        string path, string name, int skip, int take, int maxPayload, string payload, int? pid, int? tid)
    {
        RequireNetTraceOrEtl(path, "events query");

        try
        {
            return new EventQueryProvider().Query(path, name, skip, take, maxPayload, payload, pid, tid);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or FormatException
            or ArgumentException)
        {
            // A missing, unreadable, or malformed trace - or an .etl read attempted off
            // Windows (PlatformNotSupportedException derives from NotSupportedException) -
            // surfaces as a clean tool error rather than an unhandled exception.
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    ///  Rejects a non-<c>.nettrace</c> input up front for the EventPipe-only report
    ///  providers, with a clean message naming the report, rather than failing deep
    ///  inside the EventPipe parser.
    /// </summary>
    private static void RequireNetTrace(string path, string reportName)
    {
        // Format guardrail (an extension test, no I/O): the report providers parse the
        // EventPipe format, so reject an .etl or speedscope export cleanly here.
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"The {reportName} requires a .nettrace EventPipe trace; '{path}' is not a .nettrace file.");
        }
    }

    /// <summary>
    ///  Rejects a trace that is neither <c>.nettrace</c> nor <c>.etl</c> up front for the
    ///  raw event query, which reads the event stream both formats carry (but not a
    ///  speedscope export, which holds only CPU stacks).
    /// </summary>
    private static void RequireNetTraceOrEtl(string path, string reportName)
    {
        // Format guardrail (an extension test, no I/O): the raw event query spans the
        // EventPipe (.nettrace) and ETW (.etl) event streams, so reject anything else - a
        // speedscope export carries no event stream to query.
        if (string.IsNullOrEmpty(path)
            || !(path.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)))
        {
            string display = string.IsNullOrEmpty(path) ? "(no path)" : path;
            throw new McpException(
                $"The {reportName} requires a .nettrace EventPipe or .etl ETW trace (Windows-only); '{display}' is neither.");
        }
    }

    /// <summary>
    ///  Reads the disk I/O report for a Windows ETW <c>.etl</c> trace, applying the format
    ///  guardrail and mapping the provider's failure modes to a clean <see cref="McpException"/>.
    /// </summary>
    private static DiskIoResult ReadDiskIo(string path)
    {
        RequireEtl(path, "disk I/O report");

        try
        {
            return new DiskIoProvider().Read(path);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or FormatException
            or ArgumentException)
        {
            // A missing, unreadable, or malformed .etl surfaces as a clean tool error.
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    ///  Rejects a non-<c>.etl</c> input up front for the ETW-only report providers, with
    ///  a clean message naming the report, rather than failing deep inside the reader.
    /// </summary>
    private static void RequireEtl(string path, string reportName)
    {
        // Format guardrail (an extension test, no I/O): the kernel disk events are
        // ETW-only, so reject a .nettrace or speedscope export cleanly here.
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            string display = string.IsNullOrEmpty(path) ? "(no path)" : path;
            throw new McpException(
                $"The {reportName} requires a Windows ETW .etl trace; '{display}' is not a .etl file.");
        }
    }

    /// <summary>
    ///  Builds the diff warnings: the baseline and current traces' quality warnings,
    ///  each prefixed with which side it came from.
    /// </summary>
    private static IReadOnlyList<string> DiffWarnings(TraceInfo before, TraceInfo after)
    {
        List<string> warnings = [];
        foreach (string warning in before.Warnings)
        {
            warnings.Add($"baseline: {warning}");
        }

        foreach (string warning in after.Warnings)
        {
            warnings.Add($"current: {warning}");
        }

        return warnings;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    // An empty process selector means "auto-scope to the busiest process tree" (the
    // Load default), a no-op on a single-process .nettrace/speedscope trace; a non-empty
    // value scopes a multi-process .etl capture to the named process tree.
    private static ScopeRequest? ResolveScope(string process) =>
        string.IsNullOrEmpty(process) ? null : ScopeRequest.ForProcess(process);

    /// <summary>
    ///  Resolves the root-frame scope from the explicit <c>root</c> argument and the
    ///  <c>benchmark</c> preset, mirroring the CLI's <c>--root</c>/<c>--benchmark</c>
    ///  mutual exclusion (<c>RankRequestFactory.TryResolveRoot</c>).
    /// </summary>
    private static string ResolveRoot(string root, bool benchmark)
    {
        if (benchmark && !string.IsNullOrEmpty(root))
        {
            throw new McpException("Specify only one of 'root' and 'benchmark'.");
        }

        return benchmark ? FrameNames.BenchmarkWorkloadFrame : root;
    }

    // Opt-in native runtime symbol resolution; the default cache directory is used.
    // Off resolves managed frames from the rundown only (offline).
    private static SymbolOptions ResolveSymbols(bool nativeSymbols) =>
        nativeSymbols ? SymbolOptions.WithCache() : SymbolOptions.None;
}
