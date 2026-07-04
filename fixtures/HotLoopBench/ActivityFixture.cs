// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace TraceQ.Fixtures.HotLoopBench;

/// <summary>
///  A custom <see cref="System.Diagnostics.Tracing.EventSource"/> that emits paired
///  <c>{Name}Start</c> / <c>{Name}Stop</c> events, so a capture carries the start-stop
///  activities the activity metric ranks and scopes to.
/// </summary>
/// <remarks>
///  <para>
///   The events follow the runtime's Start/Stop naming convention (a <c>Start</c> /
///   <c>Stop</c> suffix over a shared task prefix), so EventSource auto-assigns the
///   Start/Stop opcodes and tracks a per-pair activity id - exactly the shape
///   TraceEvent's <c>StartStopActivityComputer</c> pairs into a named activity. Three
///   distinct tasks with descending per-round durations (Order &gt; Query &gt; Render)
///   give the activity ranking a clear, reproducible order.
///  </para>
/// </remarks>
[System.Diagnostics.Tracing.EventSource(Name = "Filtrace-ActivityBench")]
internal sealed class ActivityBenchEventSource : System.Diagnostics.Tracing.EventSource
{
    /// <summary>The singleton logger.</summary>
    public static readonly ActivityBenchEventSource Log = new();

    /// <summary>Starts an <c>Order</c> activity.</summary>
    [System.Diagnostics.Tracing.Event(1)]
    public void OrderStart() => WriteEvent(1);

    /// <summary>Stops an <c>Order</c> activity.</summary>
    [System.Diagnostics.Tracing.Event(2)]
    public void OrderStop() => WriteEvent(2);

    /// <summary>Starts a <c>Query</c> activity.</summary>
    [System.Diagnostics.Tracing.Event(3)]
    public void QueryStart() => WriteEvent(3);

    /// <summary>Stops a <c>Query</c> activity.</summary>
    [System.Diagnostics.Tracing.Event(4)]
    public void QueryStop() => WriteEvent(4);

    /// <summary>Starts a <c>Render</c> activity.</summary>
    [System.Diagnostics.Tracing.Event(5)]
    public void RenderStart() => WriteEvent(5);

    /// <summary>Stops a <c>Render</c> activity.</summary>
    [System.Diagnostics.Tracing.Event(6)]
    public void RenderStop() => WriteEvent(6);
}

/// <summary>
///  The BenchmarkDotNet configuration for the activity EventPipe capture: a net10
///  Monitoring job whose capture enables the custom
///  <see cref="ActivityBenchEventSource"/> provider alongside the CpuSampling profile.
/// </summary>
/// <remarks>
///  <para>
///   The start-stop activity events ride the custom <c>Filtrace-ActivityBench</c>
///   provider, which is not in any default set, so this config adds it (at Verbose,
///   all keywords) on top of the CpuSampling profile. The profile still supplies the
///   CLR method rundown for symbol resolution; the extra provider only adds the
///   activity events the activity metric reads.
///  </para>
/// </remarks>
internal sealed class ActivityCaptureConfig : ManualConfig
{
    public ActivityCaptureConfig()
    {
        AddJob(Job.Default
            .WithRuntime(CoreRuntime.Core10_0)
            .WithStrategy(RunStrategy.Monitoring)
            .WithLaunchCount(1)
            .WithWarmupCount(0)
            .WithIterationCount(1)
            .WithInvocationCount(1));

        Microsoft.Diagnostics.NETCore.Client.EventPipeProvider activities = new(
            "Filtrace-ActivityBench",
            System.Diagnostics.Tracing.EventLevel.Verbose,
            unchecked((long)0xFFFFFFFFFFFFFFFF));

        AddDiagnoser(new EventPipeProfiler(
            EventPipeProfile.CpuSampling,
            new[] { activities },
            performExtraBenchmarksRun: false));
    }
}

