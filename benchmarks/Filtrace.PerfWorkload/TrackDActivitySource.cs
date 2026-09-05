// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics.Tracing;

namespace Filtrace.PerfWorkload;

/// <summary>
///  Emits the nested order, query, and render boundaries consumed by activity-scoped trace benchmarks.
/// </summary>
[EventSource(Name = "Filtrace-TrackD")]
internal sealed class TrackDActivitySource : EventSource
{
    /// <summary>
    ///  Gets the process-wide event source used by the workload.
    /// </summary>
    public static readonly TrackDActivitySource Log = new();

    /// <summary>
    ///  Marks the beginning of an order activity.
    /// </summary>
    [Event(1)]
    public void OrderStart() => WriteEvent(1);

    /// <summary>
    ///  Marks the end of the current order activity.
    /// </summary>
    [Event(2)]
    public void OrderStop() => WriteEvent(2);

    /// <summary>
    ///  Marks the beginning of a query nested within an order.
    /// </summary>
    [Event(3)]
    public void QueryStart() => WriteEvent(3);

    /// <summary>
    ///  Marks the end of the current query activity.
    /// </summary>
    [Event(4)]
    public void QueryStop() => WriteEvent(4);

    /// <summary>
    ///  Marks the beginning of rendering nested within an order.
    /// </summary>
    [Event(5)]
    public void RenderStart() => WriteEvent(5);

    /// <summary>
    ///  Marks the end of the current render activity.
    /// </summary>
    [Event(6)]
    public void RenderStop() => WriteEvent(6);
}
