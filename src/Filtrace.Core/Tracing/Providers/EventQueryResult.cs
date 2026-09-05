// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  A page of events matching an <see cref="EventQueryProvider"/> query, plus the
///  total number matched so a consumer can page through them.
/// </summary>
/// <param name="TotalMatched">The total number of events matching the query across the whole trace.</param>
/// <param name="Skipped">The number of matches skipped before this page.</param>
/// <param name="Events">The events on this page, in trace (time) order.</param>
/// <param name="BudgetTruncated">
///  Whether the page holds fewer events than were requested because the response
///  token budget was reached. Distinguishes a short page caused by the budget from
///  one caused by running out of matches.
/// </param>
public sealed record EventQueryResult(
    int TotalMatched,
    int Skipped,
    IReadOnlyList<EventRecord> Events,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool BudgetTruncated = false);
