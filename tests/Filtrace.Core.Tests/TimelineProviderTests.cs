// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class TimelineProviderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // The allocation smoke trace is captured under the GC-verbose profile, so it carries
    // the GC and allocation-tick events two of the lanes read.
    private static string Alloc => FixturePath("alloc.nettrace");

    private static TimelineResult Read(
        string path,
        TimeWindow? window = null,
        IReadOnlyCollection<string>? lanes = null,
        int bucketCount = TimelineProvider.DefaultBucketCount) =>
        new TimelineProvider().Read(path, window, lanes, bucketCount);

    [TestMethod]
    public void Read_Default_ProducesAllLanesAlignedToOneGeometry()
    {
        TimelineResult result = Read(Alloc);

        result.BucketCount.Should().Be(TimelineProvider.DefaultBucketCount);
        result.FromMs.Should().Be(0.0);
        result.ToMs.Should().BeGreaterThan(0.0);
        result.BucketSizeMs.Should().BeGreaterThan(0.0);

        // Every default lane is present and every lane's array is the same length - the
        // shared time axis that lets a spike in one lane be read against the others.
        result.Gc.Should().NotBeNull().And.HaveCount(TimelineProvider.DefaultBucketCount);
        result.Cpu.Should().NotBeNull().And.HaveCount(TimelineProvider.DefaultBucketCount);
        result.Exceptions.Should().NotBeNull().And.HaveCount(TimelineProvider.DefaultBucketCount);
        result.Alloc.Should().NotBeNull().And.HaveCount(TimelineProvider.DefaultBucketCount);
        result.Jit.Should().NotBeNull().And.HaveCount(TimelineProvider.DefaultBucketCount);
    }

    [TestMethod]
    public void Read_AllocFixture_GcAndAllocLanesHaveActivity()
    {
        TimelineResult result = Read(Alloc);

        result.Gc!.Sum(static b => b.Count).Should().BeGreaterThan(0, "the workload triggers collections");
        result.Alloc!.Sum(static b => b.Bytes).Should().BeGreaterThan(0, "the workload allocates");
    }

    [TestMethod]
    public void Read_LanesSelector_BuildsOnlyRequestedLanes()
    {
        TimelineResult result = Read(Alloc, lanes: [TimelineProvider.GcLane, TimelineProvider.AllocLane]);

        result.Gc.Should().NotBeNull();
        result.Alloc.Should().NotBeNull();

        // A lane not asked for is null (not an empty array), so "not requested" reads
        // differently from "requested, nothing happened".
        result.Cpu.Should().BeNull();
        result.Exceptions.Should().BeNull();
        result.Jit.Should().BeNull();
    }

    [TestMethod]
    public void Read_BucketCountBelowMinimum_ClampsToMinimum()
    {
        TimelineResult result = Read(Alloc, bucketCount: 1);

        result.BucketCount.Should().Be(TimelineProvider.MinBucketCount);
        result.Gc.Should().HaveCount(TimelineProvider.MinBucketCount);
    }

    [TestMethod]
    public void Read_BucketCountAboveMaximum_ClampsToMaximum()
    {
        TimelineResult result = Read(Alloc, bucketCount: 10_000);

        result.BucketCount.Should().Be(TimelineProvider.MaxBucketCount);
    }

    [TestMethod]
    public void Read_TimeWindow_BoundsTheGeometry()
    {
        TimelineResult result = Read(Alloc, new TimeWindow(0.0, 10.0), [TimelineProvider.GcLane], bucketCount: 5);

        result.FromMs.Should().Be(0.0);
        result.ToMs.Should().Be(10.0);
        result.BucketCount.Should().Be(5);
        result.BucketSizeMs.Should().BeApproximately(2.0, 0.0001);
    }

    [TestMethod]
    public void Read_ExceptionsFixture_CountsThrowsAndNamesTopType()
    {
        TimelineResult result = new TimelineProvider().Read(
            FixturePath("exceptions.nettrace"), lanes: [TimelineProvider.ExceptionsLane]);

        result.Exceptions!.Sum(static b => (long)b.Count).Should().BeGreaterThan(0);
        result.Exceptions!.Any(static b => b.TopType is not null).Should().BeTrue("a busy bucket names its top type");
    }

    [TestMethod]
    public void Read_NettraceFixture_CountsEventPipeCpuSamplesAlongsideGc()
    {
        // The .nettrace smoke traces are captured under the CPU-sampling profile, so they
        // carry the SampleProfiler's ClrThreadSampleTraceData events the cpu lane counts on
        // the EventPipe side - the cross-platform counterpart to the ETW
        // SampledProfileTraceData path the .etl test exercises. Requesting gc alongside cpu
        // also drives the single combined pass (the runtime analysis and the raw-event
        // tally) that builds both lanes from one scan.
        TimelineResult result = new TimelineProvider().Read(
            FixturePath("exceptions.nettrace"),
            lanes: [TimelineProvider.GcLane, TimelineProvider.CpuLane]);

        result.Cpu!.Sum(static b => (long)b.SampleCount).Should().BeGreaterThan(0, "the capture carries CPU samples");
        result.Gc.Should().NotBeNull("the gc lane was requested in the same pass");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Read_EtlFixture_CountsCpuSamples()
    {
        // Reading an .etl is Windows-only (the ETW -> ETLX conversion); the ETW fixture
        // carries CPU samples the cpu lane counts.
        TimelineResult result = new TimelineProvider().Read(
            FixturePath("etw.etl"), lanes: [TimelineProvider.CpuLane]);

        result.Cpu!.Sum(static b => (long)b.SampleCount).Should().BeGreaterThan(0);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Read_EtlFixture_ProcessSelectorScopesToOneTree()
    {
        string etl = FixturePath("etw.etl");

        // An explicit process selector narrows the lanes to that tree and reports it;
        // --all-processes reads every process and names none. (The committed fixture's
        // busiest process is unnamed, so the automatic scope is a no-op here - the same
        // behavior the CPU reader has on it - which is why this exercises an explicit
        // name rather than the auto default.)
        TimelineResult all = new TimelineProvider().Read(
            etl, lanes: [TimelineProvider.CpuLane], scope: ScopeRequest.AllProcesses);
        TimelineResult scoped = new TimelineProvider().Read(
            etl, lanes: [TimelineProvider.CpuLane], scope: ScopeRequest.ForProcess("HotLoopBench"));

        all.Process.Should().BeNull("--all-processes reads every process");
        scoped.Process.Should().Contain("HotLoopBench", "an explicit selector reports the scope it resolved to");

        long allSamples = all.Cpu!.Sum(static b => (long)b.SampleCount);
        long scopedSamples = scoped.Cpu!.Sum(static b => (long)b.SampleCount);
        scopedSamples.Should().BeGreaterThan(0);
        scopedSamples.Should().BeLessThan(allSamples, "scoping to one tree drops the other processes' samples");
    }

    [TestMethod]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        Action act = () => Read(FixturePath("does-not-exist.nettrace"));

        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void Read_NullOrEmptyPath_ThrowsArgument(string? path)
    {
        Action act = () => Read(path!);

        act.Should().Throw<ArgumentException>();
    }
}
