// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Output;

[TestClass]
public sealed class OutputBudgetTests
{
    [TestMethod]
    public void EstimateTokens_EmptyString_Zero()
    {
        OutputBudget.EstimateTokens(string.Empty).Should().Be(0);
    }

    [TestMethod]
    [DataRow(1, 1)]   // one short word -> one token
    [DataRow(6, 1)]   // ceil(6 / 6)
    [DataRow(7, 2)]   // ceil(7 / 6)
    [DataRow(12, 2)]  // ceil(12 / 6)
    [DataRow(13, 3)]  // ceil(13 / 6)
    public void EstimateTokens_SplitsLongWordsBySubTokenDivisor(int length, int expected)
    {
        // A single uninterrupted word is one pre-tokenizer piece, modeled as
        // ceil(length / 6) sub-word tokens (at least one).
        string text = new('x', length);
        OutputBudget.EstimateTokens(text).Should().Be(expected);
    }

    [TestMethod]
    public void EstimateTokens_PunctuationDenseJson_ExceedsCharsOverFour()
    {
        // The pre-tokenizer splits each symbol run into its own piece, so dense JSON
        // estimates higher than the flat four-characters-per-token rule (which badly
        // under-counts such text). This is the whole reason the estimator exists.
        string json = "{\"frame\":\"A.B\",\"weight\":16,\"percentOfScope\":64}";
        int charsOverFour = (json.Length + 3) / 4;

        OutputBudget.EstimateTokens(json).Should().BeGreaterThan(charsOverFour);
    }

    [TestMethod]
    public void EstimateTokens_IsDeterministic()
    {
        string text = "filtrace ranks .NET CPU and allocation traces, by self or inclusive time.";

        int first = OutputBudget.EstimateTokens(text);

        OutputBudget.EstimateTokens(text).Should().Be(first);
        first.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void IsOverBudget_UnderCeiling_False()
    {
        // 8 chars -> ceil(8 / 6) = 2 tokens, under a ceiling of 3.
        OutputBudget.IsOverBudget(new string('x', 8), ceilingTokens: 3).Should().BeFalse();
    }

    [TestMethod]
    public void IsOverBudget_OverCeiling_True()
    {
        // 30 chars -> ceil(30 / 6) = 5 tokens, over a ceiling of 3.
        OutputBudget.IsOverBudget(new string('x', 30), ceilingTokens: 3).Should().BeTrue();
    }

    [TestMethod]
    public void TryGetBudgetWarning_OverCeiling_ProducesRemediationWarning()
    {
        bool fired = OutputBudget.TryGetBudgetWarning(new string('x', 400), ceilingTokens: 10, out string? warning);

        fired.Should().BeTrue();
        warning.Should().NotBeNull();
        warning.Should().Contain("--top");
        warning.Should().Contain("budget");
    }

    [TestMethod]
    public void TryGetBudgetWarning_UnderCeiling_NoWarning()
    {
        bool fired = OutputBudget.TryGetBudgetWarning("small", ceilingTokens: 1000, out string? warning);

        fired.Should().BeFalse();
        warning.Should().BeNull();
    }

    [TestMethod]
    public void DefaultCeiling_Is25000()
    {
        OutputBudget.DefaultCeilingTokens.Should().Be(25_000);
    }

    [TestMethod]
    public void ManifestBatch_MaximumBoundedShape_StaysUnderDefaultCeiling()
    {
        string longIdentity = new('b', 512);
        string longPath = $"C:\\{new string('p', 1021)}";
        string longFrame = new('f', CaptureManifestOutput.MaxFrameLength);
        string[] warnings =
        [
            .. Enumerable.Range(0, CaptureManifestOutput.MaxWarningsPerCase)
                .Select(_ => new string('w', CaptureManifestOutput.MaxWarningLength))
        ];
        BatchRankingCaseResult[] cases =
        [
            .. Enumerable.Range(0, CaptureManifestBatchAnalyzer.MaxAnalyzedCases)
                .Select(_ => new BatchRankingCaseResult(
                    longIdentity,
                    longIdentity,
                    longPath,
                    100.0,
                    "ms",
                    longFrame,
                    75.0,
                    75.0,
                    100,
                    warnings))
        ];
        AnalysisResult<BatchRankingResult> envelope = new(
            new BatchRankingResult("manifest.json", "cpu", "self", "", cases));

        string json = OutputJson.Serialize(envelope);

        OutputBudget.IsOverBudget(json).Should().BeFalse(
            $"bounded batch output estimated {OutputBudget.EstimateTokens(json)} tokens");
    }

    [TestMethod]
    public void ManifestDiff_MaximumBoundedShape_StaysUnderDefaultCeiling()
    {
        string longIdentity = new('b', 512);
        string longFrame = new('f', CaptureManifestOutput.MaxFrameLength);
        string[] warnings =
        [
            .. Enumerable.Range(0, CaptureManifestOutput.MaxWarningsPerCase)
                .Select(_ => new string('w', CaptureManifestOutput.MaxWarningLength))
        ];
        DiffRow[] rows =
        [
            .. Enumerable.Range(0, CaptureManifestDiffAnalyzer.MaxRowsPerCase)
                .Select(_ => new DiffRow(longFrame, 10.0, 20.0, 10.0)
                {
                    BeforePercentOfScope = 10.0,
                    AfterPercentOfScope = 20.0,
                    PercentagePointChange = 10.0,
                    NormalizedWeightChange = 10.0,
                    BeforeWeightPerOperation = 1.0,
                    AfterWeightPerOperation = 2.0,
                    PerOperationDelta = 1.0
                })
        ];
        RankingDiffCaseResult[] cases =
        [
            .. Enumerable.Range(0, CaptureManifestDiffAnalyzer.MaxAnalyzedCases)
                .Select(_ => new RankingDiffCaseResult(
                    longIdentity,
                    longIdentity,
                    100.0,
                    200.0,
                    100.0,
                    rows,
                    warnings)
                {
                    OperationUnit = "items",
                    BeforeScopeWeightPerOperation = 1.0,
                    AfterScopeWeightPerOperation = 2.0,
                    ScopeWeightPerOperationDelta = 1.0
                })
        ];
        AnalysisResult<RankingDiffResult> envelope = new(
            new RankingDiffResult(0.0, 0.0, 0.0, []) { Cases = cases });

        string json = OutputJson.Serialize(envelope);

        OutputBudget.IsOverBudget(json).Should().BeFalse(
            $"bounded diff output estimated {OutputBudget.EstimateTokens(json)} tokens");
    }
}
