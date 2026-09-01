// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal sealed partial class CliManifestCorpus
{
    /// <summary>
    ///  Carries the stable identity and isolated trace path serialized for one case.
    /// </summary>
    /// <param name="Id">The manifest-unique case identifier.</param>
    /// <param name="Benchmark">The benchmark method identity used to pair manifests.</param>
    /// <param name="Parameters">The parameter identity used to pair manifests.</param>
    /// <param name="BenchmarkDisplay">The human-readable benchmark and parameter label.</param>
    /// <param name="Trace">The case-relative path to its isolated trace copy.</param>
    private sealed record ManifestCase(
        string Id,
        string Benchmark,
        string Parameters,
        string BenchmarkDisplay,
        string Trace);
}
