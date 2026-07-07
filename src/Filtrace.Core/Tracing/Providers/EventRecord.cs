// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One event in an <see cref="EventQueryResult"/>.
/// </summary>
/// <param name="TimestampMs">The event time, in milliseconds relative to the start of the trace.</param>
/// <param name="Provider">The ETW / EventPipe provider that emitted the event.</param>
/// <param name="EventName">The event name.</param>
/// <param name="ThreadId">The OS thread the event was emitted on.</param>
/// <param name="Payload">The event's named fields rendered compactly, truncated to the query's payload cap.</param>
public sealed record EventRecord(
    double TimestampMs,
    string Provider,
    string EventName,
    int ThreadId,
    string Payload);
