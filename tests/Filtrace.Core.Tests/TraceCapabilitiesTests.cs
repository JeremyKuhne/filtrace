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

        // The raw event query spans both formats (the events verb and trace_query_events
        // now read an .etl too), so it is listed as an Etl analysis.
        analyses.Should().Contain("events");
    }

    [TestMethod]
    public void AnalysesFor_Speedscope_IsCpuOnly()
    {
        TraceCapabilities.AnalysesFor(TraceFormat.Speedscope).Should().Equal("cpu");
    }

    [TestMethod]
    public void AvailabilityFor_ObservedEvents_ReportsEnabledNonzero()
    {
        IReadOnlyDictionary<string, AnalysisAvailability> availability =
            TraceCapabilities.AvailabilityFor(
                TraceFormat.NetTrace,
                new Dictionary<string, int> { ["alloc"] = 12 });

        availability["alloc"].Should().Be(
            new AnalysisAvailability(true, CaptureStatus.Enabled, 12));
    }

    [TestMethod]
    public void AvailabilityFor_ExplicitEnabledWithoutEvents_ReportsEnabledZero()
    {
        IReadOnlyDictionary<string, AnalysisAvailability> availability =
            TraceCapabilities.AvailabilityFor(
                TraceFormat.NetTrace,
                new Dictionary<string, int>(),
                new Dictionary<string, CaptureStatus> { ["exceptions"] = CaptureStatus.Enabled });

        availability["exceptions"].Should().Be(
            new AnalysisAvailability(true, CaptureStatus.Enabled, 0));
    }

    [TestMethod]
    public void AvailabilityFor_ExplicitDisabled_ReportsNoEventCount()
    {
        IReadOnlyDictionary<string, AnalysisAvailability> availability =
            TraceCapabilities.AvailabilityFor(
                TraceFormat.NetTrace,
                new Dictionary<string, int>(),
                new Dictionary<string, CaptureStatus> { ["wait"] = CaptureStatus.Disabled });

        availability["wait"].Should().Be(
            new AnalysisAvailability(true, CaptureStatus.Disabled, null));
    }

    [TestMethod]
    public void AvailabilityFor_ZeroWithoutMetadata_ReportsUnknown()
    {
        IReadOnlyDictionary<string, AnalysisAvailability> availability =
            TraceCapabilities.AvailabilityFor(TraceFormat.NetTrace, new Dictionary<string, int>());

        availability["threadpool"].Should().Be(
            new AnalysisAvailability(true, CaptureStatus.Unknown, null));
    }

    [TestMethod]
    public void AvailabilityFor_EmptySpeedscope_ReportsCpuEnabledZero()
    {
        IReadOnlyDictionary<string, AnalysisAvailability> availability =
            TraceCapabilities.AvailabilityFor(
                TraceFormat.Speedscope,
                new Dictionary<string, int> { ["cpu"] = 0 });

        availability["cpu"].Should().Be(
            new AnalysisAvailability(true, CaptureStatus.Enabled, 0));
    }

    [TestMethod]
    public void AvailabilityFor_UnsupportedFormat_RemainsSeparateFromCaptureStatus()
    {
        IReadOnlyDictionary<string, AnalysisAvailability> availability =
            TraceCapabilities.AvailabilityFor(
                TraceFormat.Etl,
                new Dictionary<string, int> { ["alloc"] = 12 },
                new Dictionary<string, CaptureStatus> { ["alloc"] = CaptureStatus.Enabled });

        availability["alloc"].Should().Be(
            new AnalysisAvailability(false, CaptureStatus.Unknown, null));
    }
}
