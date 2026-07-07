// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The validated inputs to a timeline run: which trace to read, the raw time-window
///  and lane selectors (parsed by the executor), the bucket count, and how to render
///  it.
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
/// <param name="Time">The raw time-window selector (<c>start,end</c> in ms), or empty for the whole trace.</param>
/// <param name="Lanes">The raw comma-separated lane selector, or empty for every lane.</param>
/// <param name="BucketCount">The number of time buckets requested (clamped by the executor).</param>
/// <param name="Process">The raw process-name selector; empty auto-scopes a multi-process .etl to the busiest.</param>
/// <param name="AllProcesses">Whether to read every process instead of auto-scoping to the busiest.</param>
/// <param name="Format">The render format.</param>
internal sealed record TimelineRequest(
    string Path,
    string Time,
    string Lanes,
    int BucketCount,
    string Process,
    bool AllProcesses,
    OutputFormat Format);
