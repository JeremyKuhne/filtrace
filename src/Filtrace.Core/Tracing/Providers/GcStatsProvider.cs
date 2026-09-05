// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The GC-stats provider: reads the structured garbage-collection records from a
///  .NET EventPipe trace into a <see cref="GcStatsResult"/>.
/// </summary>
/// <remarks>
///  <para>
///   GC behavior is captured by the runtime's GC events (a GC-verbose EventPipe
///   profile), which TraceEvent's analysis layer assembles into per-collection
///   <c>TraceGC</c> records. Unlike the stack-source families this is structured
///   data, not weighted stacks, so it returns its own result rather than a
///   <see cref="StackSampleSource"/>.
///  </para>
/// </remarks>
public sealed class GcStatsProvider
{
    // The JSON scaffolding around one collection record - the property names, the
    // punctuation, and the numeric generation, pause, heap, and promoted fields -
    // which the per-record estimate adds to the record's variable text.
    private const int RecordScaffoldTokens = 40;
    /// <summary>
    ///  Reads the GC-stats report from the EventPipe trace at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <returns>The GC report, or an empty report when the trace carries no GC events.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public GcStatsResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        using EtlxTraceLog traceLog = TraceConverter.OpenTraceLog(fullPath, out _);

        // The GC analysis layer reconstructs per-collection records from the raw GC
        // events as the source is processed; request it before draining the events.
        using TraceLogEventSource source = traceLog.Events.GetSource();
        source.NeedLoadedDotNetRuntimes();
        source.Process();

        List<GcRecord> records = [];
        foreach (AnalysisTraceProcess process in source.Processes())
        {
            TraceLoadedDotNetRuntime? runtime = process.LoadedDotNetRuntime();
            if (runtime is null)
            {
                continue;
            }

            foreach (TraceGC gc in runtime.GC.GCs)
            {
                records.Add(new GcRecord(
                    gc.Number,
                    gc.Generation,
                    gc.Type.ToString(),
                    gc.Reason.ToString(),
                    gc.PauseDurationMSec,
                    gc.HeapSizeAfterMB,
                    gc.PromotedMB));
            }
        }

        return Summarize(records, traceLog.SessionDuration.TotalMilliseconds);
    }

    /// <summary>
    ///  Limits a report's per-collection detail to the longest pauses that fit both
    ///  <paramref name="top"/> and <see cref="OutputBudget.DefaultRowBudgetTokens"/>,
    ///  leaving the aggregate summary untouched.
    /// </summary>
    /// <param name="report">The full report, as returned by <see cref="Read"/>.</param>
    /// <param name="top">
    ///  The caller's maximum detail row count. Must be non-negative; zero keeps the
    ///  aggregate summary and drops every collection row.
    /// </param>
    /// <param name="warning">
    ///  The warning naming what was dropped, or <see langword="null"/> when the whole
    ///  collection list was kept.
    /// </param>
    /// <returns>The limited report, or <paramref name="report"/> itself when every collection fit.</returns>
    /// <remarks>
    ///  <para>
    ///   Shared by both heads so they bound and word the result identically. Collections
    ///   are ranked by pause time only when something has to be dropped, so a report that
    ///   fits keeps the trace order <see cref="Read"/> produced.
    ///  </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is negative.</exception>
    public static GcStatsResult LimitDetail(GcStatsResult report, int top, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfNegative(top);

        List<GcRecord> kept = OutputBudget.TakeWithinBudget(
            report.Gcs.OrderByDescending(static collection => collection.PauseMs).Take(top),
            EstimateRecordTokens,
            OutputBudget.DefaultRowBudgetTokens,
            out bool budgetTruncated);

        if (kept.Count == report.Gcs.Count)
        {
            warning = null;
            return report;
        }

        warning = budgetTruncated
            ? $"Showing {kept.Count} of {report.GcCount} collections by pause time; more would exceed the "
                + $"{OutputBudget.DefaultRowBudgetTokens}-token detail budget that holds the whole response under "
                + $"the {OutputBudget.DefaultCeilingTokens}-token ceiling. The aggregate summary still covers "
                + "every collection."
            : top == 0
                ? $"Aggregate only: {report.GcCount} collections were not listed. Ask again with a positive top "
                    + "for the per-collection detail."
                : $"Showing the top {top} of {report.GcCount} collections by pause time.";

        return report with { Gcs = kept };
    }

    private static int EstimateRecordTokens(GcRecord collection) =>
        RecordScaffoldTokens
            + OutputBudget.EstimateTokens(collection.Kind)
            + OutputBudget.EstimateTokens(collection.Reason);

    private static GcStatsResult Summarize(List<GcRecord> records, double durationMs)
    {
        if (records.Count == 0)
        {
            return new GcStatsResult(0, 0, 0, 0, 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, records);
        }

        int gen0 = 0;
        int gen1 = 0;
        int gen2 = 0;
        int induced = 0;
        double totalPause = 0.0;
        double maxPause = 0.0;
        double peakHeap = 0.0;
        double totalPromoted = 0.0;

        foreach (GcRecord gc in records)
        {
            switch (gc.Generation)
            {
                case 0:
                    gen0++;
                    break;
                case 1:
                    gen1++;
                    break;
                default:
                    gen2++;
                    break;
            }

            // The induced reasons (Induced, InducedNotForced, InducedLowMemory, ...) all
            // start with "Induced"; count them so an explicit GC.Collect anti-pattern is
            // visible at a glance.
            if (gc.Reason.Contains("Induced", StringComparison.OrdinalIgnoreCase))
            {
                induced++;
            }

            totalPause += gc.PauseMs;
            maxPause = Math.Max(maxPause, gc.PauseMs);
            peakHeap = Math.Max(peakHeap, gc.HeapSizeAfterMB);
            totalPromoted += gc.PromotedMB;
        }

        // Percentage of the captured window spent paused for GC. A zero-length window
        // (a degenerate capture) reports 0 rather than dividing by zero.
        double percentInGc = durationMs > 0.0 ? 100.0 * totalPause / durationMs : 0.0;

        return new GcStatsResult(
            records.Count,
            gen0,
            gen1,
            gen2,
            induced,
            totalPause,
            maxPause,
            totalPause / records.Count,
            percentInGc,
            peakHeap,
            totalPromoted,
            records);
    }
}
