// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  The validated inputs to a timeline run: which trace to read, the raw time-window
///  and lane selectors (parsed by the executor), the bucket or snapshot geometry,
///  and how to render it.
/// </summary>
/// <remarks>
///  <para>
///   This is the boundary between command-line parsing and the execution in
///   <see cref="TimelineExecutor"/>; keeping it a plain record - with the time and
///   lane selectors still as raw strings - lets the executor be exercised directly in
///   tests without driving the parser, and keeps every parse-and-validate decision in
///   one place.
///  </para>
/// </remarks>
/// <param name="Path">The trace file path.</param>
/// <param name="Mode">Whether to return aligned buckets or a point-in-time snapshot.</param>
/// <param name="AtMs">Snapshot center in milliseconds, or <see langword="null"/> for bucket mode.</param>
/// <param name="SnapshotHalfWindowMs">
///  Milliseconds retained on either side of the snapshot center, or <see langword="null"/> when omitted.
/// </param>
/// <param name="Time">The raw time-window selector (<c>start,end</c> in ms), or empty for the whole trace.</param>
/// <param name="Lanes">The raw comma-separated lane selector, or empty for every lane.</param>
/// <param name="BucketCount">
///  The number of time buckets requested (clamped by the executor), or <see langword="null"/> when omitted.
/// </param>
/// <param name="Process">The raw process-name selector; empty auto-scopes a multi-process .etl to the busiest.</param>
/// <param name="AllProcesses">Whether to read every process instead of auto-scoping to the busiest.</param>
/// <param name="ProcessIds">The exact process ids to scope to, or <see langword="null"/>/empty when not given.</param>
/// <param name="Children">Whether the process scope follows the matched processes' descendants.</param>
/// <param name="Format">The render format.</param>
internal sealed record TimelineRequest(
    string Path,
    TimelineMode Mode,
    double? AtMs,
    double? SnapshotHalfWindowMs,
    string Time,
    string Lanes,
    int? BucketCount,
    string Process,
    bool AllProcesses,
    int[]? ProcessIds,
    Children Children,
    OutputFormat Format);
