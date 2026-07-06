// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing.Computers;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The wait stack-source provider: reconstructs where managed threads block waiting
///  on a synchronization handle from a .NET EventPipe trace, weighting each wait by
///  the time the thread spent blocked, so the engine ranks blocking-wait hotspots the
///  same way it ranks CPU time or contention.
/// </summary>
/// <remarks>
///  <para>
///   The runtime emits a <c>WaitHandleWait/Start</c> event (carrying the blocking
///   call stack) when a thread begins waiting on a wait handle - a
///   <c>WaitHandle</c> (<c>ManualResetEvent</c>, <c>AutoResetEvent</c>, ...), a
///   <c>SemaphoreSlim</c>, a <c>Monitor.Wait</c>, a blocking <c>Task.Wait</c> - and a
///   <c>WaitHandleWait/Stop</c> event when the wait ends. TraceEvent's
///   <see cref="WaitHandleWaitLatencyComputer"/> pairs the two per thread and weights
///   the start stack by the blocked duration in milliseconds;
///   <see cref="LatencyStackReader"/> turns that into the same {stack, weight} shape
///   as the CPU sampler - only the metric (<see cref="MetricInfo.Wait"/>) differs - so
///   the existing <see cref="FoldingAggregator"/> ranks it unchanged. It answers "what
///   is my thread waiting on?" cross-platform from an EventPipe trace, with no ETW.
///  </para>
///  <para>
///   The <c>WaitHandleWait</c> events are a .NET 9+ feature and ride the
///   <c>WaitHandle</c> keyword, which is not in the default EventPipe keyword set, so
///   the trace must be captured with that keyword explicitly enabled (the fixture
///   capture does; see <c>make-fixtures.ps1</c>). This is a provider, not a format
///   reader: it is a different view of the same <c>.nettrace</c> the CPU reader
///   consumes, so it does not implement <c>ITraceReader</c> (which dispatches by file
///   extension).
///  </para>
/// </remarks>
public sealed class WaitProvider
{
    /// <summary>
    ///  Reads the wait stack-sample source from the EventPipe trace at
    ///  <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <param name="window">
    ///  Optional time window; when set, only waits whose start falls inside it are
    ///  read. <see langword="null"/> reads the whole trace.
    /// </param>
    /// <returns>The wait source: blocked-millisecond-weighted wait stacks.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public StackSampleSource Read(string path, TimeWindow? window = null) =>
        LatencyStackReader.Read(
            path,
            MetricInfo.Wait,
            static (traceLog, stackSource) => new WaitHandleWaitLatencyComputer(traceLog, stackSource),
            window);
}
