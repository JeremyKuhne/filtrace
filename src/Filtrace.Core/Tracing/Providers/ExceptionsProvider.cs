// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The exceptions stack-source provider: reads the runtime's exception-throw
///  events from a .NET EventPipe trace into stacks weighted by a single count
///  each, so the engine can rank where exceptions are thrown exactly as it ranks
///  CPU time or allocation.
/// </summary>
/// <remarks>
///  <para>
///   The runtime emits an <c>Exception/Start</c> event at each throw, carrying the
///   throwing call stack. Weighting each throw-site stack by one count yields an
///   exception profile in the same {stack, weight} shape as the CPU sampler, so
///   the existing <see cref="FoldingAggregator"/> ranks it without change - only
///   the metric (<see cref="MetricInfo.Exceptions"/>, measured in counts) differs.
///   The thrown exception's type is appended as a synthetic leaf frame, so self-time
///   ranks by exception type (the type thrown most rises to the top) while an
///   inclusive ranking surfaces the throw-site paths. The public callers drill reads
///   CPU stacks only and is not a same-metric exception drill.
///  </para>
///  <para>
///   This is a provider, not a format reader: it is a different view of the same
///   <c>.nettrace</c> the CPU reader consumes, so it does not implement
///   <c>ITraceReader</c> (which dispatches by file extension).
///  </para>
/// </remarks>
public sealed class ExceptionsProvider
{
    /// <summary>
    ///  Reads the exceptions stack-sample source from the EventPipe trace at
    ///  <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <param name="window">
    ///  Optional time window; when set, only exception-throw events whose timestamp
    ///  falls inside it are read. <see langword="null"/> reads the whole trace.
    /// </param>
    /// <returns>The exceptions source: count-weighted throw-site stacks.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public StackSampleSource Read(string path, TimeWindow? window = null)
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

        List<SampleStack> samples = [];
        List<string> leafToRoot = [];

        foreach (TraceEvent data in traceLog.Events)
        {
            // ExceptionTraceData is the Exception/Start (throw) event; the catch and
            // stop events have different types and are skipped.
            if (data is not ExceptionTraceData exception)
            {
                continue;
            }

            // When scoped to a time window, drop throws outside it; every event carries a
            // trace-relative timestamp, so the same guard scopes every metric.
            if (window is TimeWindow scope && !scope.Contains(data.TimeStampRelativeMSec))
            {
                continue;
            }

            TraceCallStack? callStack = exception.CallStack();
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

            // Append the thrown exception's type as a synthetic leaf so self-time ranks
            // by exception type - which type is thrown most, the first question a throw
            // profile answers - while an inclusive ranking surfaces the throw-site paths.
            // Without it the self-time leaf is the
            // runtime's exception-dispatch frame, common to every throw and useless as a
            // ranking.
            string typeName = string.IsNullOrEmpty(exception.ExceptionType)
                ? "(unknown exception type)"
                : exception.ExceptionType;

            int count = leafToRoot.Count;
            string[] frames = new string[count + 1];
            for (int i = 0; i < count; i++)
            {
                frames[i] = leafToRoot[count - 1 - i];
            }

            frames[count] = typeName;

            // Each throw is one count; the leaf is the exception type, its callers the
            // throw site.
            samples.Add(new SampleStack(frames, 1.0, exception.ThreadID.ToString(CultureInfo.InvariantCulture)));
        }

        return new StackSampleSource(MetricInfo.Exceptions, samples);
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
