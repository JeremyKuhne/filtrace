// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The thread-pool provider: reads the runtime's worker-thread adjustment events
///  from a .NET EventPipe trace into a <see cref="ThreadPoolResult"/>, surfacing how
///  often the pool grew because it detected starvation.
/// </summary>
/// <remarks>
///  <para>
///   The runtime's thread-pool hill-climbing heuristic emits a
///   <c>ThreadPoolWorkerThreadAdjustment/Adjustment</c> event whenever it changes the
///   worker-thread count, carrying the new count and the reason (Warmup, ClimbingMove,
///   Starvation, ...). These ride the <c>Threading</c> keyword, which is in the default
///   EventPipe keyword set, so a standard CPU-sampling capture records them - no special
///   keyword or elevation required. This provider tallies the adjustments by reason and
///   tracks the worker-thread range, so a burst of <c>Starvation</c> adjustments (the
///   pool injecting threads because queued work is not completing) is visible at a
///   glance.
///  </para>
/// </remarks>
public sealed class ThreadPoolProvider
{
    /// <summary>
    ///  Reads the thread-pool report from the EventPipe trace at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <returns>The thread-pool report, or an empty report when the trace carries no adjustment events.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public ThreadPoolResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        using TraceLog traceLog = TraceConverter.OpenTraceLog(fullPath, out _);

        Dictionary<string, int> reasonCounts = new(StringComparer.Ordinal);
        int adjustmentCount = 0;
        int starvationCount = 0;
        int minWorker = 0;
        int maxWorker = 0;
        bool anyWorker = false;
        int configuredMin = 0;
        int configuredMax = 0;

        foreach (TraceEvent data in traceLog.Events)
        {
            switch (data)
            {
                case ThreadPoolWorkerThreadAdjustmentTraceData adjustment:
                    adjustmentCount++;

                    string reason = adjustment.Reason.ToString();
                    reasonCounts.TryGetValue(reason, out int count);
                    reasonCounts[reason] = count + 1;

                    if (adjustment.Reason == ThreadAdjustmentReason.Starvation)
                    {
                        starvationCount++;
                    }

                    int workers = adjustment.NewWorkerThreadCount;
                    if (!anyWorker)
                    {
                        minWorker = workers;
                        maxWorker = workers;
                        anyWorker = true;
                    }
                    else
                    {
                        minWorker = Math.Min(minWorker, workers);
                        maxWorker = Math.Max(maxWorker, workers);
                    }

                    break;

                case ThreadPoolMinMaxThreadsTraceData minMax:
                    // The last MinMaxThreads event wins; the configured limits do not
                    // change mid-run in practice.
                    configuredMin = minMax.MinWorkerThreads;
                    configuredMax = minMax.MaxWorkerThreads;
                    break;
            }
        }

        // Break down by reason, most frequent first, with a stable secondary order so the
        // report is deterministic.
        List<ThreadPoolAdjustment> byReason =
        [
            .. reasonCounts
                .Select(static pair => new ThreadPoolAdjustment(pair.Key, pair.Value))
                .OrderByDescending(static a => a.Count)
                .ThenBy(static a => a.Reason, StringComparer.Ordinal)
        ];

        return new ThreadPoolResult(
            adjustmentCount,
            starvationCount,
            minWorker,
            maxWorker,
            configuredMin,
            configuredMax,
            byReason);
    }
}
