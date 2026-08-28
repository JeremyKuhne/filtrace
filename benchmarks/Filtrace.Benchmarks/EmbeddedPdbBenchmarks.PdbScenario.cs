// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

public partial class EmbeddedPdbBenchmarks
{
    /// <summary>
    ///  Defines one controlled embedded-PDB directory scenario.
    /// </summary>
    private sealed record PdbScenario(string Name, int DllCount, int HitRatePercent);
}