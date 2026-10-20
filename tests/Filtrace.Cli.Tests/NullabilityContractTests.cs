// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Cli;
using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli.Tests;

[TestClass]
public sealed class NullabilityContractTests
{
    [TestMethod]
    [DataRow(data: false)]
    [DataRow(data: true)]
    public void RenderManifest_PerOperationDeltaMissing_ReportsInvalidState(bool missingRowDelta)
    {
        RankingDiffCaseResult captureCase = new(
            "Benchmark", "", 1.0, 2.0, 1.0,
            [new DiffRow("Frame", 1.0, 2.0, 1.0)], [])
        {
            OperationUnit = "request",
            ScopeWeightPerOperationDelta = missingRowDelta ? 1.0 : null
        };

        AnalysisResult<RankingDiffResult> envelope = new(new([captureCase]));
        using StringWriter output = new();

        Action render = () => DiffTextRenderer.RenderManifest(envelope, MetricInfo.CpuSamples, Measure.Self, output);

        render.Should().Throw<InvalidOperationException>()
            .WithMessage(missingRowDelta
                ? "A per-operation row must include its delta."
                : "A per-operation case must include its scope delta.");
    }

    [TestMethod]
    public void RenderManifest_WithoutOperationUnit_DoesNotRequirePerOperationDeltas()
    {
        RankingDiffCaseResult captureCase = new(
            "Benchmark", "", 1.0, 2.0, 1.0,
            [new DiffRow("Frame", 1.0, 2.0, 1.0)], []);

        AnalysisResult<RankingDiffResult> envelope = new(new([captureCase]));
        using StringWriter output = new();

        DiffTextRenderer.RenderManifest(envelope, MetricInfo.CpuSamples, Measure.Self, output);

        output.ToString().Should().Contain("Frame").And.NotContain("per request");
    }
}
