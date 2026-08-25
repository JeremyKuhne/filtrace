// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;

namespace Filtrace.Tracing;

// The selector and scope constructor validation is platform-agnostic (no trace read),
// so it runs everywhere - unlike the ETL-backed scoping tests below.
[TestClass]
public sealed class ProcessScopeValidationTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void NameSelector_NullOrEmptyNameSubstring_ThrowsArgument(string? name)
    {
        Action act = () => _ = new ProcessNameSelector(name!);

        act.Should().Throw<ArgumentException>().WithParameterName("nameSubstring");
    }

    [TestMethod]
    public void NameSelector_NameAtLimit_Succeeds()
    {
        ProcessNameSelector selector = new(new string('x', ProcessNameSelector.MaxNameSubstringLength));

        selector.NameSubstring.Should().HaveLength(ProcessNameSelector.MaxNameSubstringLength);
    }

    [TestMethod]
    public void NameSelector_NameAboveLimit_ThrowsArgument()
    {
        Action act = () => _ = new ProcessNameSelector(new string('x', ProcessNameSelector.MaxNameSubstringLength + 1));

        act.Should().Throw<ArgumentException>().WithParameterName("nameSubstring");
    }

    [TestMethod]
    [DataRow("line\nbreak")]
    [DataRow("escape\u001b")]
    public void NameSelector_ControlCharacter_ThrowsArgument(string name)
    {
        Action act = () => _ = new ProcessNameSelector(name);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("nameSubstring")
            .WithMessage("*control characters*");
    }

    [TestMethod]
    public void Ctor_NameSelector_Succeeds()
    {
        ProcessScope scope = new(new ProcessNameSelector("HotLoopBench"));

        scope.Selector.Should().BeOfType<ProcessNameSelector>()
            .Which.NameSubstring.Should().Be("HotLoopBench");
        scope.IncludeChildren.Should().BeTrue("children are followed by default");
    }

    [TestMethod]
    public void IdSelector_DeduplicatesAndOrders()
    {
        ProcessIdSelector selector = new([5000, 100, 5000]);

        selector.ProcessIds.Should().Equal(100, 5000);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void IdSelector_NonPositiveId_ThrowsArgument(int processId)
    {
        Action act = () => _ = new ProcessIdSelector([processId]);

        act.Should().Throw<ArgumentException>().WithParameterName("processIds");
    }

    [TestMethod]
    public void IdSelector_EmptyIds_ThrowsArgument()
    {
        Action act = () => _ = new ProcessIdSelector([]);

        act.Should().Throw<ArgumentException>().WithParameterName("processIds");
    }
}

// ScopeRequest is the high-level CLI intent; its factory validation is
// platform-agnostic (no trace read), so it runs everywhere.
[TestClass]
public sealed class ScopeRequestTests
{
    [TestMethod]
    public void Auto_HasNoSelectorAndIsNotAllProcesses()
    {
        ScopeRequest.Auto.Selector.Should().BeNull();
        ScopeRequest.Auto.IncludeAll.Should().BeFalse();
        ScopeRequest.Auto.IncludeChildren.Should().BeTrue();
    }

    [TestMethod]
    public void AutoScope_CanExcludeChildren()
    {
        ScopeRequest scope = ScopeRequest.AutoScope(includeChildren: false);

        scope.Selector.Should().BeNull("the busiest process is still chosen automatically");
        scope.IncludeChildren.Should().BeFalse();
        ScopeRequest.AutoScope(includeChildren: true).Should().BeSameAs(ScopeRequest.Auto);
    }

    [TestMethod]
    public void AllProcesses_IsTheOptOut()
    {
        ScopeRequest.AllProcesses.IncludeAll.Should().BeTrue();
        ScopeRequest.AllProcesses.Selector.Should().BeNull();
    }

    [TestMethod]
    public void ForProcess_CarriesTheNameAndChildrenDefault()
    {
        ScopeRequest scope = ScopeRequest.ForProcess("MyApp");

        scope.Selector.Should().BeOfType<ProcessNameSelector>().Which.NameSubstring.Should().Be("MyApp");
        scope.IncludeAll.Should().BeFalse();
        scope.IncludeChildren.Should().BeTrue("children are followed by default");
    }