/// <summary>
///  A loop that emits nested start-stop activities (Order, Query, Render) with
///  descending durations, captured with the <see cref="ActivityBenchEventSource"/>
///  provider enabled (see <see cref="ActivityCaptureConfig"/>), so its trace carries
///  the paired activity events the activity metric ranks by cumulative duration.
/// </summary>
/// <remarks>
///  <para>
///   Each round runs one <c>Order</c> activity that nests a <c>Query</c> and a
///   <c>Render</c> inside it, so the trace exercises both distinct activity names and
///   the parent / child nesting the computer tracks. Durations come from a short
///   bounded sleep so the wall-clock split (Order longest, Render shortest) is stable
///   and the committed smoke trace stays small.
///  </para>
/// </remarks>
[Config(typeof(ActivityCaptureConfig))]
public class ActivityLoop
{
    /// <summary>
    ///  The benchmarked entry point: emits many nested start-stop activities.
    /// </summary>
    /// <returns>The accumulated sleep budget, returned so it is not elided.</returns>
    [Benchmark]
    public long RunActivities() => EmitActivities(rounds: 40);

    private long EmitActivities(int rounds)
    {
        long total = 0;
        for (int i = 0; i < rounds; i++)
        {
            ActivityBenchEventSource.Log.OrderStart();
            total += Work(1);

            ActivityBenchEventSource.Log.QueryStart();
            total += Work(2);
            ActivityBenchEventSource.Log.QueryStop();

            ActivityBenchEventSource.Log.RenderStart();
            total += Work(1);
            ActivityBenchEventSource.Log.RenderStop();

            total += Work(1);
            ActivityBenchEventSource.Log.OrderStop();
        }

        return total;
    }

    // A short, bounded sleep so each activity has a real, deterministic wall-clock
    // duration. Non-inlined so a CPU sample landing here still attributes to the
    // enclosing activity.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long Work(int milliseconds)
    {
        System.Threading.Thread.Sleep(milliseconds);
        return milliseconds;
    }
}

/// <summary>
///  Runs the start-stop activity computers over a trace and prints the activities
///  found, grouped by name with a count and total duration.
/// </summary>
/// <remarks>
///  <para>
///   This is the exact computer chain the real <c>ActivityProvider</c> uses, so it
///   doubles as a check that a capture carries usable start-stop activities before the
///   fixture is committed. Invoked by the <c>activities &lt;trace&gt;</c> verb.
///  </para>
/// </remarks>
internal static class ActivityInspector
{
    /// <summary>
    ///  Prints the start-stop activities in <paramref name="tracePath"/>.
    /// </summary>
    /// <param name="tracePath">A <c>.nettrace</c> (or <c>.etl</c>) capture path.</param>
    /// <returns>A process exit code: 0 on success.</returns>
    public static int Run(string tracePath)
    {
        if (!System.IO.File.Exists(tracePath))
        {
            Console.Error.WriteLine($"Trace not found: {tracePath}");
            return 1;
        }

        string etlxPath = TraceLog.CreateFromEventPipeDataFile(
            tracePath,
            null,
            new TraceLogOptions { ContinueOnError = true });

        using TraceLog traceLog = new(etlxPath);
        TraceLogEventSource source = traceLog.Events.GetSource();

        Microsoft.Diagnostics.Symbols.SymbolReader symbolReader = new(System.IO.TextWriter.Null);
        GCReferenceComputer gcReferences = new(source);
        ActivityComputer activityComputer = new(source, symbolReader, gcReferences);
        StartStopActivityComputer startStop = new(source, activityComputer, false);

        Dictionary<string, (int Count, double TotalMs)> byName = new(StringComparer.Ordinal);
        startStop.Stop += (activity, _) =>
        {
            // Group by the clean task name (Order / Query / Render), not the per-instance
            // activity Name (which embeds a unique activity path), so the summary reflects
            // activity types the way the activity metric ranks them.
            (int Count, double TotalMs) tally = byName.TryGetValue(activity.TaskName, out (int Count, double TotalMs) existing)
                ? existing
                : (0, 0.0);
            byName[activity.TaskName] = (tally.Count + 1, tally.TotalMs + activity.DurationMSec);
        };

        source.Process();

        Console.WriteLine($"Activities in {tracePath}:");
        foreach (KeyValuePair<string, (int Count, double TotalMs)> entry in byName.OrderByDescending(e => e.Value.TotalMs))
        {
            Console.WriteLine($"  {entry.Value.TotalMs,10:N2} ms  x{entry.Value.Count,-4} {entry.Key}");
        }

        Console.WriteLine($"  ({byName.Count} distinct activities)");
        return 0;
    }
}
