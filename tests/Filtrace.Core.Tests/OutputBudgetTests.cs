// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Output;

[TestClass]
public sealed class OutputBudgetTests
{
    [TestMethod]
    public void TakeWithinBudget_RowsThatFit_AreAllTaken()
    {
        string[] rows = ["a", "b", "c"];

        List<string> kept = OutputBudget.TakeWithinBudget(rows, static _ => 10, budgetTokens: 100, out bool truncated);

        kept.Should().HaveCount(3);
        truncated.Should().BeFalse();
    }

    [TestMethod]
    public void TakeWithinBudget_OverBudget_StopsAndReportsTruncation()
    {
        string[] rows = ["a", "b", "c"];

        List<string> kept = OutputBudget.TakeWithinBudget(rows, static _ => 40, budgetTokens: 100, out bool truncated);

        kept.Should().HaveCount(2);
        truncated.Should().BeTrue();
    }

    [TestMethod]
    public void TakeWithinBudget_FirstRowOverBudget_IsStillTaken()
    {
        // A producer with rows never returns an empty list the caller cannot act on, which
        // is why each producer has to bound whatever scales a single row's size.
        string[] rows = ["a", "b"];

        List<string> kept = OutputBudget.TakeWithinBudget(rows, static _ => 1_000, budgetTokens: 10, out bool truncated);

        kept.Should().ContainSingle();
        truncated.Should().BeTrue();
    }

    [TestMethod]
    public void TakeWithinBudget_FirstRowOverBudgetAndNotRequired_IsDropped()
    {
        // A secondary list sharing a response with one already filled does not need the
        // carve-out: the response is non-empty without it, so a row that does not fit is
        // dropped rather than pushed over the budget.
        string[] rows = ["a", "b"];

        List<string> kept = OutputBudget.TakeWithinBudget(
            rows, static _ => 1_000, budgetTokens: 10, out bool truncated, takeAtLeastOne: false);

        kept.Should().BeEmpty();
        truncated.Should().BeTrue();
    }

    [TestMethod]
    public void TakeWithinBudget_NoRows_IsNotTruncated()
    {
        string[] rows = [];

        List<string> kept = OutputBudget.TakeWithinBudget(rows, static _ => 1, budgetTokens: 10, out bool truncated);

        kept.Should().BeEmpty();
        truncated.Should().BeFalse();
    }

    [TestMethod]
    public void TakeWithinBudget_ZeroBudget_TakesTheFirstRowAlone()
    {
        // Zero is a meaningful budget - a caller subtracting what the envelope already
        // spent can reach it - and means the same as any budget the first row overruns.
        string[] rows = ["a", "b"];

        List<string> kept = OutputBudget.TakeWithinBudget(rows, static _ => 1, budgetTokens: 0, out bool truncated);

        kept.Should().ContainSingle();
        truncated.Should().BeTrue();
    }

    [TestMethod]
    public void TakeWithinBudget_NegativeBudget_Throws()
    {
        // A negative budget can only come from a computation error, and swallowing it
        // would hide that behind a plausible-looking one-row result.
        string[] rows = ["a"];

        Action act = () => OutputBudget.TakeWithinBudget(rows, static _ => 1, budgetTokens: -1, out _);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

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
