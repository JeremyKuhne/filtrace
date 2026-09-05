// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Selects the filtrace command shape and prerequisite corpus used by a CLI benchmark.
/// </summary>
internal enum CliScenarioOperation
{
    /// <summary>
    ///  Loads trace metadata and provider availability without running a ranking.
    /// </summary>
    Info,

    /// <summary>
    ///  Ranks folded leaf weight for the CPU metric.
    /// </summary>
    RankSelf,

    /// <summary>
    ///  Ranks every stack frame by inclusive CPU weight.
    /// </summary>
    RankInclusive,

    /// <summary>
    ///  Ranks CPU weight retained inside the fixture's named activity.
    /// </summary>
    RankActivity,

    /// <summary>
    ///  Ranks every case in one generated manifest.
    /// </summary>
    Batch,

    /// <summary>
    ///  Compares paired baseline and current manifests.
    /// </summary>
    Diff,

    /// <summary>
    ///  Loads trace metadata while scanning a controlled local symbol directory.
    /// </summary>
    Symbols
}
