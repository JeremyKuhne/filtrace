// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing.Readers;

namespace Filtrace.Tracing;

[TestClass]
public sealed class CpuSampleWeightingTests
{
    [TestMethod]
    [DataRow(10_000, 1.0)]
    [DataRow(1_250, 0.125)]
    public void GetSampleWeight_RecordedConstantInterval_UsesMilliseconds(
        int interval100Nanoseconds,
        double expectedMSec)
    {
        CpuSampleWeighting weighting = new();

        weighting.ObserveInterval(opcode: 73, sampleSource: 0, interval100Nanoseconds);

        weighting.GetSampleWeight().Should().Be(expectedMSec);
        weighting.GetSampleWeight().Should().Be(expectedMSec);
        weighting.HasCompleteIntervalEvidence.Should().BeTrue();
        weighting.Intervals.Should().Equal(new CpuSampleIntervalSegment(expectedMSec, 2));
    }

    [TestMethod]
    public void GetSampleWeight_IntervalChanges_UsesIntervalActiveAtEachSample()
    {
        CpuSampleWeighting weighting = new();

        weighting.ObserveInterval(opcode: 73, sampleSource: 0, newInterval100Nanoseconds: 10_000);
        double first = weighting.GetSampleWeight();
        weighting.ObserveInterval(opcode: 72, sampleSource: 0, newInterval100Nanoseconds: 1_250);
        double second = weighting.GetSampleWeight();

        first.Should().Be(1.0);
        second.Should().Be(0.125);
        weighting.HasCompleteIntervalEvidence.Should().BeTrue();
        weighting.Intervals.Should().Equal(
            new CpuSampleIntervalSegment(1.0, 1),
            new CpuSampleIntervalSegment(0.125, 1));
    }

    [TestMethod]
    public void GetSampleWeight_RepeatedSamples_DoesNotAllocatePerSample()
    {
        const int sampleCount = 10_000;
        CpuSampleWeighting weighting = new();
        weighting.ObserveInterval(opcode: 73, sampleSource: 0, newInterval100Nanoseconds: 1_250);
        weighting.GetSampleWeight();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        double totalWeight = 0.0;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            totalWeight += weighting.GetSampleWeight();
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        allocatedBytes.Should().Be(0);
        totalWeight.Should().Be(sampleCount * 0.125);
        weighting.Intervals.Should().Equal(new CpuSampleIntervalSegment(0.125, sampleCount + 1));
    }

    [TestMethod]
    public void GetSampleWeight_AtRetainedSegmentLimit_RetainsEverySegment()
    {
        CpuSampleWeighting weighting = new();

        for (int segment = 0; segment < CpuSampleWeighting.MaximumRetainedIntervalSegments; segment++)
        {
            int interval = segment % 2 == 0 ? 10_000 : 1_250;
            weighting.ObserveInterval(opcode: 72, sampleSource: 0, interval);
            weighting.GetSampleWeight();
        }

        weighting.Intervals.Should().HaveCount(CpuSampleWeighting.MaximumRetainedIntervalSegments);
        weighting.OmittedIntervalSegmentCount.Should().Be(0);
        weighting.OmittedIntervalSampleCount.Should().Be(0);
    }

    [TestMethod]
    public void GetSampleWeight_OneSegmentPastLimit_OmitsOnlyTheOverflowSegment()
    {
        CpuSampleWeighting weighting = new();

        for (int segment = 0; segment <= CpuSampleWeighting.MaximumRetainedIntervalSegments; segment++)
        {
            int interval = segment % 2 == 0 ? 10_000 : 1_250;
            weighting.ObserveInterval(opcode: 72, sampleSource: 0, interval);
            weighting.GetSampleWeight();
        }

        weighting.Intervals.Should().HaveCount(CpuSampleWeighting.MaximumRetainedIntervalSegments);
        weighting.OmittedIntervalSegmentCount.Should().Be(1);
        weighting.OmittedIntervalSampleCount.Should().Be(1);
    }

    [TestMethod]
    public void GetSampleWeight_ConsecutiveSamplesAfterLimit_CountsOneOmittedSegment()
    {
        CpuSampleWeighting weighting = new();
        for (int segment = 0; segment < CpuSampleWeighting.MaximumRetainedIntervalSegments; segment++)
        {
            int interval = segment % 2 == 0 ? 10_000 : 1_250;
            weighting.ObserveInterval(opcode: 72, sampleSource: 0, interval);
            weighting.GetSampleWeight();
        }

        weighting.ObserveInterval(opcode: 72, sampleSource: 0, newInterval100Nanoseconds: 10_000);
        weighting.GetSampleWeight();
        weighting.GetSampleWeight();
        weighting.GetSampleWeight();
        weighting.ObserveInterval(opcode: 72, sampleSource: 0, newInterval100Nanoseconds: 1_250);
        weighting.GetSampleWeight();

        weighting.OmittedIntervalSegmentCount.Should().Be(2);
        weighting.OmittedIntervalSampleCount.Should().Be(4);
        weighting.Intervals.Should().HaveCount(CpuSampleWeighting.MaximumRetainedIntervalSegments);
    }

