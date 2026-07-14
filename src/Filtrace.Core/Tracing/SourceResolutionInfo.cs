// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Source/PDB quality for sampled managed frames, separate from method-name
///  resolution.
/// </summary>
/// <param name="SearchedDirectories">Caller-supplied symbol directories searched for matching PDBs.</param>
/// <param name="SampledManagedFrameCount">Sampled managed frame occurrences.</param>
/// <param name="MappedManagedFrameCount">Sampled managed frame occurrences mapped to a source line.</param>
/// <param name="MatchingPdbModules">Sampled modules for which a matching PDB was found.</param>
/// <param name="HighestUnmappedModules">Bounded highest-impact modules with unmapped sampled frames.</param>
public sealed record SourceResolutionInfo(
    IReadOnlyList<string> SearchedDirectories,
    int SampledManagedFrameCount,
    int MappedManagedFrameCount,
    IReadOnlyList<string> MatchingPdbModules,
    IReadOnlyList<string> HighestUnmappedModules)
{
    /// <summary>
    ///  Bounded highest-impact sampled modules for which the caller-supplied
    ///  directory contains the expected PDB filename but its GUID or age does not
    ///  match the trace-recorded identity.
    /// </summary>
    public IReadOnlyList<string> PdbIdentityMismatchModules { get; init; } = [];

    /// <summary>
    ///  Unique managed methods observed in sampled stacks, or <see langword="null"/>
    ///  when the count is unavailable, including when method cardinality exceeded
    ///  the diagnostic safety limit.
    /// </summary>
    public int? SampledManagedMethodCount { get; init; }

    /// <summary>
    ///  Unique sampled managed methods for which at least one sampled address
    ///  resolved through a PDB sequence point, or <see langword="null"/> when the
    ///  count is unavailable, including when method cardinality exceeded the
    ///  diagnostic safety limit.
    /// </summary>
    public int? SourceMappedManagedMethodCount { get; init; }

    /// <summary>
    ///  Named managed frame occurrences that did not resolve to a source line.
    /// </summary>
    public int UnmappedNamedManagedFrameCount { get; init; }

    /// <summary>
    ///  Bounded highest-impact named sampled methods with frame occurrences that
    ///  did not resolve to source. Overloads sharing a display name are grouped.
    /// </summary>
    public IReadOnlyList<string> HighestUnmappedMethods { get; init; } = [];

    /// <summary>
    ///  Fraction in <c>[0, 1]</c> of sampled managed frame occurrences mapped to a
    ///  source line.
    /// </summary>
    public double SourceResolutionRate => SampledManagedFrameCount > 0
        ? (double)MappedManagedFrameCount / SampledManagedFrameCount
        : 0.0;
}