// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Shared reader for the TraceEvent latency computers that pair Start/Stop events on
///  a thread - contention (<see cref="ContentionLatencyComputer"/>) and wait-handle
///  waits (<see cref="WaitHandleWaitLatencyComputer"/>) - turning a computer's output
///  into the weighted <see cref="SampleStack"/>s the engine ranks.
/// </summary>
/// <remarks>
///  <para>
///   Every <see cref="StartStopLatencyComputer"/> weights each Start/Stop pair by the
///   blocked duration in milliseconds and builds the stack from the Start event: it is
///   rooted at the trace's process and thread pseudo-frames, and the computer pushes
///   synthetic <c>EventData &lt;name&gt; &lt;value&gt;</c> leaves (lock id, wait source,
///   duration, ...) that vary per event. This reader drops those synthetic leaves,
///   TraceEvent's <c>BROKEN</c> stack markers (a broken pair, or a stack that could not
///   be fully walked), and the process / thread roots, so the leaf is the real blocking
///   call site and per-event data does not fragment the ranking (the same frames
///   TraceEvent's <c>GetDefaultFoldPatterns</c> folds away). The result is the same
///   {stack, weight} shape as the CPU sampler, differing only in its
///   <see cref="MetricInfo"/>, so the existing <see cref="FoldingAggregator"/> ranks it
///   unchanged.
///  </para>
/// </remarks>
internal static class LatencyStackReader
{
    private static readonly Regex s_processFrame =
        new(@"^Process\d*\s+.+\s+\(\d+\)", RegexOptions.Compiled);

    private static readonly Regex s_threadFrame =
        new(@"^Thread\s+\(\d+\)", RegexOptions.Compiled);

    /// <summary>
    ///  Runs a start/stop latency computer over the EventPipe trace at
    ///  <paramref name="path"/> and returns its weighted, cleaned stacks.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <param name="metric">The metric the computer's weights are measured in.</param>
    /// <param name="createComputer">
    ///  Builds the concrete latency computer over the trace's <see cref="TraceLog"/> and
    ///  a <see cref="MutableTraceEventStackSource"/> (for example a
    ///  <see cref="ContentionLatencyComputer"/> or a
    ///  <see cref="WaitHandleWaitLatencyComputer"/>).
    /// </param>
    /// <returns>The latency source: blocked-millisecond-weighted stacks.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static StackSampleSource Read(
        string path,
        MetricInfo metric,
        Func<TraceLog, MutableTraceEventStackSource, StartStopLatencyComputer> createComputer)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        string etlxPath = TraceLog.CreateFromEventPipeDataFile(
            fullPath,
            null,
            new TraceLogOptions { ContinueOnError = true });

        using TraceLog traceLog = new(etlxPath);

        MutableTraceEventStackSource stackSource = new(traceLog);
        StartStopLatencyComputer computer = createComputer(traceLog, stackSource);
        computer.GenerateStacks();

        List<SampleStack> samples = [];
        List<string> leafToRoot = [];

        stackSource.ForEach(sample =>
        {
            // The computer weights each pair by the blocked duration in milliseconds; a
            // non-positive weight is a broken or zero-length pair.
            double weight = sample.Metric;
            if (weight <= 0.0)
            {
                return;
            }

            leafToRoot.Clear();
            for (StackSourceCallStackIndex index = sample.StackIndex;
                index != StackSourceCallStackIndex.Invalid;
                index = stackSource.GetCallerIndex(index))
            {
                StackSourceFrameIndex frameIndex = stackSource.GetFrameIndex(index);
                string frame = stackSource.GetFrameName(frameIndex, false);

                // Drop the synthetic per-event leaves, TraceEvent's BROKEN stack markers,
                // and the process / thread roots so the stack is the real blocking call
                // path only, leafed at the site that blocked.
                if (frame.StartsWith("EventData ", StringComparison.Ordinal)
                    || frame.StartsWith("BROKEN", StringComparison.Ordinal)
                    || s_threadFrame.IsMatch(frame)
                    || s_processFrame.IsMatch(frame))
                {
                    continue;
                }

                leafToRoot.Add(frame);
            }

            if (leafToRoot.Count == 0)
            {
                return;
            }

            // SampleStack frames are ordered outermost-first, so reverse the leaf-to-root
            // walk into root-to-leaf.
            int count = leafToRoot.Count;
            string[] frames = new string[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = leafToRoot[count - 1 - i];
            }

            samples.Add(new SampleStack(frames, weight));
        });

        return new StackSampleSource(metric, samples);
    }
}
