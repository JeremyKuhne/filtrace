// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli;

[TestClass]
public sealed class InfoTextRendererTests
{
    [TestMethod]
    public void Render_LegacyViewWithoutAnalyses_UsesAvailableAnalyses()
    {
        TraceInfoView view = new(
            "/trace.json",
            "Speedscope",
            10.0,
            10,
            1.0,
            [],
            ["cpu"]);
        StringWriter output = new();

        InfoTextRenderer.Render(new AnalysisResult<TraceInfoView>(view), output);

        output.ToString().Should().Contain("analyses:").And.Contain("  cpu");
    }

    [TestMethod]
    public void Render_LegacyTraceInfoMappedToView_UsesAvailableAnalyses()
    {
        TraceInfo info = new(
            "/trace.nettrace",
            TraceFormat.NetTrace,
            10.0,
            10,
            1.0,
            [],
            [],
            ["cpu", "alloc"]);
        TraceInfoView view = TraceInfoView.FromTraceInfo(info, null);
        StringWriter output = new();

        InfoTextRenderer.Render(new AnalysisResult<TraceInfoView>(view), output);

        output.ToString().Should().Contain("analyses:").And.Contain("  cpu, alloc");
    }

    [TestMethod]
    public void Render_Analyses_UsesAvailableAnalysisOrder()
    {
        TraceInfoView view = new(
            "/trace.nettrace",
            "NetTrace",
            10.0,
            10,
            1.0,
            [],
            ["cpu", "alloc"])
        {
            Analyses = new Dictionary<string, AnalysisAvailabilityView>
            {
                ["alloc"] = new("enabled", 2),
                ["cpu"] = new("enabled", 4)
            }
        };
        StringWriter output = new();

        InfoTextRenderer.Render(new AnalysisResult<TraceInfoView>(view), output);

        string text = output.ToString();
        text.IndexOf("  cpu:", StringComparison.Ordinal).Should().BeLessThan(
            text.IndexOf("  alloc:", StringComparison.Ordinal));
    }
}