    [TestMethod]
    public void ForProcess_CanExcludeChildren()
    {
        ScopeRequest.ForProcess("MyApp", includeChildren: false).IncludeChildren.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void ForProcess_NullOrEmptyName_ThrowsArgument(string? name)
    {
        Action act = () => ScopeRequest.ForProcess(name!);

        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void ForProcessIds_CarriesTheIdsAndChildrenDefault()
    {
        ScopeRequest scope = ScopeRequest.ForProcessIds([4242, 17]);

        scope.Selector.Should().BeOfType<ProcessIdSelector>().Which.ProcessIds.Should().Equal(17, 4242);
        scope.IncludeAll.Should().BeFalse();
        scope.IncludeChildren.Should().BeTrue("an id scope defaults to the tree, like a name scope");
    }

    [TestMethod]
    public void ForProcessIds_PreservesSelectorAcrossRefinements()
    {
        ScopeRequest scope = ScopeRequest.ForProcessIds([4242], includeChildren: false)
            .WithActivity("Order")
            .WithTimeWindow(1000.0, null);

        scope.Selector.Should().BeOfType<ProcessIdSelector>().Which.ProcessIds.Should().Equal(4242);
        scope.IncludeChildren.Should().BeFalse();
        scope.ActivityName.Should().Be("Order");
        scope.Window.Should().NotBeNull();
    }
}

// Reading an ETW (.etl) trace uses the Windows-only ETW conversion, so these
// tests are restricted to Windows; on other platforms they are skipped. The
// process-tree scoping logic they cover is platform-agnostic - only the .etl
// read path underneath is not.
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class ProcessScopeTests
{
    private static string EtwFixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "etw.etl");

    // The load path treats a null request as the automatic busiest-process default,
    // so tests that want the whole capture pass ScopeRequest.AllProcesses explicitly.
    private static IReadOnlyList<SampleStack> Load(ScopeRequest? scope) =>
        new TraceLoader().Load(EtwFixture, scope: scope).Source.Samples;

    private static int DistinctProcesses(IReadOnlyList<SampleStack> samples) =>
        samples.Select(static s => s.Process).Distinct(StringComparer.Ordinal).Count();

    [TestMethod]
    public void Read_EtlFixture_ProducesCpuSamples()
    {
        IReadOnlyList<SampleStack> samples = Load(ScopeRequest.AllProcesses);

        // The ETW fixture is a CPU-sampled capture, so the reader yields weighted
        // stacks; at least some carry a resolved "module!method" frame.
        samples.Should().NotBeEmpty();
        samples.Should().Contain(s => s.Frames.Any(f => f.Contains('!', StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Read_EtlFixture_SpansMultipleProcesses()
    {
        IReadOnlyList<SampleStack> samples = Load(ScopeRequest.AllProcesses);

        // The unscoped capture is the BenchmarkDotNet process tree: the host, the job
        // child, and its console host - more than one process.
        DistinctProcesses(samples).Should().BeGreaterThan(1);
        samples.Should().OnlyContain(s => s.Process.Length > 0);
    }

    [TestMethod]
    public void Read_ScopedToJobChild_KeepsOnlyThatProcessAndIsNarrower()
    {
        IReadOnlyList<SampleStack> all = Load(ScopeRequest.AllProcesses);
        IReadOnlyList<SampleStack> scoped = Load(ScopeRequest.ForProcess("HotLoopBench-Job", includeChildren: false));

        // Scoping to the job process alone drops the host and console-host samples.
        scoped.Should().NotBeEmpty();
        scoped.Count.Should().BeLessThan(all.Count);
        DistinctProcesses(scoped).Should().Be(1);
        scoped.Should().OnlyContain(s => s.Process.Contains("Job", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_ScopedToTree_IsASubsetOfTheWholeCapture()
    {
        IReadOnlyList<SampleStack> all = Load(ScopeRequest.AllProcesses);
        IReadOnlyList<SampleStack> jobOnly = Load(ScopeRequest.ForProcess("HotLoopBench-Job", includeChildren: false));
        IReadOnlyList<SampleStack> tree = Load(ScopeRequest.ForProcess("HotLoopBench"));

        // The "HotLoopBench" tree (host + job + descendants) is at least the job child
        // and never more than the whole capture.
        tree.Count.Should().BeGreaterThanOrEqualTo(jobOnly.Count);
        tree.Count.Should().BeLessThanOrEqualTo(all.Count);
        tree.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Read_ScopeIncludingChildren_IsNoNarrowerThanExcludingThem()
    {
        IReadOnlyList<SampleStack> withChildren = Load(ScopeRequest.ForProcess("HotLoopBench", includeChildren: true));
        IReadOnlyList<SampleStack> withoutChildren = Load(ScopeRequest.ForProcess("HotLoopBench", includeChildren: false));

        // Following children can only ever keep more samples, never fewer.
        withChildren.Count.Should().BeGreaterThanOrEqualTo(withoutChildren.Count);
    }

    [TestMethod]
    public void Read_ScopeMatchingNoProcess_YieldsNoSamples()
    {
        IReadOnlyList<SampleStack> scoped = Load(ScopeRequest.ForProcess("no-such-process-name"));

        scoped.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_ScopedToExactIds_MatchesTheEquivalentNameScope()
    {
        IReadOnlyList<SampleStack> byName = Load(ScopeRequest.ForProcess("HotLoopBench-Job", includeChildren: false));
        int[] jobIds = [.. ProcessIdsOf(byName)];

        IReadOnlyList<SampleStack> byId = Load(ScopeRequest.ForProcessIds(jobIds, includeChildren: false));

        // The two selectors are different ways to name the same roots, so the exact-id
        // scope must reproduce the name scope's sample set exactly.
        jobIds.Should().NotBeEmpty();
        byId.Count.Should().Be(byName.Count);
        ProcessIdsOf(byId).Should().BeEquivalentTo(ProcessIdsOf(byName));
    }

    [TestMethod]
    public void Read_ScopedToExactId_ExcludingChildren_KeepsOnlyThatProcess()
    {
        IReadOnlyList<SampleStack> tree = Load(ScopeRequest.ForProcess("HotLoopBench"));
        int busiestId = ProcessIdOf(tree
            .GroupBy(static s => s.Process, StringComparer.Ordinal)
            .OrderByDescending(static g => g.Count())
            .First().Key);

        IReadOnlyList<SampleStack> parentOnly = Load(ScopeRequest.ForProcessIds([busiestId], includeChildren: false));

        // Parent-only is the point of an exact scope: it must keep that process and
        // nothing else, even though the same id with descendants covers more.
        parentOnly.Should().NotBeEmpty();
        ProcessIdsOf(parentOnly).Should().Equal(busiestId);
        parentOnly.Count.Should().BeLessThan(tree.Count);
        Load(ScopeRequest.ForProcessIds([busiestId], includeChildren: true)).Count
            .Should().BeGreaterThanOrEqualTo(parentOnly.Count);
    }

    [TestMethod]
    public void Read_ScopedToUnknownId_WarnsThatItWasNotFound()
    {
        LoadedTrace loaded = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.ForProcessIds([999_999]));

        // A manifest replayed against the wrong capture must not look like an ordinary
        // thin result: name the ids that contributed nothing.
        loaded.Info.Warnings.Should().Contain(w => w.Contains("999999", StringComparison.Ordinal)
            && w.Contains("not found in this trace", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_ScopedToExactIds_NamesTheIdsInTheScopeNotice()
    {
        int[] jobIds = [.. ProcessIdsOf(Load(ScopeRequest.ForProcess("HotLoopBench-Job", includeChildren: false)))];

        LoadedTrace loaded = new TraceLoader().Load(
            EtwFixture, scope: ScopeRequest.ForProcessIds(jobIds, includeChildren: false));

        loaded.Info.Warnings.Should().Contain(w =>
            w.StartsWith("Scoped to pid", StringComparison.Ordinal)
            && w.Contains("(no children)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_ScopedToExactIds_CarriesResolvedRootsInTraceInfo()
    {
        int[] jobIds = [.. ProcessIdsOf(Load(ScopeRequest.ForProcess("HotLoopBench-Job", includeChildren: false)))];

        LoadedTrace loaded = new TraceLoader().Load(
            EtwFixture,
            scope: ScopeRequest.ForProcessIds(jobIds, includeChildren: false));

        loaded.Info.AppliedProcessScope.Should().NotBeNull();
        AppliedProcessScope scope = loaded.Info.AppliedProcessScope!;
        scope.Mode.Should().Be("ids");
        scope.RequestedProcessIds.Should().Equal(jobIds.Order());
        scope.RootProcessIds.Should().Equal(jobIds.Order());
        scope.DescendantProcessIds.Should().BeEmpty();
        scope.IncludeChildren.Should().BeFalse();
    }

    [TestMethod]
    public void Read_AutomaticScope_CarriesTheResolvedProcessInTraceInfo()
    {
        LoadedTrace loaded = new TraceLoader().Load(EtwFixture);

        loaded.Info.AppliedProcessScope.Should().NotBeNull();
        AppliedProcessScope scope = loaded.Info.AppliedProcessScope!;
        scope.Mode.Should().Be("automatic");
        scope.Process.Should().NotBeNullOrEmpty();
        scope.RootProcessIds.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Read_AllProcesses_CarriesTheOptOutInTraceInfo()
    {
        LoadedTrace loaded = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.AllProcesses);

        loaded.Info.AppliedProcessScope.Should().BeSameAs(AppliedProcessScope.AllProcesses);
    }

    private static IEnumerable<int> ProcessIdsOf(IReadOnlyList<SampleStack> samples) =>
        samples.Select(static s => s.Process).Distinct(StringComparer.Ordinal).Select(ProcessIdOf);

    // Sample process labels are "name(pid)" on a multi-process capture.
    private static int ProcessIdOf(string label) => int.Parse(
        label[(label.IndexOf('(', StringComparison.Ordinal) + 1)..label.LastIndexOf(')')],
        CultureInfo.InvariantCulture);

    [TestMethod]
    public void Read_ScopeMatchingNoProcess_WarnsAboutTheScopeNotTheCapture()
    {
        LoadedTrace loaded = new TraceLoader().Load(
            EtwFixture, scope: ScopeRequest.ForProcess("no-such-process-name"));

        // When scoping drops every sample the warning must blame the scope, not imply
        // the capture lacks CPU events (the trace is fine - the scope matched nothing).
        loaded.Info.Warnings.Should().Contain(w => w.Contains("after scoping to", StringComparison.Ordinal));
        loaded.Info.Warnings.Should().NotContain(w =>
            w.Contains("Was the trace captured with a CPU sampler", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_ScopedTree_RanksThroughTheEngineUnchanged()
    {
        LoadedTrace loaded = new TraceLoader().Load(
            EtwFixture,
            scope: ScopeRequest.ForProcess("HotLoopBench-Job", includeChildren: false));

        RankingResult ranking = loaded.Aggregator.SelfTime("", FrameNames.DefaultFoldPatterns, 5);

        // A scoped source flows through the folding aggregator like any other.
        ranking.Rows.Should().NotBeEmpty();
        ranking.ScopeWeight.Should().BeGreaterThan(0.0);
    }

    [TestMethod]
    public void Read_AutoScope_KeepsNoMoreThanTheWholeCapture()
    {
        LoadedTrace all = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.AllProcesses);
        LoadedTrace auto = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.Auto);

        // The automatic default scopes to the busiest process tree, which can never
        // keep more than the whole capture. (On this committed fixture - a tight BDN
        // process tree already trimmed to the workload - the busiest tree happens to be
        // the whole capture, so it does not narrow further; on a real machine-wide
        // capture it would.)
        auto.Source.Samples.Count.Should().BeLessThanOrEqualTo(all.Source.Samples.Count);
        auto.Source.Samples.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Read_AutoScope_SelectsTheProcessWithTheMostCpuSamples()
    {
        IReadOnlyList<SampleStack> all = Load(ScopeRequest.AllProcesses);
        IReadOnlyList<SampleStack> auto = Load(ScopeRequest.Auto);

        // The automatic scope ranks by CPU sample count (not CPUMSec), so it must keep
        // every sample of the most-sampled process - that process is the workload the
        // ranking is about. Its tree may add child-process samples on top, so auto is
        // never smaller than the busiest process's own sample set.
        IGrouping<string, SampleStack> busiest = all
            .GroupBy(static s => s.Process, StringComparer.Ordinal)
            .OrderByDescending(static g => g.Count())
            .First();

        int autoFromBusiest = auto.Count(s => string.Equals(s.Process, busiest.Key, StringComparison.Ordinal));
        autoFromBusiest.Should().Be(busiest.Count());
    }

    [TestMethod]
    public void Read_AutoScope_OnAnAlreadyScopedCapture_DoesNotWarn()
    {
        // The applied-scope notice is suppressed when the automatic scope did not
        // actually drop any process - emitting "Scoped to X; pass --all-processes" for
        // a no-op would be misleading. This fixture's busiest tree is the whole capture.
        LoadedTrace auto = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.Auto);
        LoadedTrace all = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.AllProcesses);

        if (auto.Source.Samples.Count == all.Source.Samples.Count)
        {
            auto.Info.Warnings.Should().NotContain(w => w.StartsWith("Scoped to the ", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Read_ExplicitNarrowingScope_Warns()
    {
        // An explicit --process that drops part of the capture surfaces the scope
        // notice so the agent knows the ranking covers one tree and how to widen.
        LoadedTrace scoped = new TraceLoader().Load(
            EtwFixture,
            scope: ScopeRequest.ForProcess("HotLoopBench-Job", includeChildren: false));

        scoped.Info.Warnings.Should().Contain(w =>
            w.StartsWith("Scoped to the ", StringComparison.Ordinal)
            && w.Contains("--all-processes", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_NullScope_MatchesAuto()
    {
        // A null request is unspecified, which the load path treats as the automatic
        // busiest-process default - so it resolves to the same trace as an explicit
        // ScopeRequest.Auto. This is what makes their shared cache key correct.
        LoadedTrace nullScope = new TraceLoader().Load(EtwFixture, scope: null);
        LoadedTrace auto = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.Auto);

        nullScope.Source.Samples.Count.Should().Be(auto.Source.Samples.Count);
        nullScope.Info.Warnings.Should().BeEquivalentTo(auto.Info.Warnings);
    }

    [TestMethod]
    public void Read_AllProcesses_ReadsEveryProcessAndDoesNotWarn()
    {
        LoadedTrace all = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.AllProcesses);

        // The opt-out reads the whole capture (more than one process) and emits no
        // scope notice.
        DistinctProcesses(all.Source.Samples).Should().BeGreaterThan(1);
        all.Info.Warnings.Should().NotContain(w => w.StartsWith("Scoped to the ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_AutoScope_IsDeterministicAcrossReads()
    {
        // The busiest-process choice is a pure function of the trace, so two automatic
        // reads of the same capture resolve to the same process and keep the same
        // samples. (The exact process is the heaviest CPU consumer, which need not be a
        // touki-named process - the BDN host can dominate - so this asserts stability
        // rather than a specific name.)
        LoadedTrace first = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.Auto);
        LoadedTrace second = new TraceLoader().Load(EtwFixture, scope: ScopeRequest.Auto);

        first.Source.Samples.Count.Should().Be(second.Source.Samples.Count);
        first.Info.Warnings.Should().BeEquivalentTo(second.Info.Warnings);
    }
}
