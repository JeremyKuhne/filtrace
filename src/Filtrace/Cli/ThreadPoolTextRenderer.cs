// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Renders a thread-pool result as the dense, fixed-width text view a human reads at
///  the terminal: a header, the adjustment tally (leading with the starvation count),
///  the worker-thread range against the configured limits, then the per-reason
///  breakdown, and finally any warnings.
/// </summary>
/// <remarks>
///  <para>
///   This is the text half of the thread-pool report; the JSON half is
///   <see cref="OutputJson"/>. Both render the same <see cref="AnalysisResult{T}"/>
///   envelope.
///  </para>
/// </remarks>
internal static class ThreadPoolTextRenderer
{
    /// <summary>
    ///  Renders the thread-pool envelope to <paramref name="output"/>.
    /// </summary>
    /// <param name="envelope">The thread-pool report, with its warnings.</param>
    /// <param name="path">The trace path, for the header line.</param>
    /// <param name="output">The writer the text is rendered to.</param>
    public static void Render(AnalysisResult<ThreadPoolResult> envelope, string path, TextWriter output)
    {
        ThreadPoolResult report = envelope.Result;

        output.WriteLine($"ThreadPool report  -  {path}");
        output.WriteLine();

        if (report.AdjustmentCount == 0)
        {
            output.WriteLine("  (no thread-pool worker-thread adjustments recorded)");
            RenderWarnings(envelope, output);
            return;
        }

        output.WriteLine(
            $"  worker-thread adjustments: {report.AdjustmentCount}   (starvation: {report.StarvationCount})");
        output.WriteLine(
            $"  worker threads: {report.MinWorkerThreadCount} -> {report.MaxWorkerThreadCount}   "
            + $"(configured min {report.ConfiguredMinWorkerThreads}, max {report.ConfiguredMaxWorkerThreads})");
        output.WriteLine();

        output.WriteLine("  by reason");
        foreach (ThreadPoolAdjustment adjustment in report.AdjustmentsByReason)
        {
            output.WriteLine($"    {adjustment.Reason,-20}  {adjustment.Count,6}");
        }

        RenderWarnings(envelope, output);
    }

    private static void RenderWarnings(AnalysisResult<ThreadPoolResult> envelope, TextWriter output)
    {
        foreach (string warning in envelope.Warnings)
        {
            output.WriteLine($"! {warning}");
        }
    }
}
