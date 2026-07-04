// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The activity stack-source provider: reconstructs the trace's start-stop activities
///  - requests, jobs, and operations - from a .NET EventPipe trace, weighting each by
///  its wall-clock duration and nesting it under its parent activity, so the engine
///  ranks which activity types cost the most time the same way it ranks CPU or
///  allocation.
/// </summary>
/// <remarks>
///  <para>
///   An <c>EventSource</c> that emits paired <c>{Name}Start</c> / <c>{Name}Stop</c>
///   events (ASP.NET requests, <c>HttpClient</c> calls, the TPL, or any custom source)
///   makes the runtime track a per-pair activity id. TraceEvent's
///   <see cref="StartStopActivityComputer"/> pairs those into named activities with a
///   duration and a parent (<see cref="StartStopActivity.Creator"/>); this provider
///   turns each completed activity into a duration-weighted stack rooted at its
///   outermost ancestor and leafed at the activity itself, so the same
///   <see cref="FoldingAggregator"/> that ranks CPU time ranks activity time unchanged.
///   The frame at each level is the activity's clean <see cref="StartStopActivity.TaskName"/>
///   (for example <c>Order</c>), so every instance of an activity folds into one row.
///  </para>
///  <para>
///   This is a provider, not a format reader: it is a different view of the same
///   <c>.nettrace</c> the CPU reader consumes, so it does not implement
///   <c>ITraceReader</c>. Because activity durations nest (a parent's duration spans its
///   children's), inclusive time double-counts a parent and its children - the same
///   overlap the contention and wait latency metrics carry - so self time (which
///   activity type spent the most of its own span) is the clearer reading.
///  </para>
/// </remarks>
public sealed class ActivityProvider
{
    /// <summary>
    ///  Reads the activity stack-sample source from the EventPipe trace at
    ///  <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <returns>The activity source: wall-clock-duration-weighted activity stacks.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public StackSampleSource Read(string path)
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
        TraceLogEventSource source = traceLog.Events.GetSource();

        // The activity computer needs a symbol reader and a GC-reference computer to
        // build; neither resolves symbols here (activity names come from the event
        // stream, not from PDBs), so a no-op symbol reader is enough.
        using SymbolReader symbolReader = new(TextWriter.Null, "", null);
        GCReferenceComputer gcReferences = new(source);
        ActivityComputer activityComputer = new(source, symbolReader, gcReferences);
        StartStopActivityComputer startStop = new(source, activityComputer, ignoreApplicationInsightsRequestsWithRelatedActivityId: false);

        List<SampleStack> samples = [];

        // Each completed activity contributes one duration-weighted stack. Stop is a
        // callback field on the computer (not an event), invoked as each Start/Stop pair
        // closes while the source is processed below.
        startStop.Stop += (activity, _) =>
        {
            double weight = activity.DurationMSec;
            if (weight <= 0.0)
            {
                return;
            }

            // Walk the parent (Creator) chain leaf-to-root, naming each level by its clean
            // TaskName so instances of the same activity fold together, then reverse into
            // the root-to-leaf order SampleStack expects.
            List<string> leafToRoot = [];
            for (StartStopActivity? current = activity; current is not null; current = current.Creator)
            {
                leafToRoot.Add(current.TaskName);
            }

            int count = leafToRoot.Count;
            string[] frames = new string[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = leafToRoot[count - 1 - i];
            }

            samples.Add(new SampleStack(frames, weight));
        };

        source.Process();

        return new StackSampleSource(MetricInfo.Activity, samples);
    }
}
