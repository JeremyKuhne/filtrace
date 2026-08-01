// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Renders a lifecycle result as the dense, fixed-width text view a human reads at the
///  terminal: a header, the phase medians, the loader milestones, then the
///  per-invocation detail, and finally any warnings and hints.
/// </summary>
/// <remarks>
///  <para>
///   This is the text half of the lifecycle report; the JSON half is
///   <see cref="OutputJson"/>. Both render the same <see cref="AnalysisResult{T}"/>
///   envelope.
///  </para>
/// </remarks>
internal static class LifecycleTextRenderer
{
    /// <summary>
    ///  Renders the lifecycle envelope to <paramref name="output"/>.
    /// </summary>
    /// <param name="envelope">The lifecycle report, with its warnings and hints.</param>
    /// <param name="path">The trace path, for the header line.</param>
    /// <param name="output">The writer the text is rendered to.</param>
    public static void Render(AnalysisResult<LifecycleResult> envelope, string path, TextWriter output)
    {
        LifecycleResult report = envelope.Result;

        output.WriteLine($"Lifecycle report  -  {path}   (roots: {report.Scope})");
        output.WriteLine();

        if (report.InvocationCount == 0)
        {
            output.WriteLine("  (no matching process)");
            RenderNotes(envelope, output);
            return;
        }

        output.WriteLine(
            $"  {report.InvocationCount} invocation(s), {report.MeasuredCount} fully observed");
        output.WriteLine(
            $"  sampled CPU   root {report.TotalRootCpuMs:N2} ms   children {report.TotalChildCpuMs:N2} ms"
            + "   (sampled CPU, not wall clock)");
        output.WriteLine();

        if (report.Phases.Count > 0)
        {
            output.WriteLine("  wall-clock phases, milliseconds");
            output.WriteLine($"    {"phase",-32}  {"n",3}  {"p50",10}  {"min",10}  {"max",10}");
            foreach (LifecyclePhase phase in report.Phases)
            {
                output.WriteLine(
                    $"    {phase.Phase,-32}  {phase.Count,3}  {phase.MedianMs,10:N3}  "
                    + $"{phase.MinimumMs,10:N3}  {phase.MaximumMs,10:N3}");
            }

            output.WriteLine();
        }

        if (report.ImageMilestones.Count > 0)
        {
            output.WriteLine("  image load offsets from root start, milliseconds");
            output.WriteLine($"    {"module",-32}  {"n",3}  {"p50",10}  {"min",10}  {"max",10}");
            foreach (LifecycleImageMilestone milestone in report.ImageMilestones)
            {
                output.WriteLine(
                    $"    {milestone.Module,-32}  {milestone.Count,3}  {milestone.MedianOffsetMs,10:N3}  "
                    + $"{milestone.MinimumOffsetMs,10:N3}  {milestone.MaximumOffsetMs,10:N3}");
            }

            output.WriteLine();
        }

        output.WriteLine(
            $"  {"#",3}  {"pid",7}  {"start(ms)",11}  {"life(ms)",10}  {"cpu(ms)",9}  process");
        foreach (LifecycleInvocation invocation in report.Invocations)
        {
            RenderProcess(output, $"{invocation.Ordinal,3}", invocation.Root, clipped: !invocation.Measurable);
            foreach (LifecycleProcess child in invocation.Children)
            {
                RenderProcess(output, "   ", child, clipped: !child.StartObserved || !child.StopObserved, indent: "  + ");
            }
        }

        RenderNotes(envelope, output);
    }

    private static void RenderProcess(
        TextWriter output,
        string ordinal,
        LifecycleProcess process,
        bool clipped,
        string indent = "    ")
    {
        // A clipped row is marked rather than dropped: its lifetime is a lower bound, and a
        // reader comparing rows needs to know which ones the medians excluded.
        string mark = clipped ? " *" : "";
        output.WriteLine(
            $"  {ordinal}  {process.ProcessId,7}  {process.StartMs,11:N3}  {process.LifetimeMs,10:N3}  "
            + $"{process.CpuMs,9:N2}  {indent}{process.Name}{mark}");
    }

    private static void RenderNotes(AnalysisResult<LifecycleResult> envelope, TextWriter output)
    {
        foreach (string warning in envelope.Warnings)
        {
            output.WriteLine($"! {warning}");
        }

        foreach (string hint in envelope.Hints)
        {
            output.WriteLine($"> {hint}");
        }
    }
}
