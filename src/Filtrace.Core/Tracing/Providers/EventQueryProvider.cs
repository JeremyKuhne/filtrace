// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Queries the raw events of a trace by name, with pagination and payload
///  truncation, so an agent can inspect arbitrary events (the field guide's "Any
///  Stacks" / event view) without drowning in a machine-wide firehose.
/// </summary>
/// <remarks>
///  <para>
///   This is a structured query, not a stack source, so like the GC-stats
///   provider it returns its own result. Pagination (<c>skip</c> / <c>take</c>)
///   and a per-event payload cap keep the output inside an agent's budget even
///   when a query matches hundreds of thousands of events.
///  </para>
/// </remarks>
public sealed class EventQueryProvider
{
    /// <summary>
    ///  The default maximum number of characters of an event's rendered payload.
    /// </summary>
    public const int DefaultMaxPayloadChars = 200;

    /// <summary>
    ///  Queries events whose <c>Provider/EventName</c> contains <paramref name="nameFilter"/>.
    /// </summary>
    /// <param name="path">The trace file path.</param>
    /// <param name="nameFilter">
    ///  A case-insensitive substring matched against <c>Provider/EventName</c>; empty matches every event.
    /// </param>
    /// <param name="skip">The number of matches to skip (for paging). Must be non-negative.</param>
    /// <param name="take">The maximum number of matches to return. Must be non-negative.</param>
    /// <param name="maxPayloadChars">The per-event payload character cap. Must be non-negative.</param>
    /// <param name="payloadFilter">
    ///  A case-insensitive substring matched against each event's payload <em>values</em>;
    ///  empty applies no payload filter. The full untruncated value is scanned, so a match
    ///  past <paramref name="maxPayloadChars"/> is not missed, and the scan runs only when a
    ///  filter is set, so an unfiltered query never materializes payload values.
    /// </param>
    /// <param name="processId">Keep only events emitted from this OS process id; <see langword="null"/> keeps every process.</param>
    /// <param name="threadId">Keep only events emitted on this OS thread id; <see langword="null"/> keeps every thread.</param>
    /// <returns>The page of matching events, plus the total matched.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A paging or cap argument is negative.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public EventQueryResult Query(
        string path,
        string nameFilter = "",
        int skip = 0,
        int take = 100,
        int maxPayloadChars = DefaultMaxPayloadChars,
        string payloadFilter = "",
        int? processId = null,
        int? threadId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPayloadChars);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        using Etlx.TraceLog traceLog = OpenTrace(fullPath);

        int matched = 0;
        List<EventRecord> page = [];
        foreach (TraceEvent data in traceLog.Events)
        {
            // Only build the qualified name when there is a filter to test it against;
            // an empty filter matches every event, so the allocation would be wasted.
            if (nameFilter.Length > 0
                && !$"{data.ProviderName}/{data.EventName}".Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The process and thread filters are cheap id comparisons, applied before the
            // payload scan so that scan only runs for events that already match.
            if (processId is int pid && data.ProcessID != pid)
            {
                continue;
            }

            if (threadId is int tid && data.ThreadID != tid)
            {
                continue;
            }

            // The payload search scans the full, untruncated values so a match past the
            // output cap is not missed. It is done last, and only when a payload filter is
            // set, so an unfiltered query never materializes payload values.
            if (payloadFilter.Length > 0 && !PayloadMatches(data, payloadFilter))
            {
                continue;
            }

            // Count every match for the total, but only materialize the requested page.
            if (matched >= skip && page.Count < take)
            {
                page.Add(new EventRecord(
                    data.TimeStampRelativeMSec,
                    data.ProviderName,
                    data.EventName,
                    data.ProcessID,
                    data.ThreadID,
                    RenderPayload(data, maxPayloadChars)));
            }

            matched++;
        }

        // Report the number of matches actually skipped, which is fewer than the
        // requested skip when the query matched fewer events than that.
        return new EventQueryResult(matched, Math.Min(skip, matched), page);
    }

    // Opens a trace of either supported format as a TraceLog: an ETW .etl via
    // OpenOrConvert (the ETW -> ETLX conversion is Windows-only), or an EventPipe
    // .nettrace via CreateFromEventPipeDataFile. The event loop over TraceLog.Events is
    // identical for both, so the raw query spans EventPipe and ETW alike.
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

    // Whether any of the event's payload values contains the filter (case-insensitive).
    // Scans the full untruncated value - a match past the output cap must still count -
    // and is called only when a payload filter is set.
    private static bool PayloadMatches(TraceEvent data, string filter)
    {
        string[] names = data.PayloadNames;
        for (int i = 0; i < names.Length; i++)
        {
            if (data.PayloadString(i, null).Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Renders an event's named fields as "name=value; ..." truncated to the cap, so
    // a single huge payload cannot blow the output budget.
    private static string RenderPayload(TraceEvent data, int maxPayloadChars)
    {
        if (maxPayloadChars == 0)
        {
            return "";
        }

        string[] names = data.PayloadNames;
        if (names.Length == 0)
        {
            return "";
        }

        // Append at most maxPayloadChars characters in total, so a single very large
        // payload value cannot grow the builder far past the cap before it is
        // truncated (the result is naturally already within the cap).
        System.Text.StringBuilder builder = new();
        for (int i = 0; i < names.Length; i++)
        {
            if (builder.Length >= maxPayloadChars)
            {
                break;
            }

            if (builder.Length > 0)
            {
                AppendCapped(builder, "; ", maxPayloadChars);
            }

            AppendCapped(builder, names[i], maxPayloadChars);
            AppendCapped(builder, "=", maxPayloadChars);

            // Skip materializing the (possibly very large) value when the name has
            // already filled the cap.
            if (builder.Length < maxPayloadChars)
            {
                AppendCapped(builder, data.PayloadString(i, null), maxPayloadChars);
            }
        }

        return builder.ToString();
    }

    // Appends at most (cap - builder.Length) characters of value, so the builder
    // never grows past the cap even when a single value is degenerately large.
    internal static void AppendCapped(System.Text.StringBuilder builder, string value, int cap)
    {
        int remaining = cap - builder.Length;
        if (remaining <= 0)
        {
            return;
        }

        builder.Append(value, 0, Math.Min(value.Length, remaining));
    }
}
