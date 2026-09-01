// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Identifies one process and thread instance that participates in an EE pause.
    /// </summary>
    /// <param name="ProcessInstanceIndex">The TraceEvent process-instance index.</param>
    /// <param name="ThreadInstanceIndex">The TraceEvent thread-instance index.</param>
    internal readonly record struct PauseIdentity(int ProcessInstanceIndex, int ThreadInstanceIndex);
}
