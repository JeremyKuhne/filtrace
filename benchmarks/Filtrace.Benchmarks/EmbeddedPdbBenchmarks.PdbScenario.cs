// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

public partial class EmbeddedPdbBenchmarks
{
    /// <summary>
    ///  Defines one controlled symbol-directory size and exact embedded-PDB hit rate.
    /// </summary>
    /// <param name="Name">The stable parameter label exposed to BenchmarkDotNet.</param>
    /// <param name="DllCount">The number of equal-sized assemblies placed in the directory.</param>
    /// <param name="HitRatePercent">The percentage of assemblies whose debug data is embedded.</param>
    private sealed record PdbScenario(string Name, int DllCount, int HitRatePercent);
}
