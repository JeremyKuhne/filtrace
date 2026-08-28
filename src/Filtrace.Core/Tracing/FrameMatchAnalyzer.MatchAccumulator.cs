// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

public static partial class FrameMatchAnalyzer
{
    /// <summary>
    ///  Accumulates stack evidence for one matching frame definition.
    /// </summary>
    private sealed class MatchAccumulator(string frame)
    {
        public string Frame { get; } = frame;

        public HashSet<int> Depths { get; } = [];

        public int MatchingStackCount { get; set; }

        public int SelectedStackCount { get; set; }
    }
}