// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class TraceCapabilitiesTests
{
    [TestMethod]
    public void AnalysesFor_NetTrace_ListsEventPipeAnalyses()
    {
        IReadOnlyList<string> analyses = TraceCapabilities.AnalysesFor(TraceFormat.NetTrace);

        analyses.Should().Contain("cpu");
        analyses.Should().Contain("alloc");
        analyses.Should().Contain("exceptions");
        analyses.Should().Contain("contention");
        analyses.Should().Contain("wait");
        analyses.Should().Contain("gcstats");
        analyses.Should().Contain("jitstats");
        analyses.Should().Contain("threadpool");

        // Thread time, the runtime-work classification, the process inventory, and the
        // disk-I/O report are ETW-only.
        analyses.Should().NotContain("threadtime");
        analyses.Should().NotContain("classify");
        analyses.Should().NotContain("diskio");
    }

    [TestMethod]
    public void AnalysesFor_Etl_ListsEtwAnalyses()
    {
        IReadOnlyList<string> analyses = TraceCapabilities.AnalysesFor(TraceFormat.Etl);

        analyses.Should().Contain("cpu");
        analyses.Should().Contain("threadtime");
        analyses.Should().Contain("classify");
        analyses.Should().Contain("processes");
        analyses.Should().Contain("diskio");

        // Allocation, exceptions, contention, wait, and the GC / JIT / thread-pool reports are EventPipe-only.
        analyses.Should().NotContain("alloc");
        analyses.Should().NotContain("exceptions");
        analyses.Should().NotContain("contention");
        analyses.Should().NotContain("wait");
        analyses.Should().NotContain("gcstats");
        analyses.Should().NotContain("jitstats");
        analyses.Should().NotContain("threadpool");

        // The raw events query is .nettrace-only (the events verb and trace_query_events
        // reject an .etl), so it is not listed as an Etl analysis.
        analyses.Should().NotContain("events");
    }

    [TestMethod]
    public void AnalysesFor_Speedscope_IsCpuOnly()
    {
        TraceCapabilities.AnalysesFor(TraceFormat.Speedscope).Should().Equal("cpu");
    }
}
