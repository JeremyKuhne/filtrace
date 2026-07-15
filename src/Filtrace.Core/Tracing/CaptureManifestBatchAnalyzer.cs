// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>Runs one compact ranking query across a bounded capture manifest.</summary>
public static class CaptureManifestBatchAnalyzer
{
    /// <summary>Maximum cases analyzed in one batch.</summary>
    public const int MaxAnalyzedCases = 24;

    /// <summary>Runs a self or inclusive ranking summary for every manifest case.</summary>
    /// <param name="manifest">Capture manifest.</param>
    /// <param name="metric">Canonical metric selector.</param>
    /// <param name="inclusive">Whether to rank inclusive rather than self weight.</param>
    /// <param name="root">Optional root-frame selector.</param>
    /// <param name="foldPatterns">Leaf-fold patterns.</param>
    /// <param name="load">Loads a case with the owning head's cache and scope policy.</param>
    /// <returns>The compact case-keyed batch result.</returns>
    public static BatchRankingResult Analyze(
        CaptureManifest manifest,
        string metric,
        bool inclusive,
        string root,
        IReadOnlyList<string> foldPatterns,
        Func<CaptureManifest, CaptureManifestCase, LoadedTrace> load)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(metric);
        ArgumentNullException.ThrowIfNull(foldPatterns);
        ArgumentNullException.ThrowIfNull(load);
        if (manifest.Cases.Count > MaxAnalyzedCases)
        {
            throw new InvalidDataException(
                $"Capture manifest has {manifest.Cases.Count} cases; batch analysis supports at most {MaxAnalyzedCases}.");
        }

        List<BatchRankingCaseResult> cases = new(manifest.Cases.Count);
        foreach (CaptureManifestCase captureCase in manifest.Cases)
        {
            List<string> warnings = [];
            string benchmark = captureCase.Benchmark ?? captureCase.Id;
            if (captureCase.Benchmark is null)
            {
                CaptureManifestOutput.AddWarning(
                    warnings,
                    "benchmark identity is unresolved; manifest batch skipped this case; analyze the trace directly");
                cases.Add(new BatchRankingCaseResult(
                    benchmark,
                    captureCase.Parameters,
                    captureCase.TracePath,
                    0.0,
                    string.Empty,
                    null,
                    0.0,
                    0.0,
                    null,
                    warnings));
                continue;
            }

            try
            {
                LoadedTrace trace = load(manifest, captureCase);
                RankingResult ranking = inclusive
                    ? trace.Aggregator.InclusiveTime(root, foldPatterns, 1)
                    : trace.Aggregator.SelfTime(root, foldPatterns, 1);
                AddQualityWarnings(warnings, trace, ranking, root);
                RankRow? top = ranking.Rows.FirstOrDefault();
                string? operationUnit = null;
                double? scopePerOperation = null;
                double? topPerOperation = null;
                if (captureCase.HasCompleteOperationMetadata)
                {
                    operationUnit = captureCase.OperationUnit;
                    scopePerOperation = ranking.ScopeWeight / captureCase.OperationCount!.Value;
                    topPerOperation = top?.Weight / captureCase.OperationCount.Value;
                }
                else if (captureCase.OperationCount is not null || captureCase.OperationUnit is not null)
                {
                    CaptureManifestOutput.AddWarning(
                        warnings,
                        "per-operation values omitted: operationCount and operationUnit must both be present");
                }

                cases.Add(new BatchRankingCaseResult(
                    benchmark,
                    captureCase.Parameters,
                    captureCase.TracePath,
                    ranking.ScopeWeight,
                    trace.Aggregator.Metric.Unit,
                    top is null ? null : CaptureManifestOutput.BoundFrame(top.Frame),
                    top?.Weight ?? 0.0,
                    top?.PercentOfScope ?? 0.0,
                    ranking.ContributingRecordCount,
                    warnings)
                {
                    OperationUnit = operationUnit,
                    ScopeWeightPerOperation = scopePerOperation,
                    TopWeightPerOperation = topPerOperation
                });
            }
            catch (Exception exception) when (IsCaseFailure(exception))
            {
                CaptureManifestOutput.AddWarning(warnings, exception.Message);
                cases.Add(new BatchRankingCaseResult(
                    benchmark,
                    captureCase.Parameters,
                    captureCase.TracePath,
                    0.0,
                    string.Empty,
                    null,
                    0.0,
                    0.0,
                    null,
                    warnings));
            }
        }

        return new BatchRankingResult(
            manifest.Path,
            metric,
            inclusive ? "inclusive" : "self",
            root,
            cases);
    }

    private static void AddQualityWarnings(
        List<string> warnings,
        LoadedTrace trace,
        RankingResult ranking,
        string root)
    {
        foreach (string warning in trace.Info.Warnings.Take(4))
        {
            CaptureManifestOutput.AddWarning(warnings, warning);
        }

        if (ContributingRecordQuality.TryGetMethodWarning(
            trace.Source.RecordSemantics,
            ranking.ContributingRecordCount,
            out string? recordWarning))
        {
            CaptureManifestOutput.AddWarning(warnings, recordWarning!);
        }

        if (ranking.Rows.Count == 0)
        {
            CaptureManifestOutput.AddWarning(warnings, "query matched no ranked frames");
        }

        if (!string.IsNullOrEmpty(root))
        {
            FrameMatchReport report = FrameMatchAnalyzer.Analyze(
                trace.Source,
                root,
                FrameMatchSelection.Outermost);
            if (report.Matches.Count == 0)
            {
                CaptureManifestOutput.AddWarning(warnings, $"root '{root}' matched no frames");
            }
            else if (report.IsAmbiguous)
            {
                CaptureManifestOutput.AddWarning(
                    warnings,
                    $"root '{root}' matched {report.Matches.Count} frame definitions; outermost selected");
            }
        }
    }

    private static bool IsCaseFailure(Exception exception) =>
        exception is IOException
        or UnauthorizedAccessException
        or NotSupportedException
        or InvalidOperationException
        or FormatException
        or ArgumentException;
}