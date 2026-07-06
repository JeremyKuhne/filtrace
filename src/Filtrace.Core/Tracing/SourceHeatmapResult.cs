// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A per-line self-time heat map for a single source file: each leaf sample
///  (after folding JIT-helper leaves into their caller) is bucketed by the source
///  line that was executing, ordered by line number for overlaying onto the source.
/// </summary>
/// <remarks>
///  <para>
///   Only samples carrying per-frame source locations contribute; speedscope
///   inputs have none, so a heat map is meaningful only for <c>.nettrace</c> and
///   <c>.etl</c> traces read with local PDBs present.
///  </para>
/// </remarks>
/// <param name="ScopeWeight">Total whole-trace weight, in the metric's unit (the percent denominator).</param>
/// <param name="File">The source file name the lines belong to (no directory).</param>
/// <param name="FileWeight">Self-weight attributed to the file across all its lines, in the metric's unit.</param>
/// <param name="Lines">The hot lines of the file, ordered by line number.</param>
public sealed record SourceHeatmapResult(
    double ScopeWeight,
    string File,
    double FileWeight,
    IReadOnlyList<HeatLine> Lines);
