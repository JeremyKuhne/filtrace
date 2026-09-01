// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

public static partial class FrameMatchAnalyzer
{
    /// <summary>
    ///  Accumulates stack evidence for one matching frame definition.
    /// </summary>
    /// <param name="frame">The full frame definition shared by all accumulated matches.</param>
    private sealed class MatchAccumulator(string frame)
    {
        /// <summary>
        ///  Gets the full frame definition represented by this accumulator.
        /// </summary>
        public string Frame { get; } = frame;

        /// <summary>
        ///  Gets the distinct zero-based stack depths at which the frame appeared.
        /// </summary>
        public HashSet<int> Depths { get; } = [];

        /// <summary>
        ///  Gets or sets the number of source stacks containing this frame definition.
        /// </summary>
        public int MatchingStackCount { get; set; }

        /// <summary>
        ///  Gets or sets the number of stacks for which the requested strategy selected this definition.
        /// </summary>
        public int SelectedStackCount { get; set; }
    }
}
