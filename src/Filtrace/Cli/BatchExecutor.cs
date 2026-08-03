// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Server;
using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>Runs one compact ranking query across every case in a manifest.</summary>
internal static class BatchExecutor
{
    public static int Run(BatchRequest request, TextWriter output, TextWriter error)
    {
        if (!TraceExecution.TryValidateFold(request.Fold, error))
        {
            return ExitCodes.UsageError;
        }

        try
        {
            CaptureManifest manifest = CaptureManifestReader.Read(request.ManifestPath);
            TraceStore store = new();
            bool belowThreshold = false;
            BatchRankingResult result = CaptureManifestBatchAnalyzer.Analyze(
                manifest,
                MetricSelector(request.Metric),
                request.Measure == Measure.Inclusive,
                request.Root,
                request.Fold,
                (captureManifest, captureCase) =>
                {
                    LoadedTrace trace = store.Get(
                        captureCase.TracePath,
                        request.Symbols ?? captureCase.SymbolsDirectory,
                        request.Metric,
                        captureManifest.ResolveCaseScope(captureCase, request.Scope));
                    belowThreshold |= SymbolGate.IsBelowThreshold(
                        trace.Info.SymbolResolutionRate,
                        trace.Info.SampleCount);
                    return trace;
                });
            AnalysisResult<BatchRankingResult> envelope = new(
                result,
                hints: SteeringHints.ForBatch(result),
                context: AnalysisContext.ForMetric(
                    "batch",
                    TraceMetricSelector.GetInfo(request.Metric),
                    request.Measure == Measure.Inclusive ? "inclusive" : "self",
                    request.Root));

            if (request.Format == OutputFormat.Json)
            {
                output.WriteLine(OutputJson.Serialize(envelope));
            }
            else
            {
                BatchTextRenderer.Render(envelope, output);
            }

            return request.Strict && belowThreshold ? ExitCodes.StrictGate : ExitCodes.Success;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            error.WriteLine(exception.Message);
            return ExitCodes.InputError;
        }
    }

    private static string MetricSelector(TraceMetric metric) => metric switch
    {
        TraceMetric.Cpu => "cpu",
        TraceMetric.ThreadTime => "threadtime",
        TraceMetric.Allocations => "alloc",
        TraceMetric.Exceptions => "exceptions",
        TraceMetric.Contention => "contention",
        TraceMetric.Wait => "wait",
        TraceMetric.Activity => "activity",
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null)
    };

}