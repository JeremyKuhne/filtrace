// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Groups the optional raw-event lanes produced by a single pass over the trace.
    /// </summary>
    /// <param name="Cpu">CPU sample buckets, or <see langword="null"/> when not requested.</param>
    /// <param name="Exceptions">Exception-count buckets, or <see langword="null"/> when not requested.</param>
    /// <param name="Alloc">Allocation buckets, or <see langword="null"/> when not requested.</param>
    /// <param name="Jit">JIT compilation buckets, or <see langword="null"/> when not requested.</param>
    private readonly record struct EventLanes(
        IReadOnlyList<CpuBucket>? Cpu,
        IReadOnlyList<ExceptionBucket>? Exceptions,
        IReadOnlyList<AllocBucket>? Alloc,
        IReadOnlyList<JitBucket>? Jit);
}
