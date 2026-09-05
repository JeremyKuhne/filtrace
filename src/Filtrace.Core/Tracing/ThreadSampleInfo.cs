// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Per-thread sample count within a loaded trace.
/// </summary>
public sealed class ThreadSampleInfo
{
    /// <summary>
    ///  Initializes a new <see cref="ThreadSampleInfo"/>.
    /// </summary>
    /// <param name="thread">The source thread label or synthetic profile identifier.</param>
    /// <param name="sampleCount">The number of normalized samples attributed to the thread.</param>
    public ThreadSampleInfo(string thread, int sampleCount)
    {
        Thread = thread;
        SampleCount = sampleCount;
    }

    /// <summary>
    ///  A label identifying the thread (OS thread id, or a synthetic id for
    ///  speedscope profiles).
    /// </summary>
    public string Thread { get; }

    /// <summary>
    ///  Number of samples attributed to the thread.
    /// </summary>
    public int SampleCount { get; }
}
