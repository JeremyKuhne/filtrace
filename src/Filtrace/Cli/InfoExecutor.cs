// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  Runs a trace-info request against the analysis core: load the trace, project its
///  identity and quality signals into the same <see cref="TraceInfoView"/> the
///  <c>trace_info</c> MCP tool returns, wrap it in the output contract, and render it
///  as text or JSON.
/// </summary>
/// <remarks>
///  <para>
///   This is the CLI counterpart of the <c>trace_info</c> tool, so the two entry
///   points expose the same orientation step: the analyses a capture can answer and
///   whether its symbol-resolution rate is high enough to trust a ranking. It builds
///   the identical envelope, so <c>--format json</c> emits the same shape the tool's
///   structured content carries. The execution is independent of the command-line
///   parser; it takes an <see cref="InfoRequest"/> and writes to the supplied writers,
///   so it can be driven directly in tests as well as from the verb handler.
///  </para>
/// </remarks>
internal static class InfoExecutor
{
    /// <summary>
    ///  Executes the trace-info request.
    /// </summary>
    /// <param name="request">The validated inputs.</param>
    /// <param name="output">The writer the result is rendered to.</param>
    /// <param name="error">The writer load errors are reported to.</param>
    /// <returns>A process exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Run(InfoRequest request, TextWriter output, TextWriter error)
    {
        if (!TraceExecution.TryLoad(
            request.Path,
            TraceMetric.Cpu,
            request.Symbols,
            error,
            out LoadedTrace? trace,
            request.Scope))
        {
            return ExitCodes.InputError;
        }

        TraceInfo info = trace.Info;
        TraceInfoView view = new(
            info.Path,
            info.Format.ToString(),
            info.TotalWeight,
            info.SampleCount,
            info.SymbolResolutionRate,
            info.Threads,
            info.AvailableAnalyses);

        AnalysisResult<TraceInfoView> envelope = new(view, info.Warnings, SteeringHints.ForTraceInfo(info));

        if (request.Format == OutputFormat.Json)
        {
            output.WriteLine(OutputJson.Serialize(envelope));
        }
        else
        {
            InfoTextRenderer.Render(envelope, output);
        }

        return ExitCodes.Success;
    }
}
