// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing.Readers;

namespace Filtrace.Tracing;

[TestClass]
public sealed class CpuSampleEvidenceTests
{
    [TestMethod]
    public void GetSampleWeight_SampleProfilerAfterEtwInterval_UsesCountsAndNoEtwInterval()
    {
        CpuSampleWeighting weighting = new();
        weighting.ObserveInterval(opcode: 73, sampleSource: 0, newInterval100Nanoseconds: 1_250);
        CpuSampleEvidence evidence = default;

        evidence.GetSampleWeight(weighting, sampleProfiler: true).Should().Be(1.0);

        CpuSampleProvenance provenance = evidence.CreateProvenance(weighting, sampleCount: 1);
        provenance.WeightUnit.Should().Be("samples");
        provenance.Source.Should().Be(CpuSampleEvidence.SampleProfilerSource);
        provenance.TimeWeightsEstablished.Should().BeFalse();
        provenance.UnknownIntervalSampleCount.Should().Be(1);
        provenance.Intervals.Should().BeEmpty();
        evidence.Warning.Should().Contain("not on-core CPU milliseconds")
            .And.Contain("blocked-time percentages");
    }

    [TestMethod]
    public void CreateProvenance_MixedEtwAndSampleProfiler_UsesRawCountsAndKeepsOnlyEtwIntervalEvidence()
    {
        CpuSampleWeighting weighting = new();
        weighting.ObserveInterval(opcode: 73, sampleSource: 0, newInterval100Nanoseconds: 1_250);
        CpuSampleEvidence evidence = default;

        evidence.GetSampleWeight(weighting, sampleProfiler: false).Should().Be(0.125);
        evidence.GetSampleWeight(weighting, sampleProfiler: true).Should().Be(1.0);

        CpuSampleProvenance provenance = evidence.CreateProvenance(weighting, sampleCount: 2);
        provenance.WeightUnit.Should().Be("samples");
        provenance.Source.Should().Be(CpuSampleEvidence.MixedSource);
        provenance.TimeWeightsEstablished.Should().BeFalse();
        provenance.UnknownIntervalSampleCount.Should().Be(1);
        provenance.Intervals.Should().Equal(new CpuSampleIntervalSegment(0.125, 1));
        evidence.Warning.Should().Contain("Mixed ETW sampled-profile and SampleProfiler")
            .And.Contain("weights are raw counts from different samplers");
    }

    [TestMethod]
    public void GetSampleWeight_EtwOnly_UsesTraceRecordedIntervalAndRetainsExistingWarning()
    {
        CpuSampleWeighting weighting = new();
        weighting.ObserveInterval(opcode: 73, sampleSource: 0, newInterval100Nanoseconds: 1_250);
        CpuSampleEvidence evidence = default;

        evidence.GetSampleWeight(weighting, sampleProfiler: false).Should().Be(0.125);

        CpuSampleProvenance provenance = evidence.CreateProvenance(weighting, sampleCount: 1);
        provenance.WeightUnit.Should().Be("ms");
        provenance.Source.Should().Be("etw-perfinfo");
        provenance.TimeWeightsEstablished.Should().BeTrue();
        provenance.UnknownIntervalSampleCount.Should().Be(0);
        evidence.Warning.Should().BeNull();

        List<string> warnings = [];
        evidence.AddWarnings(warnings, weighting, provenance, sampleCount: 1);
        warnings.Should().ContainSingle()
            .Which.Should().Be("CPU sample weights use the trace-recorded ETW PerfInfo interval (0.125 ms).");
    }
}
