// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Selects whether a timeline request returns aligned buckets or a bounded detail
///  snapshot around one trace-relative timestamp.
/// </summary>
public enum TimelineMode
{
    /// <summary>
    ///  Return aligned activity buckets over a time range.
    /// </summary>
    Buckets,

    /// <summary>
    ///  Return bounded cross-lane evidence around one timestamp.
    /// </summary>
    Snapshot
}
