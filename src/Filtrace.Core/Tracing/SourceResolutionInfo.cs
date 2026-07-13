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
    ///  Fraction in <c>[0, 1]</c> of sampled managed frame occurrences mapped to a
    ///  source line.
    /// </summary>
    public double SourceResolutionRate => SampledManagedFrameCount > 0
        ? (double)MappedManagedFrameCount / SampledManagedFrameCount
        : 0.0;
}