// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  One distinct full frame definition matched by a substring selector.
/// </summary>
/// <param name="Frame">The full frame name, including module and signature when the trace carries them.</param>
/// <param name="Depths">The distinct zero-based stack depths where the frame was observed, outermost first.</param>
/// <param name="MatchingStackCount">The number of sample stacks containing this frame definition.</param>
/// <param name="SelectedStackCount">The number of sample stacks where this definition was selected.</param>
public sealed record FrameMatch(
    string Frame,
    IReadOnlyList<int> Depths,
    int MatchingStackCount,
    int SelectedStackCount);