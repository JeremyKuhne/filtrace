// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The count of worker-thread adjustments the runtime made for one reason, in a
///  <see cref="ThreadPoolResult"/>.
/// </summary>
/// <param name="Reason">The adjustment reason (for example <c>Starvation</c> or <c>ClimbingMove</c>).</param>
/// <param name="Count">How many adjustments the runtime made for that reason.</param>
public sealed record ThreadPoolAdjustment(string Reason, int Count);

/// <summary>
///  The thread-pool report for a trace: how the runtime grew or shrank the worker
///  pool and, above all, how often it did so because it detected <c>Starvation</c> -
///  the signal behind the classic "everything is slow under load but the CPU is idle
///  and requests pile up" hang (typically sync-over-async blocking pool threads).
/// </summary>
/// <remarks>
///  <para>
///   The runtime emits a <c>ThreadPoolWorkerThreadAdjustment/Adjustment</c> event each
///   time its hill-climbing heuristic changes the worker-thread count, carrying the new
///   count and a reason. A run of <c>Starvation</c> adjustments means the pool kept
///   injecting threads because queued work was not completing - the definitive
///   starvation fingerprint. Like the GC and JIT reports this is structured data, not
///   weighted stacks, so it returns its own result rather than a
///   <see cref="StackSampleSource"/>.
///  </para>
/// </remarks>
/// <param name="AdjustmentCount">The total number of worker-thread adjustments.</param>
/// <param name="StarvationCount">The number of adjustments the runtime made because it detected starvation - the headline signal.</param>
/// <param name="MinWorkerThreadCount">The lowest worker-thread count the adjustments settled at.</param>
/// <param name="MaxWorkerThreadCount">The highest worker-thread count the adjustments grew to.</param>
/// <param name="ConfiguredMinWorkerThreads">The configured minimum worker threads (from the last <c>ThreadPoolMinMaxThreads</c> event), or 0 if the trace carried none.</param>
/// <param name="ConfiguredMaxWorkerThreads">The configured maximum worker threads, or 0 if the trace carried none.</param>
/// <param name="AdjustmentsByReason">The adjustment counts broken down by reason, most frequent first.</param>
public sealed record ThreadPoolResult(
    int AdjustmentCount,
    int StarvationCount,
    int MinWorkerThreadCount,
    int MaxWorkerThreadCount,
    int ConfiguredMinWorkerThreads,
    int ConfiguredMaxWorkerThreads,
    IReadOnlyList<ThreadPoolAdjustment> AdjustmentsByReason);
