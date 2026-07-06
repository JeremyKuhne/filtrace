// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One garbage collection's structured record in a <see cref="GcStatsResult"/>.
/// </summary>
/// <param name="Number">The collection's sequence number within the trace.</param>
/// <param name="Generation">The generation condemned (0, 1, or 2).</param>
/// <param name="Kind">The collection kind (for example <c>Blocking</c> or <c>Background</c>).</param>
/// <param name="Reason">Why the collection was triggered.</param>
/// <param name="PauseMs">How long the managed threads were paused, in milliseconds.</param>
/// <param name="HeapSizeAfterMB">The managed heap size after the collection, in megabytes.</param>
/// <param name="PromotedMB">Memory promoted to an older generation by the collection, in megabytes.</param>
public sealed record GcRecord(
    int Number,
    int Generation,
    string Kind,
    string Reason,
    double PauseMs,
    double HeapSizeAfterMB,
    double PromotedMB);
