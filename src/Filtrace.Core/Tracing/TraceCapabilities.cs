// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The analyses filtrace can run against a trace of a given format - the "what can
///  this capture answer?" inventory an agent reads to route a question to a metric or
///  report it can actually produce, instead of trying one the format cannot support.
/// </summary>
/// <remarks>
///  <para>
///   The list is a hard format constraint, not a capture-content guarantee: an
///   analysis is listed when filtrace can build it from this format at all. Allocation,
///   exceptions, contention, wait, activity, and the GC / JIT / thread-pool reports are
///   EventPipe-only; thread time, the runtime-work classification, the process
///   inventory, and the disk-I/O report are ETW-only; a speedscope export carries CPU
///   stacks alone. The raw event query is the one analysis that spans both EventPipe
///   and ETW. <see cref="AvailabilityFor"/> combines this format inventory with
///   observed source records and optional recorder metadata; absence alone remains
///   unknown because some analyses need non-default keywords.
///  </para>
/// </remarks>
public static class TraceCapabilities
{
    private static readonly string[] AllAnalyses =
    [
        "cpu", "alloc", "exceptions", "contention", "wait", "activity",
        "gcstats", "jitstats", "threadpool", "threadtime", "classify",
        "processes", "diskio", "events"
    ];

    /// <summary>
    ///  The analysis selectors (rank metrics and report verbs) filtrace can produce
    ///  from a trace of <paramref name="format"/>, lowest-level first.
    /// </summary>
    /// <param name="format">The trace's on-disk format.</param>
    /// <returns>The applicable analysis names.</returns>
    public static IReadOnlyList<string> AnalysesFor(TraceFormat format) => format switch
    {
        TraceFormat.Speedscope => ["cpu"],
        TraceFormat.NetTrace => ["cpu", "alloc", "exceptions", "contention", "wait", "activity", "gcstats", "jitstats", "threadpool", "events"],
        TraceFormat.Etl => ["cpu", "threadtime", "classify", "processes", "diskio", "events"],
        _ => []
    };

    internal static bool IsKnownAnalysis(string analysis) =>
        Array.IndexOf(AllAnalyses, analysis) >= 0;

    /// <summary>
    ///  Combines format support, observed source records, and optional recorder
    ///  metadata into per-analysis availability.
    /// </summary>
    /// <param name="format">The trace's on-disk format.</param>
    /// <param name="eventCounts">Observed source-record counts keyed by analysis name.</param>
    /// <param name="captureStatuses">
    ///  Recorder-established enablement keyed by analysis name, or
    ///  <see langword="null"/> when the trace has no capture metadata.
    /// </param>
    /// <returns>Every known analysis keyed by selector.</returns>
    public static IReadOnlyDictionary<string, AnalysisAvailability> AvailabilityFor(
        TraceFormat format,
        IReadOnlyDictionary<string, int> eventCounts,
        IReadOnlyDictionary<string, CaptureStatus>? captureStatuses = null)
    {
        ArgumentNullException.ThrowIfNull(eventCounts);

        HashSet<string> supported = new(AnalysesFor(format), StringComparer.Ordinal);
        Dictionary<string, AnalysisAvailability> availability = new(StringComparer.Ordinal);
        foreach (string analysis in AllAnalyses)
        {
            if (!supported.Contains(analysis))
            {
                availability[analysis] = new AnalysisAvailability(
                    FormatSupported: false,
                    CaptureStatus.Unknown,
                    EventCount: null);
                continue;
            }

            int observed = eventCounts.GetValueOrDefault(analysis);
            if (observed > 0
                || format == TraceFormat.Speedscope && analysis == "cpu")
            {
                availability[analysis] = new AnalysisAvailability(
                    FormatSupported: true,
                    CaptureStatus.Enabled,
                    observed);
                continue;
            }

            CaptureStatus captureStatus = captureStatuses?.GetValueOrDefault(analysis)
                ?? CaptureStatus.Unknown;
            availability[analysis] = new AnalysisAvailability(
                FormatSupported: true,
                captureStatus,
                captureStatus == CaptureStatus.Enabled ? 0 : null);
        }

        return availability;
    }
}
