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
///   exceptions, contention, wait, and the GC / JIT reports are EventPipe-only; thread
///   time, the runtime-work classification, and the process inventory are ETW-only; a
///   speedscope export carries CPU stacks alone. Whether the specific events are
///   present is a separate question each analysis answers with its own "no &lt;x&gt;
///   events found" warning, since some (for example wait) need a non-default capture
///   keyword.
///  </para>
/// </remarks>
public static class TraceCapabilities
{
    /// <summary>
    ///  The analysis selectors (rank metrics and report verbs) filtrace can produce
    ///  from a trace of <paramref name="format"/>, lowest-level first.
    /// </summary>
    /// <param name="format">The trace's on-disk format.</param>
    /// <returns>The applicable analysis names.</returns>
    public static IReadOnlyList<string> AnalysesFor(TraceFormat format) => format switch
    {
        TraceFormat.Speedscope => ["cpu"],
        TraceFormat.NetTrace => ["cpu", "alloc", "exceptions", "contention", "wait", "gcstats", "jitstats", "events"],
        TraceFormat.Etl => ["cpu", "threadtime", "classify", "processes", "events"],
        _ => []
    };
}
