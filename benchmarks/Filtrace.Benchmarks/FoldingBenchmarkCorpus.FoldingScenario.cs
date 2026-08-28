// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal static partial class FoldingBenchmarkCorpus
{
    /// <summary>
    ///  Defines the dimensions of one synthetic folding workload.
    /// </summary>
    private readonly record struct FoldingScenario(
        string Name,
        int SampleCount,
        int StackDepth,
        int DistinctFrameCount);
}