    [TestMethod]
    public void GetSampleWeight_LargeAlternatingIntervals_BoundsMetadataAndPreservesWeights()
    {
        const int sampleCount = 100_000;
        CpuSampleWeighting weighting = new();
        double totalWeight = 0.0;

        for (int sample = 0; sample < sampleCount; sample++)
        {
            int interval = sample % 2 == 0 ? 10_000 : 1_250;
            weighting.ObserveInterval(opcode: 72, sampleSource: 0, interval);
            totalWeight += weighting.GetSampleWeight();
        }

        weighting.Intervals.Should().HaveCount(CpuSampleWeighting.MaximumRetainedIntervalSegments);
        weighting.OmittedIntervalSegmentCount.Should().Be(sampleCount - CpuSampleWeighting.MaximumRetainedIntervalSegments);
        weighting.OmittedIntervalSampleCount.Should().Be(sampleCount - CpuSampleWeighting.MaximumRetainedIntervalSegments);
        totalWeight.Should().Be((sampleCount / 2 * 1.0) + (sampleCount / 2 * 0.125));
        weighting.HasCompleteIntervalEvidence.Should().BeTrue();
    }

    [TestMethod]
    public void GetSampleWeight_MissingInterval_UsesRawCountAndReportsIncompleteEvidence()
    {
        CpuSampleWeighting weighting = new();

        double weight = weighting.GetSampleWeight();

        weight.Should().Be(1.0);
        weighting.HasCompleteIntervalEvidence.Should().BeFalse();
        weighting.UnknownIntervalSampleCount.Should().Be(1);
        weighting.Intervals.Should().BeEmpty();
    }

    [TestMethod]
    public void GetSampleWeight_IntervalRecordedAfterSample_RemainsIncomplete()
    {
        CpuSampleWeighting weighting = new();

        weighting.GetSampleWeight();
        weighting.ObserveInterval(opcode: 73, sampleSource: 0, newInterval100Nanoseconds: 1_250);

        weighting.GetSampleWeight().Should().Be(0.125);
        weighting.HasCompleteIntervalEvidence.Should().BeFalse();
    }

    [TestMethod]
    public void GetSampleWeight_CollectionEnd_DoesNotApplyRestoredInterval()
    {
        CpuSampleWeighting weighting = new();
        weighting.ObserveInterval(opcode: 73, sampleSource: 0, newInterval100Nanoseconds: 1_250);
        weighting.GetSampleWeight().Should().Be(0.125);

        weighting.ObserveInterval(opcode: 74, sampleSource: 0, newInterval100Nanoseconds: 10_000);

        weighting.GetSampleWeight().Should().Be(0.125);
        weighting.Intervals.Should().Equal(new CpuSampleIntervalSegment(0.125, 2));
    }

    [TestMethod]
    [DataRow(72, 0)]
    [DataRow(73, -1)]
    public void GetSampleWeight_RecognizedNonpositiveIntervalAfterValid_UsesRawCount(
        int opcode,
        int interval100Nanoseconds)
    {
        CpuSampleWeighting weighting = new();
        weighting.ObserveInterval(opcode: 73, sampleSource: 0, newInterval100Nanoseconds: 1_250);
        weighting.GetSampleWeight().Should().Be(0.125);

        weighting.ObserveInterval(opcode, sampleSource: 0, interval100Nanoseconds);

        weighting.GetSampleWeight().Should().Be(1.0);
        weighting.HasCompleteIntervalEvidence.Should().BeFalse();
        weighting.UnknownIntervalSampleCount.Should().Be(1);
        weighting.Intervals.Should().Equal(new CpuSampleIntervalSegment(0.125, 1));
    }

    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(32, 1)]
    [DataRow(33, 2)]
    public void GetSampleWeight_RestoredIntervalAfterUnknown_StartsNewSegment(
        int initialSegmentCount,
        int expectedOmittedSegments)
    {
        CpuSampleWeighting weighting = new();
        int lastInterval = 0;
        for (int segment = 0; segment < initialSegmentCount; segment++)
        {
            lastInterval = segment % 2 == 0 ? 1_250 : 10_000;
            weighting.ObserveInterval(opcode: 72, sampleSource: 0, lastInterval);
            weighting.GetSampleWeight();
        }

        weighting.ObserveInterval(opcode: 72, sampleSource: 0, newInterval100Nanoseconds: 0);
        weighting.GetSampleWeight().Should().Be(1.0);
        weighting.ObserveInterval(opcode: 72, sampleSource: 0, lastInterval);
        weighting.GetSampleWeight().Should().Be(lastInterval / 10_000.0);

        weighting.UnknownIntervalSampleCount.Should().Be(1);
        weighting.HasCompleteIntervalEvidence.Should().BeFalse();
        weighting.Intervals.Should().HaveCount(Math.Min(initialSegmentCount + 1, CpuSampleWeighting.MaximumRetainedIntervalSegments));
        weighting.Intervals.Should().OnlyContain(static interval => interval.SampleCount == 1);
        weighting.OmittedIntervalSegmentCount.Should().Be(expectedOmittedSegments);
        weighting.OmittedIntervalSampleCount.Should().Be(expectedOmittedSegments);
    }

    [TestMethod]
    [DataRow(71, 0, 10_000)]
    [DataRow(73, 1, 10_000)]
    [DataRow(73, 0, 0)]
    [DataRow(73, 0, -1)]
    public void GetSampleWeight_InvalidOrForeignIntervalRecord_RemainsUnknown(
        int opcode,
        int sampleSource,
        int interval100Nanoseconds)
    {
        CpuSampleWeighting weighting = new();

        weighting.ObserveInterval(opcode, sampleSource, interval100Nanoseconds);

        weighting.GetSampleWeight().Should().Be(1.0);
        weighting.HasCompleteIntervalEvidence.Should().BeFalse();
        weighting.UnknownIntervalSampleCount.Should().Be(1);
        weighting.Intervals.Should().BeEmpty();
    }
}