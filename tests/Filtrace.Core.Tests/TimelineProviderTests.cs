// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class TimelineProviderTests
{
    private static string FixturePath(string name) =>
        Path.Join(AppContext.BaseDirectory, "Fixtures", name);

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
    public void ReadSnapshot_ExceptionsFixture_ReturnsBoundedCrossLaneEvidence()
    {
        TimelineResult result = new TimelineProvider().ReadSnapshot(
            FixturePath("exceptions.nettrace"),
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs);

        result.Mode.Should().Be("snapshot");
        result.BucketCount.Should().Be(1);
        result.FromMs.Should().Be(0.0);
        result.ToMs.Should().BeGreaterThan(0.0);
        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Events.EventCount.Should().BeGreaterThan(0);
        result.Snapshot.Events.TypeCount.Should().BeGreaterThan(TimelineProvider.SnapshotDetailLimit);
        result.Snapshot.Events.Types.Should().HaveCount(TimelineProvider.SnapshotDetailLimit);
        result.Snapshot.Cpu.SampleCount.Should().BeGreaterThan(0);
        result.Snapshot.Cpu.Methods.Should().NotBeEmpty()
            .And.HaveCountLessThanOrEqualTo(TimelineProvider.SnapshotDetailLimit);
        result.Snapshot.Exceptions.ExceptionCount.Should().BeGreaterThan(0);
        result.Snapshot.Exceptions.Types.Should().NotBeEmpty()
            .And.HaveCountLessThanOrEqualTo(TimelineProvider.SnapshotDetailLimit);
    }

    [TestMethod]
    public void ReadSnapshot_AllocationFixture_ReturnsGcAndAllocationEvidence()
    {
        TimelineResult result = new TimelineProvider().ReadSnapshot(
            Alloc,
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs);

        result.Snapshot!.Gc.CollectionCount.Should().BeGreaterThan(0);
        result.Snapshot.Gc.Collections.Should().NotBeEmpty()
            .And.HaveCountLessThanOrEqualTo(TimelineProvider.SnapshotDetailLimit);
        result.Snapshot.Alloc.TickCount.Should().BeGreaterThan(0);
        result.Snapshot.Alloc.Bytes.Should().BeGreaterThan(0);
        result.Snapshot.Alloc.Types.Should().NotBeEmpty()
            .And.HaveCountLessThanOrEqualTo(TimelineProvider.SnapshotDetailLimit);
    }

    [TestMethod]
    public void ReadSnapshot_JitFixture_ReturnsJittedMethods()
    {
        TimelineResult result = new TimelineProvider().ReadSnapshot(
            FixturePath("jit.nettrace"),
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs);

        result.Snapshot!.Jit.CompilationCount.Should().BeGreaterThan(0);
        result.Snapshot.Jit.MethodCount.Should().BeGreaterThan(0);
        result.Snapshot.Jit.Methods.Should().NotBeEmpty()
            .And.HaveCountLessThanOrEqualTo(TimelineProvider.SnapshotDetailLimit);
    }

    [TestMethod]
    public void ReadSnapshot_AtAndHalfWindow_ReportExactResolvedWindow()
    {
        TimelineResult result = new TimelineProvider().ReadSnapshot(Alloc, atMs: 10.0, halfWindowMs: 2.0);

        result.FromMs.Should().Be(8.0);
        result.ToMs.Should().Be(12.0);
        result.Snapshot!.AtMs.Should().Be(10.0);
    }

    [TestMethod]
    public void Read_UnknownProcessId_CarriesScopeWarningInBothModes()
    {
        ScopeRequest scope = ScopeRequest.ForProcessIds([999_999]);

        TimelineResult buckets = new TimelineProvider().Read(Alloc, scope: scope);
        TimelineResult snapshot = new TimelineProvider().ReadSnapshot(
            Alloc,
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs,
            scope: scope);

        buckets.ScopeWarnings.Should().ContainSingle(warning =>
            warning.Contains("not found in this trace", StringComparison.Ordinal));
        snapshot.ScopeWarnings.Should().ContainSingle(warning =>
            warning.Contains("not found in this trace", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReadSnapshot_PauseStartedBeforeWindow_IncludesCollectionAndClipsTotal()
    {
        TimelineResult result = new TimelineProvider().ReadSnapshot(Alloc, atMs: 20.65, halfWindowMs: 0.02);

        result.Snapshot!.Gc.CollectionCount.Should().Be(1);
        result.Snapshot.Gc.Collections.Should().ContainSingle();
        result.Snapshot.Gc.Collections[0].StartMs.Should().BeGreaterThan(result.ToMs);
        result.Snapshot.Gc.TotalPauseMs.Should().BeGreaterThan(0.0)
            .And.BeLessThanOrEqualTo(result.ToMs - result.FromMs);
    }

    [TestMethod]
    public void ReadSnapshot_PauseExtendsPastWindow_ClipsTotalButKeepsFullDetail()
    {
        TimelineResult result = new TimelineProvider().ReadSnapshot(Alloc, atMs: 20.69, halfWindowMs: 0.01);

        SnapshotGcSummary gc = result.Snapshot!.Gc;
        gc.CollectionCount.Should().Be(1);
        gc.TotalPauseMs.Should().BeApproximately(0.02, 0.001);
        gc.MaxPauseMs.Should().BeApproximately(0.02, 0.001);
        gc.Collections.Should().ContainSingle();
        gc.Collections[0].PauseMs.Should().BeGreaterThan(gc.TotalPauseMs, "detail retains the collection's full pause");
    }

    [TestMethod]
    [DataRow(double.NaN, 100.0)]
    [DataRow(-1.0, 100.0)]
    [DataRow(0.0, 0.0)]
    [DataRow(10.0, 0.001)]
    [DataRow(0.0, double.PositiveInfinity)]
    public void ReadSnapshot_InvalidGeometry_Throws(double atMs, double halfWindowMs)
    {
        Action act = () => new TimelineProvider().ReadSnapshot(Alloc, atMs, halfWindowMs);

        act.Should().Throw<ArgumentOutOfRangeException>();
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
        all.AppliedProcessScope.Should().Be(AppliedProcessScope.AllProcesses);
        scoped.Process.Should().Contain("HotLoopBench", "an explicit selector reports the scope it resolved to");
        scoped.AppliedProcessScope.Should().NotBeNull();
        scoped.AppliedProcessScope!.Mode.Should().Be("name");
        scoped.AppliedProcessScope.Process.Should().Be("HotLoopBench");
        scoped.AppliedProcessScope.IncludeChildren.Should().BeTrue();

        long allSamples = all.Cpu!.Sum(static b => (long)b.SampleCount);
        long scopedSamples = scoped.Cpu!.Sum(static b => (long)b.SampleCount);
        scopedSamples.Should().BeGreaterThan(0);
        scopedSamples.Should().BeLessThan(allSamples, "scoping to one tree drops the other processes' samples");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void ReadSnapshot_EtlFixture_ProcessSelectorScopesTheSnapshot()
    {
        string etl = FixturePath("etw.etl");
        TimelineResult all = new TimelineProvider().ReadSnapshot(
            etl,
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs,
            scope: ScopeRequest.AllProcesses);
        TimelineResult scoped = new TimelineProvider().ReadSnapshot(
            etl,
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs,
            scope: ScopeRequest.ForProcess("HotLoopBench"));

        all.Process.Should().BeNull();
        all.AppliedProcessScope.Should().Be(AppliedProcessScope.AllProcesses);
        scoped.Process.Should().Contain("HotLoopBench");
        scoped.AppliedProcessScope.Should().NotBeNull();
        scoped.AppliedProcessScope!.Mode.Should().Be("name");
        scoped.AppliedProcessScope.Process.Should().Be("HotLoopBench");
        scoped.Snapshot!.Cpu.SampleCount.Should().BeGreaterThan(0);
        scoped.Snapshot.Cpu.SampleCount.Should().BeLessThan(all.Snapshot!.Cpu.SampleCount);
        scoped.Snapshot.Events.EventCount.Should().BeLessThan(all.Snapshot.Events.EventCount);

        int rootProcessId = scoped.AppliedProcessScope.RootProcessIds.Should().ContainSingle().Subject;
        TimelineResult byId = new TimelineProvider().ReadSnapshot(
            etl,
            atMs: 0.0,
            halfWindowMs: TimelineProvider.MaxSnapshotHalfWindowMs,
            scope: ScopeRequest.ForProcessIds([rootProcessId], includeChildren: false));
        byId.AppliedProcessScope.Should().NotBeNull();
        byId.AppliedProcessScope!.Mode.Should().Be("ids");
        byId.AppliedProcessScope.RequestedProcessIds.Should().Equal(rootProcessId);
        byId.AppliedProcessScope.IncludeChildren.Should().BeFalse();
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
