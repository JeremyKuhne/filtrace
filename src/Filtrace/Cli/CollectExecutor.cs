// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

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
    public static int Run(EtwCollectRequest request, TextWriter output, TextWriter error)
    {
        try
        {
            EtwCollectResult result = EtwCollector.Collect(request);
            string trace = result.OutputPath;

            output.WriteLine(
                $"Captured {result.FileSizeBytes:N0} bytes to {trace} " +
                $"(process {result.ProcessName} [{result.ProcessId}] exited {result.ProcessExitCode}).");
            output.WriteLine();
            output.WriteLine("Next-step filtrace commands:");
            output.WriteLine($"  filtrace processes \"{trace}\"");
            output.WriteLine($"  filtrace cpu \"{trace}\" --process \"{result.ProcessName}\"");
            if (request.Metric == CollectMetric.ThreadTime)
            {
                output.WriteLine($"  filtrace threadtime \"{trace}\" --process \"{result.ProcessName}\"");
            }

            output.WriteLine($"  filtrace classify \"{trace}\" --process \"{result.ProcessName}\" --native-symbols");
            return ExitCodes.Success;
        }
        catch (Exception ex) when (
            ex is PlatformNotSupportedException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or IOException)
        {
            error.WriteLine(ex.Message);
            return ExitCodes.InputError;
        }
    }
}
