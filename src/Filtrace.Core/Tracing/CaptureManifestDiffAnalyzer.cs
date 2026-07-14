// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>Runs bounded normalized diffs across paired capture manifests.</summary>
public static class CaptureManifestDiffAnalyzer
{
    /// <summary>Maximum paired cases analyzed in one request.</summary>
    public const int MaxAnalyzedCases = 24;

    /// <summary>Maximum changed rows returned per case.</summary>
    public const int MaxRowsPerCase = 5;

    /// <summary>Diffs cases paired by benchmark and parameters.</summary>
    /// <param name="before">Baseline manifest.</param>
    /// <param name="after">Current manifest.</param>
    /// <param name="inclusive">Whether to rank inclusive rather than self weight.</param>
    /// <param name="root">Optional root-frame selector.</param>
    /// <param name="foldPatterns">Leaf-fold patterns.</param>
    /// <param name="top">Requested changed rows per case, capped at <see cref="MaxRowsPerCase"/>.</param>
    /// <param name="load">Loads one case with the owning head's cache and scope policy.</param>
    /// <returns>The case-keyed diff plus bounded pairing warnings.</returns>
    public static CaptureManifestDiffAnalysis Analyze(
        CaptureManifest before,
        CaptureManifest after,
        bool inclusive,
        string root,
        IReadOnlyList<string> foldPatterns,
        int top,
        Func<CaptureManifest, CaptureManifestCase, LoadedTrace> load)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(foldPatterns);
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        ArgumentNullException.ThrowIfNull(load);

        CaptureManifestPairResult pairing = CaptureManifestPairer.Pair(before, after);
        if (pairing.Pairs.Count > MaxAnalyzedCases)
        {
            throw new InvalidDataException(
                $"Manifest diff has {pairing.Pairs.Count} paired cases; the maximum is {MaxAnalyzedCases}.");
        }

        int rowsPerCase = Math.Min(top, MaxRowsPerCase);
        List<string> warnings = [.. pairing.Warnings];
        if (top > MaxRowsPerCase)
        {
            warnings.Add($"manifest diff rows are capped to {MaxRowsPerCase} per case");
        }

        List<RankingDiffCaseResult> cases = new(pairing.Pairs.Count);
        foreach (CaptureManifestCasePair pair in pairing.Pairs)
        {
            List<string> caseWarnings = [];
            try
            {
                LoadedTrace beforeTrace = load(before, pair.Before);
                LoadedTrace afterTrace = load(after, pair.After);
                RankingResult beforeRanking = Rank(
                    beforeTrace,
                    inclusive,
                    root,
                    foldPatterns);
                RankingResult afterRanking = Rank(
                    afterTrace,
                    inclusive,
                    root,
                    foldPatterns);

                AddQualityWarnings(caseWarnings, "baseline", beforeTrace, beforeRanking, root);
                AddQualityWarnings(caseWarnings, "current", afterTrace, afterRanking, root);
                RankingDiffResult diff = Diff(
                    beforeRanking,
                    afterRanking,
                    rowsPerCase,
                    pair.Before,
                    pair.After,
                    caseWarnings);

                cases.Add(ToCaseResult(pair, diff, caseWarnings));
            }
            catch (Exception exception) when (IsCaseFailure(exception))
            {
                CaptureManifestOutput.AddWarning(caseWarnings, exception.Message);
                cases.Add(new RankingDiffCaseResult(
                    pair.Before.Benchmark!,
                    pair.Before.Parameters,
                    0.0,
                    0.0,
                    0.0,
                    [],
                    caseWarnings));
            }
        }

