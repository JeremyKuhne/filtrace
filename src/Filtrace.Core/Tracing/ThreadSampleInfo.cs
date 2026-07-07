// Copyright (c) 2025 Jeremy W Kuhne
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
