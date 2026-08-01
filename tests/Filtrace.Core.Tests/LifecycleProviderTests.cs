// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class LifecycleProviderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string EtwTrace => FixturePath("etw.etl");

    // The ETW fixture captured a BenchmarkDotNet job process that launched a console
    // host, and the trim kept both Process/Start and Process/Stop for each - so it is a
    // real observed parent-and-child invocation, not a clipped one.
    private static LifecycleResult LoadHotLoop(
        IReadOnlyList<string>? images = null,
        List<string>? warnings = null) =>
        new LifecycleProvider().Read(EtwTrace, ScopeRequest.ForProcess("HotLoop"), images, warnings);

    [TestMethod]
    public void Read_NamedRoot_ReportsOneFullyObservedInvocation()
    {
        LifecycleResult result = LoadHotLoop();

        result.Scope.Should().Be("HotLoop");
        result.InvocationCount.Should().Be(1);
        result.MeasuredCount.Should().Be(1);
        result.Invocations.Should().ContainSingle();
        result.Invocations[0].Ordinal.Should().Be(1);
        result.Invocations[0].Measurable.Should().BeTrue();
    }

    [TestMethod]
    public void Read_NamedRoot_ObservesBothEdgesOfTheRoot()
    {
        LifecycleProcess root = LoadHotLoop().Invocations[0].Root;

        root.StartObserved.Should().BeTrue("the capture recorded the root's Process/Start");
        root.StopObserved.Should().BeTrue("the capture recorded the root's Process/Stop");
        root.StartMs.Should().BeGreaterThan(0.0);
        root.LifetimeMs.Should().BeApproximately(root.StopMs - root.StartMs, 0.0001);
    }

    [TestMethod]
    public void Read_NamedRoot_FindsTheLaunchedChild()
    {
        LifecycleInvocation invocation = LoadHotLoop().Invocations[0];

        invocation.Children.Should().NotBeEmpty("the job process launched a console host");
        invocation.Children.Should().OnlyContain(child => child.ProcessId != invocation.Root.ProcessId);
        invocation.Children[0].StartMs.Should().BeGreaterThan(invocation.Root.StartMs);
    }

    [TestMethod]
    public void Read_NamedRoot_PhasesPartitionTheRootLifetime()
    {
        LifecycleInvocation invocation = LoadHotLoop().Invocations[0];

        invocation.RootStartToChildStartMs.Should().NotBeNull();
        invocation.ChildSpanMs.Should().NotBeNull();
        invocation.ChildStopToRootStopMs.Should().NotBeNull();

        // The three phases run from the root's start to its stop with no gap, whichever
        // side of the root's exit the last child landed on.
        double sum = invocation.RootStartToChildStartMs!.Value
            + invocation.ChildSpanMs!.Value
            + invocation.ChildStopToRootStopMs!.Value;
        sum.Should().BeApproximately(invocation.Root.LifetimeMs, 0.0001);
    }

    [TestMethod]
    public void Read_NamedRoot_SummarizesEveryPhaseOverTheMeasuredInvocations()
    {
        LifecycleResult result = LoadHotLoop();

        result.Phases.Should().HaveCount(4);
        result.Phases.Select(static phase => phase.Phase).Should().Equal(
            "root lifetime",
            "root start to first child",
            "child span",
            "last child stop to root stop");

        // One measured invocation, so every phase is that invocation's own value.
        result.Phases.Should().OnlyContain(phase => phase.Count == result.MeasuredCount);
        result.Phases.Should().OnlyContain(phase => phase.MinimumMs <= phase.MedianMs);
        result.Phases.Should().OnlyContain(phase => phase.MedianMs <= phase.MaximumMs);
    }

    [TestMethod]
    public void Read_NamedRoot_ReportsSampledCpuSeparatelyFromWallClock()
    {
        LifecycleResult result = LoadHotLoop();

        result.TotalRootCpuMs.Should().BeGreaterThan(0.0);

        // The point of the report: the root's wall clock is not its sampled CPU, and the
        // gap is the blocked time a CPU ranking cannot show.
        result.Invocations[0].Root.LifetimeMs.Should().BeGreaterThan(result.Invocations[0].Root.CpuMs);
    }

    [TestMethod]
    public void Read_WithImages_TimesTheLoadOffsetFromTheRootStart()
    {
        LifecycleResult result = LoadHotLoop(["ntdll"]);

        result.ImageMilestones.Should().ContainSingle();
        LifecycleImageMilestone milestone = result.ImageMilestones[0];
        milestone.Module.Should().Be("ntdll");
        milestone.Count.Should().Be(1);
        milestone.MedianOffsetMs.Should().BeGreaterThan(0.0);
        milestone.MedianOffsetMs.Should().BeLessThan(result.Invocations[0].Root.LifetimeMs);
    }

    [TestMethod]
    public void Read_WithoutImages_ReportsNoMilestones()
    {
        LoadHotLoop().ImageMilestones.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_UnmatchedName_ReturnsAnEmptyReportRatherThanThrowing()
    {
        LifecycleResult result = new LifecycleProvider().Read(
            EtwTrace,
            ScopeRequest.ForProcess("no-such-process-name"));

        result.InvocationCount.Should().Be(0);
        result.MeasuredCount.Should().Be(0);
        result.Invocations.Should().BeEmpty();
        result.Phases.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_ExactPid_SelectsTheSameInvocationAsTheName()
    {
        int rootId = LoadHotLoop().Invocations[0].Root.ProcessId;

        LifecycleResult byId = new LifecycleProvider().Read(
            EtwTrace,
            ScopeRequest.ForProcessIds([rootId]));

        byId.InvocationCount.Should().Be(1);
        byId.Invocations[0].Root.ProcessId.Should().Be(rootId);
        byId.Scope.Should().Be($"pids {rootId}");
    }

    [TestMethod]
    public void Read_ChildrenAreNeverReportedAsTheirOwnInvocation()
    {
        // Descendants define the phases, so folding them into the root set would turn one
        // invocation into several and make every phase meaningless.
        LifecycleResult result = LoadHotLoop();
        IEnumerable<int> childIds = result.Invocations.SelectMany(
            static invocation => invocation.Children.Select(static child => child.ProcessId));

        result.Invocations.Select(static invocation => invocation.Root.ProcessId)
            .Should().NotIntersectWith(childIds);
    }

    [TestMethod]
    public void Read_AutomaticScope_UsesTheBusiestProcess()
    {
        LifecycleResult result = new LifecycleProvider().Read(EtwTrace);

        result.Scope.Should().NotBeEmpty();
        result.InvocationCount.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Read_MissingFile_Throws()
    {
        Action read = () => new LifecycleProvider().Read(FixturePath("no-such-trace.etl"));

        read.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    public void Read_EmptyPath_Throws()
    {
        Action read = () => new LifecycleProvider().Read("");

        read.Should().Throw<ArgumentException>();
    }
}
