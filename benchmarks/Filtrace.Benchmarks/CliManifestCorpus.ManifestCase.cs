// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal sealed partial class CliManifestCorpus
{
    /// <summary>
    ///  Defines one serialized benchmark manifest case.
    /// </summary>
    private sealed record ManifestCase(
        string Id,
        string Benchmark,
        string Parameters,
        string BenchmarkDisplay,
        string Trace);
}