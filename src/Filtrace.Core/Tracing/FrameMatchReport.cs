// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The distinct frame definitions and stack depths matched by a substring selector.
/// </summary>
/// <param name="Selector">The substring used to match full frame names.</param>
/// <param name="Selection">The rule used to select one matching frame per stack.</param>
/// <param name="MatchingStackCount">The number of sample stacks containing at least one match.</param>
/// <param name="Matches">Every distinct matching full frame definition.</param>
public sealed record FrameMatchReport(
    string Selector,
    FrameMatchSelection Selection,
    int MatchingStackCount,
    IReadOnlyList<FrameMatch> Matches)
{
    /// <summary>
    ///  Whether the selector matched multiple definitions or one definition at
    ///  multiple stack depths.
    /// </summary>
    public bool IsAmbiguous =>
        Matches.Count > 1 || Matches.Any(static match => match.Depths.Count > 1);
}