// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  Renders a trace's identity and quality signals as a text report a human reads at
///  the terminal: a one-line banner, the analyses the format can answer, the busiest
///  threads, then the symptom-routing hints and quality warnings.
/// </summary>
internal static class InfoTextRenderer
{
    // Cap the per-thread list so a many-threaded capture does not flood the report;
    // the threads are already ordered busiest-first.
    private const int MaxThreads = 10;

    private const int SampleCountColumnWidth = 9;

    /// <summary>
    ///  Renders the trace-info envelope to <paramref name="output"/>.
    /// </summary>
    /// <param name="envelope">The trace-info result, with its hints and warnings.</param>
    /// <param name="output">The writer the text is rendered to.</param>
    public static void Render(AnalysisResult<TraceInfoView> envelope, TextWriter output)
    {
        TraceInfoView view = envelope.Result;

        // The banner mirrors the header every ranking prints; the weight is the CPU
        // view's total sampled milliseconds, the metric this orientation load reads.
        output.WriteLine(
            $"{view.Format}  {view.SampleCount} samples  {view.TotalWeight:N1} ms  symbols {FormatRate(view.SymbolResolutionRate)}");
        output.WriteLine();

        output.WriteLine("analyses:");
        if (view.Analyses is not { Count: > 0 })
        {
            output.WriteLine($"  {string.Join(", ", view.AvailableAnalyses)}");
        }
        else
        {
            foreach (string name in view.AvailableAnalyses)
            {
                if (!view.Analyses.TryGetValue(name, out AnalysisAvailabilityView? availability))
                {
                    continue;
                }

                string count = availability.EventCount is int observed
                    ? $", {observed} events"
                    : string.Empty;
                output.WriteLine(
                    $"  {name}: capture={availability.CaptureStatus}{count}");
            }
        }

        if (view.EtlxCacheState is not null)
        {
            output.WriteLine($"etlx cache: {view.EtlxCacheState}");
        }

        if (view.SourceResolution is SourceResolutionInfo source)
        {
            output.WriteLine($"source: {source.MappedManagedFrameCount}/{source.SampledManagedFrameCount} sampled managed frames ({FormatRate(source.SourceResolutionRate)})");
            output.WriteLine(
                $"symbol directories: {ListOrNone(source.SearchedDirectories)}");
            output.WriteLine(
                $"matching PDB modules: {ListOrNone(source.MatchingPdbModules)}");
            output.WriteLine(
                $"PDB identity mismatch modules: {ListOrNone(source.PdbIdentityMismatchModules)}");
            if (source.SampledManagedMethodCount is int sampledMethods
                && source.SourceMappedManagedMethodCount is int mappedMethods)
            {
                double rate = sampledMethods > 0 ? (double)mappedMethods / sampledMethods : 0.0;
                output.WriteLine(
                    $"methods with sequence points: {mappedMethods}/{sampledMethods} ({FormatRate(rate)})");
            }
            else
            {
                output.WriteLine("methods with sequence points: unavailable");
            }

            output.WriteLine(
                $"named managed frames without source: {source.UnmappedNamedManagedFrameCount}");
            output.WriteLine(
                $"highest unmapped modules: {ListOrNone(source.HighestUnmappedModules)}");
            output.WriteLine(
                $"highest unmapped methods: {ListOrNone(source.HighestUnmappedMethods)}");
        }

        output.WriteLine("threads:");
        if (view.Threads.Count == 0)
        {
            output.WriteLine("  (none)");
        }
        else
        {
            int shown = 0;
            foreach (ThreadSampleInfo thread in view.Threads)
            {
                if (shown == MaxThreads)
                {
                    output.WriteLine($"  ... and {view.Threads.Count - MaxThreads} more");
                    break;
                }

                string name = thread.Thread.Length > 0 ? thread.Thread : "(unnamed)";
                output.WriteLine($"  {thread.SampleCount,SampleCountColumnWidth}  {name}");
                shown++;
            }
        }

        // The symptom-to-analysis routing hints, then quality warnings.
        foreach (string hint in envelope.Hints)
        {
            output.WriteLine($"> {hint}");
        }

        foreach (string warning in envelope.Warnings)
        {
            output.WriteLine($"! {warning}");
        }
    }

    private static string ListOrNone(IReadOnlyList<string> values) =>
        values.Count == 0 ? "(none)" : string.Join(", ", values);

    private static string FormatRate(double value) =>
        $"{(value * 100.0).ToString("0", CultureInfo.InvariantCulture)}%";
}
