// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;

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
}