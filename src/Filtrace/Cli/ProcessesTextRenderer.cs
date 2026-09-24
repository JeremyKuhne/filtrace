// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  Renders a trace's process inventory as a text table a human reads at the
///  terminal: a one-line trace banner, then each process on its own line with its
///  weight, share of the capture, and sample count, highest weight first.
/// </summary>
internal static class ProcessesTextRenderer
{
    private const int WeightColumnWidth = 16;
    private const int PercentColumnWidth = 6;
    private const int SamplesColumnWidth = 9;

    /// <summary>
    ///  Renders the process-inventory envelope to <paramref name="output"/>.
    /// </summary>
    /// <param name="envelope">The process-inventory result, with its warnings.</param>
    /// <param name="inventory">The trace format and CPU-weight provenance.</param>
    /// <param name="output">The writer the text is rendered to.</param>
    public static void Render(
        AnalysisResult<ProcessListResult> envelope,
        ProcessInventorySnapshot inventory,
        TextWriter output)
    {
        ProcessListResult result = envelope.Result;
        string unit = inventory.Metric.Unit;

        output.WriteLine(
            $"{inventory.Format}  {result.TotalSamples} samples  {result.TotalWeight:N1} {unit}");

        output.WriteLine();
        output.WriteLine(
            $"processes by {inventory.Metric.Name}  -  {result.TotalSamples} samples  {result.TotalWeight:N1} {unit}");

        output.WriteLine(
            $"  {"weight",WeightColumnWidth}  {"%",PercentColumnWidth}  {"samples",SamplesColumnWidth}  process");

        foreach (ProcessSummary process in result.Processes)
        {
            // A single-process trace format carries an empty process label; name it so
            // the row is not blank.
            string name = process.Process.Length > 0 ? process.Process : "(single process)";
            output.WriteLine(
                $"  {$"{process.Weight:N2} {unit}",WeightColumnWidth}  {process.PercentOfScope,PercentColumnWidth:N2}  "
                    + $"{process.SampleCount,SamplesColumnWidth}  {name}");
        }

        if (result.Processes.Count == 0)
        {
            output.WriteLine("  (no samples)");
        }

        foreach (string warning in envelope.Warnings)
        {
            output.WriteLine($"! {warning}");
        }
    }
}
