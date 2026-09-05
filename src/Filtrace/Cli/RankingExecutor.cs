// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  Runs a ranking request against the analysis core: load the trace, compute the
///  self- or inclusive-weight ranking, wrap it in the output contract, and render it
///  as text or JSON.
/// </summary>
/// <remarks>
///  <para>
///   The execution is independent of the command-line parser; it takes its inputs
///   as a <see cref="RankRequest"/> and writes to the supplied writers, so it can be
///   driven directly in tests as well as from the verb handlers in
///   <see cref="TraceCommands"/>.
///  </para>
/// </remarks>
internal static class RankingExecutor
{
    /// <summary>
    ///  Executes the ranking request.
    /// </summary>
    /// <param name="request">The validated ranking inputs.</param>
    /// <param name="output">The writer the result is rendered to.</param>
    /// <param name="error">The writer load errors are reported to.</param>
    /// <returns>A process exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Run(RankRequest request, TextWriter output, TextWriter error)
    {
        if (!TraceExecution.TryValidateFold(request.Fold, error))
        {
            return ExitCodes.UsageError;
        }

        string path = request.Path;
        string? symbols = string.IsNullOrEmpty(request.Symbols) ? null : request.Symbols;
        ScopeRequest? scope = request.Scope;
        if (!string.IsNullOrEmpty(request.CaseId))
        {
            string caseId = request.CaseId;
            try
            {
                CaptureManifest manifest = CaptureManifestReader.Read(request.Path);
                CaptureManifestCase captureCase = manifest.GetCase(caseId);
                path = captureCase.TracePath;
                symbols ??= captureCase.SymbolsDirectory;
                scope = manifest.ResolveCaseScope(captureCase, scope);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or ArgumentException)
            {
                error.WriteLine($"Could not resolve manifest case '{caseId}': {exception.Message}");
                return ExitCodes.InputError;
            }
        }

        if (!TraceExecution.TryLoad(
            path, request.Metric, symbols, error, out LoadedTrace? trace, scope, request.SymbolOptions))
        {
            return ExitCodes.InputError;
        }

        TraceInfo info = trace.Info;
        RankingResult ranked = request.Measure == Measure.Inclusive
            ? trace.Aggregator.InclusiveTime(request.Root, request.Fold, request.Top)
            : trace.Aggregator.SelfTime(request.Root, request.Fold, request.Top);

        RankingResult ranking = FoldingAggregator.LimitRows(ranked, out string? budgetWarning);
        List<string> warnings = [.. TraceExecution.ResultWarnings(info)];
        if (budgetWarning is not null)
        {
            warnings.Add(budgetWarning);
        }

        if (ContributingRecordQuality.TryGetMethodWarning(
            trace.Source.RecordSemantics,
            ranking.ContributingRecordCount,
            out string? recordWarning))
        {
            warnings.Add(recordWarning!);
        }

        AnalysisResult<RankingResult> envelope = new(
            ranking,
            warnings,
            SteeringHints.ForRanking(ranking, trace.Aggregator.Metric, scope),
            AnalysisContext.ForTrace(
                "rank",
                trace,
                request.Measure == Measure.Inclusive ? "inclusive" : "self",
                request.Root));

        if (request.Format == OutputFormat.Json)
        {
            output.WriteLine(OutputJson.Serialize(envelope));
        }
        else
        {
            RankingTextRenderer.Render(envelope, info, trace.Aggregator.Metric, request.Measure, output);
        }

        return TraceExecution.StrictExit(info, request.Strict);
    }
}
