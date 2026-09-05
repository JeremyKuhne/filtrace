// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using FastTrace;
using FastTrace.Etlx;
using FastTrace.Parsers.Clr;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The allocation stack-source provider: reads the <c>GCAllocationTick</c>
///  events from a .NET EventPipe trace into stacks weighted by bytes allocated,
///  so the engine can rank allocation by call site exactly as it ranks CPU time.
/// </summary>
/// <remarks>
///  <para>
///   The runtime emits a <c>GCAllocationTick</c> roughly every 100 KB allocated,
///   carrying the allocating call stack and the byte amount since the previous
///   tick. Weighting each stack by that amount yields an allocation profile in
///   the same {stack, weight} shape as the CPU sampler, so the existing
///   <see cref="FoldingAggregator"/> ranks it without change - only the metric
///   (<see cref="MetricInfo.Allocations"/>, measured in bytes) differs.
///  </para>
///  <para>
///   This is a provider, not a format reader: it is a different view of the same
///   <c>.nettrace</c> the CPU reader consumes, so it does not implement
///   <c>ITraceReader</c> (which dispatches by file extension).
///  </para>
/// </remarks>
public sealed class AllocationProvider
{
    /// <summary>
    ///  Reads the allocation stack-sample source from the EventPipe trace at
    ///  <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <param name="window">
    ///  Optional time window; when set, only allocation events whose timestamp falls
    ///  inside it are read. <see langword="null"/> reads the whole trace.
    /// </param>
    /// <returns>The allocation source: byte-weighted allocation-site stacks.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public StackSampleSource Read(string path, TimeWindow? window = null) =>
        Read(path, window, out _);

    /// <summary>
    ///  Reads byte-weighted allocation stacks and reports the number of positive allocation ticks encountered.
    /// </summary>
    /// <param name="path">The EventPipe trace path.</param>
    /// <param name="window">An optional trace-relative event-time filter.</param>
    /// <param name="recordCount">
    ///  The number of positive allocation tick records encountered before output filtering.
    /// </param>
    /// <returns>The allocation stack source retained by the requested window.</returns>
    internal StackSampleSource Read(string path, TimeWindow? window, out int recordCount)
    {
        return Read(path, window, out recordCount, out _, cancellationToken: default);
    }

    /// <inheritdoc cref="Read(string, TimeWindow?, out int)"/>
    internal StackSampleSource Read(
        string path,
        TimeWindow? window,
        out int recordCount,
        out EtlxCacheState cacheState,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        using TraceLog traceLog = TraceConverter.OpenTraceLog(fullPath, out cacheState, cancellationToken);

        List<SampleStack> samples = [];
        List<string> leafToRoot = [];
        int captureRecordCount = 0;

        foreach (TraceEvent data in traceLog.Events)
        {
            if (data is not GCAllocationTickTraceData alloc)
            {
                continue;
            }

            long bytes = alloc.AllocationAmount64;
            if (bytes <= 0)
            {
                continue;
            }

            captureRecordCount++;

            // When scoped to a time window, drop allocation ticks outside it; every event
            // carries a trace-relative timestamp, so the same guard scopes every metric.
            if (window is TimeWindow scope && !scope.Contains(data.TimeStampRelativeMSec))
            {
                continue;
            }

            TraceCallStack? callStack = data.CallStack();
            if (callStack is null)
            {
                continue;
            }

            leafToRoot.Clear();
            for (TraceCallStack? frame = callStack; frame is not null; frame = frame.Caller)
            {
                leafToRoot.Add(QualifyFrame(frame.CodeAddress));
            }

            if (leafToRoot.Count == 0)
            {
                continue;
            }

            int count = leafToRoot.Count;
            string[] frames = new string[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = leafToRoot[count - 1 - i];
            }

            samples.Add(new SampleStack(frames, bytes, data.ThreadID.ToString()));
        }

        recordCount = captureRecordCount;
        return new StackSampleSource(MetricInfo.Allocations, samples);
    }

    // Builds the "module!Method(sig)" frame name the aggregator and FrameNames.Short
    // expect, matching how the CPU reader names frames so folding stays consistent.
    private static string QualifyFrame(TraceCodeAddress address)
    {
        string method = address.FullMethodName;
        string module = address.ModuleName;
        if (string.IsNullOrEmpty(method))
        {
            return $"{(string.IsNullOrEmpty(module) ? "?" : module)}!?";
        }

        return string.IsNullOrEmpty(module) ? method : $"{module}!{method}";
    }
}
