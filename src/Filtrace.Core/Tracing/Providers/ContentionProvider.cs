// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing.Computers;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The contention stack-source provider: reconstructs where managed threads block
///  acquiring locks from a .NET EventPipe trace, weighting each contention by the
///  time the thread spent blocked, so the engine ranks lock-contention hotspots the
///  same way it ranks CPU time or allocation.
/// </summary>
/// <remarks>
///  <para>
///   The runtime emits a <c>Contention/Start</c> event (carrying the blocking call
///   stack) when a thread begins waiting to enter a monitor, and a
///   <c>Contention/Stop</c> event (carrying the duration) when it finally acquires
///   the lock. TraceEvent's <see cref="ContentionLatencyComputer"/> pairs the two
///   per thread and weights the start stack by the blocked duration in milliseconds;
///   <see cref="LatencyStackReader"/> turns that into the same {stack, weight} shape
///   as the CPU sampler - only the metric (<see cref="MetricInfo.Contention"/>)
///   differs - so the existing <see cref="FoldingAggregator"/> ranks it unchanged.
///   The lock sites threads wait on longest rise to the top of the ranking.
///  </para>
///  <para>
///   This is a provider, not a format reader: it is a different view of the same
///   <c>.nettrace</c> the CPU reader consumes, so it does not implement
///   <c>ITraceReader</c> (which dispatches by file extension). Contention events are
///   part of the default EventPipe keyword set, so a standard CPU-sampling capture
///   carries them and no elevation is required.
///  </para>
/// </remarks>
public sealed class ContentionProvider
{
    /// <summary>
    ///  Reads the contention stack-sample source from the EventPipe trace at
    ///  <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <param name="window">
    ///  Optional time window; when set, only contentions whose start falls inside it
    ///  are read. <see langword="null"/> reads the whole trace.
    /// </param>
    /// <returns>The contention source: blocked-millisecond-weighted contention stacks.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public StackSampleSource Read(string path, TimeWindow? window = null) =>
        LatencyStackReader.Read(
            path,
            MetricInfo.Contention,
            static (traceLog, stackSource) => new ContentionLatencyComputer(traceLog, stackSource),
            window);
}
