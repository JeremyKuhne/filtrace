// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  Runs the <c>collect</c> verb against the analysis core's <see cref="EtwCollector"/>,
///  mapping its failure modes to a defined exit code rather than an unhandled exception and
///  printing the next-step analysis commands the fresh capture unlocks.
/// </summary>
/// <remarks>
///  <para>
///   Unlike the analysis verbs this one records a trace rather than reading one, so it
///   bypasses the ranking pipeline entirely. It is Windows-only and needs Administrator;
///   both are surfaced as a clean input error off the happy path.
///  </para>
///  <para>
///   The execution is independent of the command-line parser: it takes a
///   <see cref="EtwCollectRequest"/> directly and writes to the supplied writers, so it can
///   be driven in tests as well as from the verb handler in <see cref="TraceCommands"/>.
///  </para>
/// </remarks>
internal static class CollectExecutor
{
    /// <summary>
    ///  Records the ETW capture described by <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The capture inputs.</param>
    /// <param name="output">The writer the result and next steps are reported to.</param>
    /// <param name="error">The writer a failure message is reported to.</param>
    /// <returns>A process exit code (see <see cref="ExitCodes"/>).</returns>
    /// <returns>
    ///  A process exit code (see <see cref="ExitCodes"/>). This reports whether the capture
    ///  ran, not how the launched command fared: a capture whose launches all failed still
    ///  produced a trace and returns success. Read
    ///  <see cref="EtwCollectResult.ProcessExitCode"/>, or the printed failure count, to
    ///  judge the command itself.
    /// </returns>
    public static int Run(EtwCollectRequest request, TextWriter output, TextWriter error) =>
        Run(request, OutputFormat.Text, output, error);

    /// <summary>
    ///  Runs the capture and renders the result in <paramref name="format"/>.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   The JSON form is what lets a capture script record each launch without parsing the
    ///   human summary, which is the only way a manifest can carry them accurately.
    ///  </para>
    /// </remarks>
    public static int Run(
        EtwCollectRequest request,
        OutputFormat format,
        TextWriter output,
        TextWriter error)
    {
        try
        {
            EtwCollectResult result = EtwCollector.Collect(request);

            // A silently clamped interval scales every weight the capture produces, so both
            // heads report it rather than leave it to be discovered from a thin ranking.
            List<string> warnings = [];
            if (result.CpuSample.Clamped)
            {
                warnings.Add(
                    $"Requested a {FormatMSec(result.CpuSample.RequestedMSec)} ms sample interval, but this "
                    + $"machine honors {FormatMSec(result.CpuSample.MinimumMSec)} to "
                    + $"{FormatMSec(result.CpuSample.MaximumMSec)} ms; the capture sampled at "
                    + $"{FormatMSec(result.CpuSample.EffectiveMSec)} ms.");
            }

            if (format == OutputFormat.Json)
            {
                output.WriteLine(OutputJson.Serialize(new AnalysisResult<EtwCollectResult>(
                    result,
                    warnings,
                    [],
                    new AnalysisContext("collect"))));
                return ExitCodes.Success;
            }

            string trace = result.OutputPath;

            output.WriteLine(
                $"Captured {result.FileSizeBytes:N0} bytes to {trace} " +
                $"(process {result.ProcessName} [{result.ProcessId}] exited {result.ProcessExitCode}).");

            WriteInvocationSummary(result, output);

            // What the session actually enabled, so a trace can be audited after the fact
            // rather than inferred from the verb that wrote it.
            output.WriteLine(
                $"  profile {result.Profile.ToString().ToLowerInvariant()}; kernel {result.KernelKeywords}; " +
                $"clr {result.ClrKeywords}; cpu sample {FormatMSec(result.CpuSample.EffectiveMSec)} ms");

            foreach (string warning in warnings)
            {
                output.WriteLine($"! {warning}");
            }

            if (result.Profile == CollectProfile.Startup)
            {
                output.WriteLine(
                    "  startup keeps only the managed-naming CLR keywords, so GC, contention, and exception "
                    + "analyses have no events in this capture.");
            }

            output.WriteLine();
            output.WriteLine("Next-step filtrace commands:");
            output.WriteLine($"  filtrace processes \"{trace}\"");
            output.WriteLine($"  filtrace rank \"{trace}\" --metric cpu --process \"{result.ProcessName}\"");
            if (request.Profile == CollectProfile.ThreadTime)
            {
                output.WriteLine($"  filtrace rank \"{trace}\" --metric threadtime --process \"{result.ProcessName}\"");
            }

            output.WriteLine($"  filtrace classify \"{trace}\" --process \"{result.ProcessName}\" --native-symbols");
            return ExitCodes.Success;
        }
        catch (Exception ex) when (
            ex is PlatformNotSupportedException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or IOException
            or System.ComponentModel.Win32Exception)
        {
            error.WriteLine(ex.Message);
            return ExitCodes.InputError;
        }
    }

    // Sub-millisecond intervals are the point of the widened range, so the format has to
    // keep enough places to tell 0.1221 from 0.125.
    private static string FormatMSec(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    ///  Reports what a repeated capture ran, and names the launches that failed.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Bounded on purpose: a hundred-iteration capture must not print a hundred rows, and
    ///   the only per-launch detail worth reading back is which ones did not exit cleanly.
    ///   The trace carries the rest.
    ///  </para>
    /// </remarks>
    private static void WriteInvocationSummary(EtwCollectResult result, TextWriter output)
    {
        if (result.Invocations.Count <= 1)
        {
            return;
        }

        List<EtwInvocation> failed = [.. result.Invocations.Where(static invocation => invocation.ExitCode != 0)];
        double totalMSec = result.Invocations.Sum(static invocation => invocation.Duration.TotalMilliseconds);

        output.WriteLine(
            $"  {result.Invocations.Count} launches in one session, "
            + $"{totalMSec.ToString("N0", CultureInfo.InvariantCulture)} ms of process wall time, "
            + $"{failed.Count} failed.");

        if (failed.Count > 0)
        {
            string ordinals = string.Join(
                ", ",
                failed.Take(MaxReportedFailures).Select(
                    static invocation => $"#{invocation.Ordinal} exited {invocation.ExitCode}"));
            string more = failed.Count > MaxReportedFailures
                ? $", and {failed.Count - MaxReportedFailures} more"
                : string.Empty;
            output.WriteLine($"  failed launches: {ordinals}{more}");
        }
    }

    private const int MaxReportedFailures = 5;
}
