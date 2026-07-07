// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Renders a timeline result as the dense text view a human reads at the terminal:
///  the window and bucket geometry, then one aligned sparkline row per requested lane
///  with a short summary, and finally the steering hints and any warnings.
/// </summary>
/// <remarks>
///  <para>
///   This is the text half of the timeline; the JSON half is
///   <see cref="OutputJson"/>. Both render the same <see cref="AnalysisResult{T}"/>
///   envelope. Each lane's per-bucket magnitudes are drawn as an ASCII ramp scaled to
///   that lane's own peak, so a lane's shape reads at a glance even though the lanes
///   are measured in different units.
///  </para>
/// </remarks>
internal static class TimelineTextRenderer
{
    // A blank-to-dense ASCII ramp: index 0 (blank) is an empty bucket, and the rest
    // scale a non-empty bucket by its share of the lane's peak. ASCII so the output is
    // portable across terminals and encodings.
    private const string Ramp = " .:-=+*#";

    private const int LaneLabelWidth = 11;

    /// <summary>
    ///  Renders the timeline envelope to <paramref name="output"/>.
    /// </summary>
    /// <param name="envelope">The timeline, with its hints and warnings.</param>
    /// <param name="path">The trace path, for the header line.</param>
    /// <param name="output">The writer the text is rendered to.</param>
    public static void Render(AnalysisResult<TimelineResult> envelope, string path, TextWriter output)
    {
        TimelineResult timeline = envelope.Result;

        output.WriteLine($"timeline  -  {path}");
        output.WriteLine();
        if (timeline.Process is not null)
        {
            output.WriteLine($"  process {timeline.Process}");
        }

        output.WriteLine(
            $"  {timeline.BucketCount} buckets x {timeline.BucketSizeMs:N1} ms   "
            + $"window [{timeline.FromMs:N0}, {timeline.ToMs:N0}] ms");
        output.WriteLine();

        bool anyLane = false;

        if (timeline.Gc is { } gc)
        {
            anyLane = true;
            long total = gc.Sum(static b => b.Count);
            double peakPause = gc.Count > 0 ? gc.Max(static b => b.MaxPauseMs) : 0.0;
            RenderLane(GcLaneLabel, gc.Select(static b => (double)b.Count), $"{total} GCs, peak pause {peakPause:N2} ms", output);
        }

        if (timeline.Cpu is { } cpu)
        {
            anyLane = true;
            long total = cpu.Sum(static b => (long)b.SampleCount);
            string top = PeakTopMethod(cpu);
            RenderLane(CpuLaneLabel, cpu.Select(static b => (double)b.SampleCount), $"{total} samples{top}", output);
        }

        if (timeline.Exceptions is { } exceptions)
        {
            anyLane = true;
            long total = exceptions.Sum(static b => (long)b.Count);
            string top = PeakTopType(exceptions);
            RenderLane(ExceptionsLaneLabel, exceptions.Select(static b => (double)b.Count), $"{total} thrown{top}", output);
        }

        if (timeline.Alloc is { } alloc)
        {
            anyLane = true;
            long ticks = alloc.Sum(static b => b.Count);
            double totalMB = alloc.Sum(static b => b.Bytes) / (1024.0 * 1024.0);
            RenderLane(AllocLaneLabel, alloc.Select(static b => (double)b.Bytes), $"{totalMB:N2} MB in {ticks} ticks", output);
        }

        if (timeline.Jit is { } jit)
        {
            anyLane = true;
            long total = jit.Sum(static b => (long)b.MethodCount);
            RenderLane(JitLaneLabel, jit.Select(static b => (double)b.MethodCount), $"{total} methods", output);
        }

        if (!anyLane)
        {
            output.WriteLine("  (no lanes requested)");
        }

        output.WriteLine();
        foreach (string hint in envelope.Hints)
        {
            output.WriteLine($"> {hint}");
        }

        foreach (string warning in envelope.Warnings)
        {
            output.WriteLine($"! {warning}");
        }
    }

    private const string GcLaneLabel = "gc";
    private const string CpuLaneLabel = "cpu";
    private const string ExceptionsLaneLabel = "exceptions";
    private const string AllocLaneLabel = "alloc";
    private const string JitLaneLabel = "jit";

    // Renders one lane: its name, a peak-scaled ASCII sparkline of the per-bucket
    // magnitudes, then a short numeric summary.
    private static void RenderLane(string label, IEnumerable<double> magnitudes, string summary, TextWriter output)
    {
        double[] values = [.. magnitudes];
        output.WriteLine($"  {label,-LaneLabelWidth} {Sparkline(values)}   {summary}");
    }

    // Draws the values as an ASCII ramp scaled to the lane's own peak, so an empty
    // bucket is blank and the busiest bucket is the densest glyph.
    private static string Sparkline(double[] values)
    {
        double max = 0.0;
        foreach (double value in values)
        {
            max = Math.Max(max, value);
        }

        char[] cells = new char[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] <= 0.0 || max <= 0.0)
            {
                cells[i] = Ramp[0];
                continue;
            }

            // Scale into ramp levels 1..end so any non-empty bucket is visible, reserving
            // the blank glyph for a genuinely empty bucket.
            int level = 1 + (int)(values[i] / max * (Ramp.Length - 1));
            cells[i] = Ramp[Math.Clamp(level, 1, Ramp.Length - 1)];
        }

        return new string(cells);
    }

    // The ", top <method>" suffix naming the leaf method of the busiest CPU bucket, or
    // empty when no bucket resolved a method.
    private static string PeakTopMethod(IReadOnlyList<CpuBucket> lane)
    {
        int best = 0;
        string? top = null;
        foreach (CpuBucket bucket in lane)
        {
            if (bucket.SampleCount > best && bucket.TopMethod is not null)
            {
                best = bucket.SampleCount;
                top = bucket.TopMethod;
            }
        }

        return top is null ? "" : $", top {top}";
    }

    // The ", top <type>" suffix naming the exception type of the busiest exception
    // bucket, or empty when none carried a type.
    private static string PeakTopType(IReadOnlyList<ExceptionBucket> lane)
    {
        int best = 0;
        string? top = null;
        foreach (ExceptionBucket bucket in lane)
        {
            if (bucket.Count > best && bucket.TopType is not null)
            {
                best = bucket.Count;
                top = bucket.TopType;
            }
        }

        return top is null ? "" : $", top {top}";
    }
}
