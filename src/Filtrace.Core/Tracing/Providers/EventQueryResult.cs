// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  A page of events matching an <see cref="EventQueryProvider"/> query, plus the
///  total number matched so a consumer can page through them.
/// </summary>
/// <param name="TotalMatched">The total number of events matching the query across the whole trace.</param>
/// <param name="Skipped">The number of matches skipped before this page.</param>
/// <param name="Events">The events on this page, in trace (time) order.</param>
public sealed record EventQueryResult(
    int TotalMatched,
    int Skipped,
    IReadOnlyList<EventRecord> Events);