        RankingDiffResult result = new(0.0, 0.0, 0.0, []) { Cases = cases };
        return new CaptureManifestDiffAnalysis(result, warnings);
    }

    private static RankingResult Rank(
        LoadedTrace trace,
        bool inclusive,
        string root,
        IReadOnlyList<string> foldPatterns) =>
        inclusive
            ? trace.Aggregator.InclusiveTime(root, foldPatterns, int.MaxValue)
            : trace.Aggregator.SelfTime(root, foldPatterns, int.MaxValue);

    private static RankingDiffResult Diff(
        RankingResult before,
        RankingResult after,
        int top,
        CaptureManifestCase beforeCase,
        CaptureManifestCase afterCase,
        List<string> warnings)
    {
        if (beforeCase.HasCompleteOperationMetadata
            && afterCase.HasCompleteOperationMetadata
            && string.Equals(
                beforeCase.OperationUnit,
                afterCase.OperationUnit,
                StringComparison.Ordinal))
        {
            return RankingDiff.DiffPerOperation(
                before,
                after,
                top,
                beforeCase.OperationCount!.Value,
                afterCase.OperationCount!.Value,
                beforeCase.OperationUnit!);
        }

        bool hasAnyOperationMetadata = beforeCase.OperationCount is not null
            || beforeCase.OperationUnit is not null
            || afterCase.OperationCount is not null
            || afterCase.OperationUnit is not null;
        if (hasAnyOperationMetadata)
        {
            CaptureManifestOutput.AddWarning(
                warnings,
                "per-operation values omitted: both cases require positive operationCount and the same operationUnit");
        }

        return RankingDiff.Diff(before, after, top);
    }

    private static RankingDiffCaseResult ToCaseResult(
        CaptureManifestCasePair pair,
        RankingDiffResult diff,
        IReadOnlyList<string> warnings) =>
        new(
            pair.Before.Benchmark!,
            pair.Before.Parameters,
            diff.BeforeScopeWeight,
            diff.AfterScopeWeight,
            diff.ScopeDelta,
            diff.Rows.Select(static row => row with
            {
                Frame = CaptureManifestOutput.BoundFrame(row.Frame)
            }).ToArray(),
            warnings)
        {
            BeforeContributingRecordCount = diff.BeforeContributingRecordCount,
            AfterContributingRecordCount = diff.AfterContributingRecordCount,
            OperationUnit = diff.OperationUnit,
            BeforeScopeWeightPerOperation = diff.BeforeScopeWeightPerOperation,
            AfterScopeWeightPerOperation = diff.AfterScopeWeightPerOperation,
            ScopeWeightPerOperationDelta = diff.ScopeWeightPerOperationDelta
        };

    private static void AddQualityWarnings(
        List<string> warnings,
        string side,
        LoadedTrace trace,
        RankingResult ranking,
        string root)
    {
        foreach (string warning in trace.Info.Warnings.Take(4))
        {
            CaptureManifestOutput.AddWarning(warnings, $"{side}: {warning}");
        }

        if (ContributingRecordQuality.TryGetMethodWarning(
            trace.Source.RecordSemantics,
            ranking.ContributingRecordCount,
            out string? recordWarning))
        {
            CaptureManifestOutput.AddWarning(warnings, $"{side}: {recordWarning}");
        }

        if (!string.IsNullOrEmpty(root))
        {
            FrameMatchReport report = FrameMatchAnalyzer.Analyze(
                trace.Source,
                root,
                FrameMatchSelection.Outermost);
            if (report.Matches.Count == 0)
            {
                CaptureManifestOutput.AddWarning(
                    warnings,
                    $"{side}: root '{root}' matched no frames");
            }
            else if (report.IsAmbiguous)
            {
                CaptureManifestOutput.AddWarning(
                    warnings,
                    $"{side}: root '{root}' matched {report.Matches.Count} frame definitions; outermost selected");
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

/// <summary>Manifest diff payload plus cross-manifest pairing warnings.</summary>
/// <param name="Result">Case-keyed ranking diff result.</param>
/// <param name="Warnings">Bounded pairing and output-cap warnings.</param>
public sealed record CaptureManifestDiffAnalysis(
    RankingDiffResult Result,
    IReadOnlyList<string> Warnings);