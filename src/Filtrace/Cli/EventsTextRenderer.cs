// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Renders an events-query result as the dense, fixed-width text view a human
///  reads at the terminal: a header naming the filter and page, the matched events
///  in aligned columns, then any steering hint toward the next page.
/// </summary>
/// <remarks>
///  <para>
///   This is the text half of the events query; the JSON half is
///   <see cref="OutputJson"/>. Both render the same <see cref="AnalysisResult{T}"/>
///   envelope.
///  </para>
/// </remarks>
internal static class EventsTextRenderer
{
    private const int QualifiedNameWidth = 44;

    /// <summary>
    ///  Renders the events envelope to <paramref name="output"/>.
    /// </summary>
    /// <param name="envelope">The events page, with its steering hints.</param>
    /// <param name="request">The query the page answers, so the header reflects every active filter.</param>
    /// <param name="output">The writer the text is rendered to.</param>
    public static void Render(AnalysisResult<EventQueryResult> envelope, EventsRequest request, TextWriter output)
    {
        EventQueryResult result = envelope.Result;

        output.WriteLine($"events  -  {request.Path}  ({DescribeFilters(request)})");
        output.WriteLine();

        if (result.Events.Count == 0)
        {
            output.WriteLine(
                result.TotalMatched == 0
                    ? "  (no events matched)"
                    : $"  (no events on this page; {result.TotalMatched} matched - lower --skip)");
            RenderHints(envelope, output);
            return;
        }

        int from = result.Skipped + 1;
        int through = result.Skipped + result.Events.Count;
        output.WriteLine($"  {result.TotalMatched} matched   showing {from}-{through}");
        output.WriteLine();

        output.WriteLine($"  {"time(ms)",12}  {"proc",6}  {"thread",6}  {"provider / event",-QualifiedNameWidth}  payload");
        foreach (EventRecord e in result.Events)
        {
            string qualified = $"{e.Provider}/{e.EventName}";
            output.WriteLine(
                $"  {e.TimestampMs,12:N2}  {e.ProcessId,6}  {e.ThreadId,6}  {qualified,-QualifiedNameWidth}  {e.Payload}");
        }

        RenderHints(envelope, output);
    }

    // A human-readable summary of the query's active filters for the header, so the
    // rendered output reflects the actual inputs rather than only the name filter.
    // "all events" when nothing narrows the query.
    private static string DescribeFilters(EventsRequest request)
    {
        List<string> parts = [];
        if (!string.IsNullOrEmpty(request.Name))
        {
            parts.Add($"name '{request.Name}'");
        }

        if (!string.IsNullOrEmpty(request.Payload))
        {
            parts.Add($"payload '{request.Payload}'");
        }

        if (request.Pid is int pid)
        {
            parts.Add($"pid {pid}");
        }

        if (request.Tid is int tid)
        {
            parts.Add($"tid {tid}");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "all events";
    }

    private static void RenderHints(AnalysisResult<EventQueryResult> envelope, TextWriter output)
    {
        foreach (string hint in envelope.Hints)
        {
            output.WriteLine($"> {hint}");
        }
    }
}
