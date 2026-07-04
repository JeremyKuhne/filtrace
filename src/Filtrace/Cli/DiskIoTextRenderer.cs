// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Renders a disk-I/O result as the dense, fixed-width text view a human reads at
///  the terminal: a header, the aggregate read / write / disk-time summary, then the
///  per-file detail in aligned columns, and finally any warnings.
/// </summary>
/// <remarks>
///  <para>
///   This is the text half of the disk-I/O report; the JSON half is
///   <see cref="OutputJson"/>. Both render the same <see cref="AnalysisResult{T}"/>
///   envelope.
///  </para>
/// </remarks>
internal static class DiskIoTextRenderer
{
    private const double BytesPerKB = 1024.0;

    /// <summary>
    ///  Renders the disk-I/O envelope to <paramref name="output"/>.
    /// </summary>
    /// <param name="envelope">The disk-I/O report, with its warnings.</param>
    /// <param name="path">The trace path, for the header line.</param>
    /// <param name="output">The writer the text is rendered to.</param>
    public static void Render(AnalysisResult<DiskIoResult> envelope, string path, TextWriter output)
    {
        DiskIoResult report = envelope.Result;

        output.WriteLine($"DiskIO report  -  {path}");
        output.WriteLine();

        if (report.ReadCount == 0 && report.WriteCount == 0)
        {
            output.WriteLine("  (no physical disk I/O events)");
            RenderWarnings(envelope, output);
            return;
        }

        output.WriteLine(
            $"  reads   {report.ReadCount,8}   {report.TotalReadBytes / BytesPerKB,12:N1} KB");
        output.WriteLine(
            $"  writes  {report.WriteCount,8}   {report.TotalWriteBytes / BytesPerKB,12:N1} KB");
        output.WriteLine(
            $"  disk time   {report.TotalDiskMs:N2} ms");
        output.WriteLine();

        output.WriteLine(
            $"  {"reads",6}  {"read(KB)",12}  {"writes",6}  {"write(KB)",12}  {"disk(ms)",10}  file");
        foreach (DiskIoFileRecord file in report.Files)
        {
            output.WriteLine(
                $"  {file.ReadCount,6}  {file.ReadBytes / BytesPerKB,12:N1}  {file.WriteCount,6}  "
                + $"{file.WriteBytes / BytesPerKB,12:N1}  {file.TotalDiskMs,10:N2}  {file.FileName}");
        }

        RenderWarnings(envelope, output);
    }

    private static void RenderWarnings(AnalysisResult<DiskIoResult> envelope, TextWriter output)
    {
        foreach (string warning in envelope.Warnings)
        {
            output.WriteLine($"! {warning}");
        }
    }
}
