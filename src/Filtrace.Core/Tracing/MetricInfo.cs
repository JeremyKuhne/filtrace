// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Describes the metric a <see cref="StackSampleSource"/> carries: its display
///  name and the unit its sample weights are measured in.
/// </summary>
/// <remarks>
///  <para>
///   Each investigation family weights its stacks by a different metric - CPU
///   time in milliseconds today, allocation bytes or event counts as later
///   providers land. Threading the metric through the source lets the engine and
///   its renderers stay provider-agnostic instead of assuming milliseconds.
///  </para>
/// </remarks>
/// <param name="Name">The metric's display name (for example <c>CPU</c>).</param>
/// <param name="Unit">The unit the sample weights are measured in (for example <c>ms</c>).</param>
public sealed record MetricInfo(string Name, string Unit)
{
    /// <summary>
    ///  The CPU-time metric: wall-clock milliseconds per sampled call stack. This
    ///  is the metric of the CPU provider.
    /// </summary>
    public static MetricInfo Cpu { get; } = new("CPU", "ms");

    /// <summary>
    ///  The thread-time metric: wall-clock milliseconds per stack, including the
    ///  time a thread spent blocked (not running). This is the metric of the
    ///  thread-time provider, which - unlike CPU sampling - accounts for off-CPU
    ///  time, so a stack's weight reflects elapsed time rather than busy time.
    /// </summary>
    public static MetricInfo ThreadTime { get; } = new("ThreadTime", "ms");

    /// <summary>
    ///  The allocation metric: bytes allocated per <c>GCAllocationTick</c> call
    ///  stack. This is the metric of the allocation provider.
    /// </summary>
    public static MetricInfo Allocations { get; } = new("Allocations", "bytes");

    /// <summary>
    ///  The exceptions metric: one count per exception throw. This is the metric of
    ///  the exceptions provider, which weights each throw-site stack by a single
    ///  count so the engine ranks where exceptions are thrown.
    /// </summary>
    public static MetricInfo Exceptions { get; } = new("Exceptions", "count");

    /// <summary>
    ///  The contention metric: milliseconds a thread spent blocked acquiring a lock,
    ///  per contention call stack. This is the metric of the contention provider,
    ///  which weights each lock-contention site by the time threads waited on it, so
    ///  the engine ranks where lock contention costs the most wall-clock time.
    /// </summary>
    public static MetricInfo Contention { get; } = new("Contention", "ms");

    /// <summary>
    ///  The wait metric: milliseconds a thread spent blocked waiting on a
    ///  synchronization handle (a <c>WaitHandle</c>, <c>SemaphoreSlim</c>,
    ///  <c>Monitor.Wait</c>, ...), per wait call stack. This is the metric of the wait
    ///  provider, which weights each blocking-wait site by the time threads waited on it.
    /// </summary>
    public static MetricInfo Wait { get; } = new("Wait", "ms");

    /// <summary>
    ///  The activity metric: milliseconds each start-stop activity (a request, job, or
    ///  operation) ran, per activity call path. This is the metric of the activity
    ///  provider, which weights each activity by its wall-clock duration and nests it
    ///  under its parent activity, so the engine ranks which activity types cost the most
    ///  wall-clock time.
    /// </summary>
    public static MetricInfo Activity { get; } = new("Activity", "ms");
}
