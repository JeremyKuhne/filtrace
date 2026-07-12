// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using TraceLog = Microsoft.Diagnostics.Tracing.Etlx.TraceLog;
using TraceLogEventSource = Microsoft.Diagnostics.Tracing.Etlx.TraceLogEventSource;
using TraceLogOptions = Microsoft.Diagnostics.Tracing.Etlx.TraceLogOptions;
using TraceProcess = Microsoft.Diagnostics.Tracing.Analysis.TraceProcess;

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

        using TraceLog traceLog = TraceConverter.OpenTraceLog(fullPath, out _);

        // The GC analysis layer reconstructs per-collection records from the raw GC
        // events as the source is processed; request it before draining the events.
        using TraceLogEventSource source = traceLog.Events.GetSource();
        source.NeedLoadedDotNetRuntimes();
        source.Process();

        List<GcRecord> records = [];
        foreach (TraceProcess process in source.Processes())
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
