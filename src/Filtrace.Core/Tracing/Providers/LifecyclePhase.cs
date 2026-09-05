// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One wall-clock phase summarized across every invocation that contributed a value:
///  the median, the extremes, and how many invocations were measured.
/// </summary>
/// <remarks>
///  <para>
///   A short command is dominated by run-to-run variance, so a single invocation says
///   little. The median is the value to reason about; the minimum and maximum bound
///   how much the phase moved across the run.
///  </para>
///  <para>
///   <see cref="MedianMs"/> is a true p50: for an even count it is the mean of the two
///   middle values. Every value is wall-clock time, not sampled CPU time.
///  </para>
/// </remarks>
/// <param name="Phase">The phase name.</param>
/// <param name="Count">How many invocations contributed a value.</param>
/// <param name="MedianMs">The median value, in milliseconds.</param>
/// <param name="MinimumMs">The smallest value, in milliseconds.</param>
/// <param name="MaximumMs">The largest value, in milliseconds.</param>
public sealed record LifecyclePhase(
    string Phase,
    int Count,
    double MedianMs,
    double MinimumMs,
    double MaximumMs);
