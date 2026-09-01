// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal static partial class FoldingBenchmarkCorpus
{
    /// <summary>
    ///  Defines the independent dimensions used to generate one synthetic stack source.
    /// </summary>
    /// <param name="Name">The stable label encoding the three numeric dimensions.</param>
    /// <param name="SampleCount">The number of weighted stack records to generate.</param>
    /// <param name="StackDepth">The number of frames and source locations in each stack.</param>
    /// <param name="DistinctFrameCount">The exact frame-name cardinality across all stacks.</param>
    private readonly record struct FoldingScenario(
        string Name,
        int SampleCount,
        int StackDepth,
        int DistinctFrameCount);
}
