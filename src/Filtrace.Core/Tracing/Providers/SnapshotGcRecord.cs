// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One garbage collection retained in a timeline snapshot.
/// </summary>
/// <param name="Number">The collection sequence number.</param>
/// <param name="StartMs">Collection start, in milliseconds from trace start.</param>
/// <param name="Generation">The condemned generation.</param>
/// <param name="Kind">The collection kind.</param>
/// <param name="Reason">Why the collection was triggered.</param>
/// <param name="PauseMs">Full managed-thread pause duration, which may extend outside the snapshot window.</param>
public sealed record SnapshotGcRecord(
    int Number,
    double StartMs,
    int Generation,
    string Kind,
    string Reason,
    double PauseMs);
