// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    // The raw-event lanes returned together from the single event pass; a lane not
    // requested is null. A value type so the "no lanes requested" path allocates nothing.
    private readonly record struct EventLanes(
        IReadOnlyList<CpuBucket>? Cpu,
        IReadOnlyList<ExceptionBucket>? Exceptions,
        IReadOnlyList<AllocBucket>? Alloc,
        IReadOnlyList<JitBucket>? Jit);
}
