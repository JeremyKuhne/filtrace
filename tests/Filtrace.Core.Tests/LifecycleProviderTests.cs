// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;

namespace Filtrace.Tracing.Providers;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class LifecycleProviderTests
{
    private static string FixturePath(string name) =>
        Path.Join(AppContext.BaseDirectory, "Fixtures", name);

    private static string EtwTrace => FixturePath("etw.etl");

    // The ETW fixture captured a BenchmarkDotNet job process that launched a console
    // host, and the trim kept both Process/Start and Process/Stop for each - so it is a
    // real observed parent-and-child invocation, not a clipped one.
    private static LifecycleResult LoadHotLoop(
        IReadOnlyList<string>? images = null,
        List<string>? warnings = null) =>
            new LifecycleProvider().Read(EtwTrace, ScopeRequest.ForProcess("HotLoop"), images, warnings);

    [TestMethod]
    public void LimitDetail_MoreInvocationsThanTheBudget_ClampsTheSerializedResponse()
    {
        // One invocation carries its root plus every child, so a wide capture matrix
        // reaches the budget on row size as well as row count. No committed fixture is
        // that wide, so the report is built directly.
        LifecycleInvocation[] invocations = [.. Enumerable.Range(0, 2_000).Select(static index => new LifecycleInvocation(
            index,
            new LifecycleProcess(1000 + index, "dotnet.exe", 0.0, 120.0, 120.0, 40.0, StartObserved: true, StopObserved: true, 0),
            [new LifecycleProcess(9000 + index, "HotLoopBench.exe", 5.0, 110.0, 105.0, 90.0, StartObserved: true, StopObserved: true, 0)],
            5.0,
            105.0,
            10.0,
            Measurable: true))];

        LifecycleResult wide = new("dotnet", 2_000, 2_000, 80000.0, 180000.0, [], invocations, []);

        LifecycleResult limited = LifecycleProvider.LimitDetail(wide, top: 100_000, out string? warning);

        limited.Invocations.Count.Should().BeLessThan(wide.Invocations.Count);
        limited.InvocationCount.Should().Be(wide.InvocationCount, "the medians still cover every invocation");
        warning.Should().NotBeNull();

        string serialized = OutputJson.Serialize(new AnalysisResult<LifecycleResult>(limited));
        OutputBudget.EstimateTokens(serialized).Should().BeLessThan(OutputBudget.DefaultCeilingTokens);
    }

    [TestMethod]
    public void LimitDetail_WithinTheBudget_ReturnsTheReportUnchanged()
    {
        LifecycleResult full = LoadHotLoop();

        LifecycleProvider.LimitDetail(full, top: 100_000, out string? warning).Should().BeSameAs(full);
        warning.Should().BeNull();
    }

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
    public void Read_NamedRoot_EveryRootSatisfiesTheSelectorItself()
    {
        // The resolved scope is a set of process ids, which cannot separate an id from a
        // later, unrelated process that reused it - so every reported root must still
        // satisfy the name selector in its own right.
        LifecycleResult result = LoadHotLoop();

        result.Invocations.Should().OnlyContain(
            invocation => invocation.Root.Name.Contains("HotLoop", StringComparison.OrdinalIgnoreCase));
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

        // The selector resolved, it just matched nothing - which is a scope problem, and
        // has to read differently from a trace with no usable process table at all.
        result.Scope.Should().Be("no-such-process-name");
    }

    [TestMethod]
    public void DescribeCoverage_UnmatchedSelector_NamesTheScope()
    {
        LifecycleResult result = new("myapp", 0, 0, 0, 0, [], [], []);

        LifecycleProvider.DescribeCoverage(result).Should().ContainSingle()
            .Which.Should().Contain("No process matching 'myapp'");
    }

    [TestMethod]
    public void DescribeCoverage_UnresolvedSelector_PointsAtTheCaptureNotTheScope()
    {
        // An empty scope means no selector could be resolved at all, so telling the caller
        // their name did not match would send them to fix the wrong thing.
        LifecycleResult result = new(string.Empty, 0, 0, 0, 0, [], [], []);

        string warning = LifecycleProvider.DescribeCoverage(result).Should().ContainSingle().Subject;
        warning.Should().Contain("no process the report could use as an invocation root");
        warning.Should().Contain("Process kernel keyword");
        warning.Should().NotContain("No process matching");
    }

    [TestMethod]
    public void DescribeCoverage_FullyObserved_ReportsNothing()
    {
        LifecycleResult result = new("myapp", 3, 3, 0, 0, [], [], []);

        LifecycleProvider.DescribeCoverage(result).Should().BeEmpty();
    }

    [TestMethod]
    public void DescribeCoverage_PartiallyClipped_CountsTheExcluded()
    {
        LifecycleResult result = new("myapp", 5, 3, 0, 0, [], [], []);

        LifecycleProvider.DescribeCoverage(result).Should().ContainSingle()
            .Which.Should().Contain("2 of 5 invocations were clipped");
    }

    [TestMethod]
    public void DescribeCoverage_NothingObserved_SaysTheLifetimesAreLowerBounds()
    {
        LifecycleResult result = new("myapp", 2, 0, 0, 0, [], [], []);

        LifecycleProvider.DescribeCoverage(result).Should().ContainSingle()
            .Which.Should().Contain("lower bound");
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
