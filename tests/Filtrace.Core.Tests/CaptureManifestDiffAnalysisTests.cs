// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class CaptureManifestDiffAnalysisTests
{
    [TestMethod]
    public void Constructor_TwoArgumentShape_PreservesConstructionAndDeconstruction()
    {
        RankingDiffResult result = new(0.0, 0.0, 0.0, []);
        IReadOnlyList<string> warnings = ["warning"];
        CaptureManifestDiffAnalysis analysis = new(result, warnings);

        (RankingDiffResult deconstructedResult, IReadOnlyList<string> deconstructedWarnings) = analysis;

        deconstructedResult.Should().BeSameAs(result);
        deconstructedWarnings.Should().BeSameAs(warnings);
        analysis.Metric.Should().Be(MetricInfo.Cpu);
        (analysis with { Metric = MetricInfo.CpuSamples }).Metric.Should().Be(MetricInfo.CpuSamples);
    }
}