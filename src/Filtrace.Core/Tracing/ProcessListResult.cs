// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A trace's process inventory: every process that owns CPU samples, ranked by
///  weight, so a multi-process ETW capture can be scoped to the right one before
///  ranking.
/// </summary>
/// <remarks>
///  <para>
///   This is the answer to "who is in this capture?" - the first move on a
///   machine-wide <c>.etl</c> before scoping a ranking with <c>--process</c>. The
///   automatic scope already narrows to the busiest process by sample count, but a
///   capture can hold several meaningful processes; this view lists them so the
///   choice is explicit. Single-process EventPipe and speedscope traces list one
///   process with an empty label.
///  </para>
/// </remarks>
/// <param name="TotalWeight">The whole capture's weight, in the metric's unit (the percent denominator).</param>
/// <param name="TotalSamples">The total number of CPU samples across every process.</param>
/// <param name="Processes">The processes, highest weight first.</param>
public sealed record ProcessListResult(
    double TotalWeight,
    int TotalSamples,
    IReadOnlyList<ProcessSummary> Processes);
