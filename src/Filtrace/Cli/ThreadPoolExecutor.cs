// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Runs a thread-pool request against the analysis core: read the runtime's
///  worker-thread adjustment events, wrap the result in the output contract, and
///  render it as text or JSON.
/// </summary>
/// <remarks>
///  <para>
///   Unlike the ranking verbs this is a structured report, not a stack ranking, so
///   it does not flow through the folding aggregator. The whole report is a small,
///   fixed-size summary (an adjustment tally, the worker-thread range, and a per-reason
///   breakdown), so nothing is capped; a trace with no adjustment events yields an
///   empty report with an explanatory warning.
///  </para>
///  <para>
///   The execution is independent of the command-line parser; it takes its inputs
///   as a <see cref="ThreadPoolRequest"/> and writes to the supplied writers, so it
///   can be driven directly in tests as well as from the verb handler in
///   <see cref="TraceCommands"/>.
///  </para>
/// </remarks>
internal static class ThreadPoolExecutor
{
    /// <summary>
    ///  Executes the thread-pool request.
    /// </summary>
    /// <param name="request">The validated thread-pool inputs.</param>
    /// <param name="output">The writer the result is rendered to.</param>
    /// <param name="error">The writer load errors are reported to.</param>
    /// <returns>A process exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Run(ThreadPoolRequest request, TextWriter output, TextWriter error)
    {
        if (!TraceExecution.TryReadNetTraceReport(
            request.Path,
            "thread-pool",
            () => new ThreadPoolProvider().Read(request.Path),
            error,
            out ThreadPoolResult? report))
        {
            return ExitCodes.InputError;
        }

        List<string> warnings = [];
        if (report.AdjustmentCount == 0)
        {
            warnings.Add("The trace carries no thread-pool worker-thread adjustment events.");
        }

        AnalysisResult<ThreadPoolResult> envelope = new(report, warnings);

        if (request.Format == OutputFormat.Json)
        {
            output.WriteLine(OutputJson.Serialize(envelope));
        }
        else
        {
            ThreadPoolTextRenderer.Render(envelope, request.Path, output);
        }

        return ExitCodes.Success;
    }
}
