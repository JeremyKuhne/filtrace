// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The metric an ETW capture is tuned for.
/// </summary>
public enum CollectMetric
{
    /// <summary>
    ///  CPU sampling (the kernel <c>Default</c> keyword set, whose <c>Profile</c> keyword
    ///  is the sampled profiler). Feeds <c>cpu</c> and <c>classify</c>.
    /// </summary>
    Cpu,

    /// <summary>
    ///  CPU sampling plus the context-switch keywords that carry blocked intervals, so
    ///  wall-clock time can be reconstructed. Feeds <c>threadtime</c> as well as <c>cpu</c>.
    /// </summary>
    ThreadTime,
}
