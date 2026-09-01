// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Retains one unmatched EE suspension and whether it proves GC provenance.
    /// </summary>
    /// <param name="TimestampMs">The suspension timestamp in trace-relative milliseconds.</param>
    /// <param name="IsGc">Whether the suspension reason identifies the pause as garbage collection.</param>
    internal readonly record struct PendingPauseStart(double TimestampMs, bool IsGc);
}
