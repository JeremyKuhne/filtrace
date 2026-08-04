// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics.Tracing;

namespace Filtrace.PerfWorkload;

[EventSource(Name = "Filtrace-TrackD")]
internal sealed class TrackDActivitySource : EventSource
{
    public static readonly TrackDActivitySource Log = new();

    [Event(1)]
    public void OrderStart() => WriteEvent(1);

    [Event(2)]
    public void OrderStop() => WriteEvent(2);

    [Event(3)]
    public void QueryStart() => WriteEvent(3);

    [Event(4)]
    public void QueryStop() => WriteEvent(4);

    [Event(5)]
    public void RenderStart() => WriteEvent(5);

    [Event(6)]
    public void RenderStop() => WriteEvent(6);
}
