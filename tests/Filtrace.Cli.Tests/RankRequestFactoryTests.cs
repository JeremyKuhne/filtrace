// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

[TestClass]
public sealed class RankRequestFactoryTests
{
    [TestMethod]
    [DataRow("cpu", TraceMetric.Cpu)]
    [DataRow("CPU", TraceMetric.Cpu)]
    [DataRow("Cpu", TraceMetric.Cpu)]
    [DataRow("alloc", TraceMetric.Allocations)]
    [DataRow("Alloc", TraceMetric.Allocations)]
    [DataRow("allocations", TraceMetric.Allocations)]
    [DataRow("exceptions", TraceMetric.Exceptions)]
    [DataRow("Exceptions", TraceMetric.Exceptions)]
    [DataRow("threadtime", TraceMetric.ThreadTime)]
    [DataRow("ThreadTime", TraceMetric.ThreadTime)]
    public void TryResolveMetric_KnownMetric_ResolvesProvider(string metric, TraceMetric expected)
    {
        RankRequestFactory.TryResolveMetric(metric, out TraceMetric resolved).Should().BeTrue();
        resolved.Should().Be(expected);
    }

    [TestMethod]
    [DataRow("gcstats")]
    [DataRow("bogus")]
    [DataRow("")]
    public void TryResolveMetric_UnknownMetric_IsFalse(string metric)
    {
        // gcstats is a planned report provider (not a stack metric), so it resolves as
        // unknown to the ranking verbs until its own verb lands.
        RankRequestFactory.TryResolveMetric(metric, out _).Should().BeFalse();
    }

    [TestMethod]
    public void Create_NullFold_UsesDefaultFoldPatterns()
    {
        RankRequest request = RankRequestFactory.Create(
            "t.nettrace", TraceMetric.Cpu, Measure.Self, root: "", top: 25, fold: null, symbols: null, OutputFormat.Text, strict: false);

        request.Fold.Should().BeSameAs(FrameNames.DefaultFoldPatterns);
    }

    [TestMethod]
    public void Create_EmptyFold_UsesDefaultFoldPatterns()
    {
        RankRequest request = RankRequestFactory.Create(
            "t.nettrace", TraceMetric.Cpu, Measure.Self, root: "", top: 25, fold: [], symbols: null, OutputFormat.Text, strict: false);

        request.Fold.Should().BeSameAs(FrameNames.DefaultFoldPatterns);
    }

    [TestMethod]
    public void Create_ExplicitFold_IsUsed()
    {
        RankRequest request = RankRequestFactory.Create(
            "t.nettrace", TraceMetric.Cpu, Measure.Self, root: "", top: 25, fold: ["^A", "^B"], symbols: null, OutputFormat.Text, strict: false);

        request.Fold.Should().Equal("^A", "^B");
    }

    [TestMethod]
    public void Create_MapsAllFields()
    {
        RankRequest request = RankRequestFactory.Create(
            "t.nettrace",
            TraceMetric.Allocations,
            Measure.Inclusive,
            root: "MoveNext",
            top: 10,
            fold: null,
            symbols: "bin/net10.0",
            OutputFormat.Json,
            strict: true);

        request.Path.Should().Be("t.nettrace");
        request.Metric.Should().Be(TraceMetric.Allocations);
        request.Measure.Should().Be(Measure.Inclusive);
        request.Root.Should().Be("MoveNext");
        request.Top.Should().Be(10);
        request.Symbols.Should().Be("bin/net10.0");
        request.Format.Should().Be(OutputFormat.Json);
        request.Strict.Should().BeTrue();
    }

    [TestMethod]
    public void TryResolveScope_NoOptions_IsAutomatic()
    {
        bool resolved = RankRequestFactory.TryResolveScope(
            "", processIds: null, Children.Include, allProcesses: false, out ScopeRequest scope, out string? error);

        resolved.Should().BeTrue();

        error.Should().BeNull();
        scope.Should().BeSameAs(ScopeRequest.Auto);
    }

    [TestMethod]
    public void TryResolveScope_AllProcesses_IsTheOptOut()
    {
        bool resolved = RankRequestFactory.TryResolveScope(
            "", processIds: null, Children.Include, allProcesses: true, out ScopeRequest scope, out _);

        resolved.Should().BeTrue();

        scope.Should().BeSameAs(ScopeRequest.AllProcesses);
    }

    [TestMethod]
    public void TryResolveScope_ProcessName_BuildsAnExplicitScope()
    {
        bool resolved = RankRequestFactory.TryResolveScope(
            "MyApp", processIds: null, Children.Include, allProcesses: false, out ScopeRequest scope, out _);

        resolved.Should().BeTrue();

        scope.Selector.Should().BeOfType<ProcessNameSelector>().Which.NameSubstring.Should().Be("MyApp");
        scope.IncludeAll.Should().BeFalse();
    }

    [TestMethod]
    public void TryResolveScope_Pids_BuildAnExactScope()
    {
        RankRequestFactory.TryResolveScope("", [42, 7], Children.Include, allProcesses: false, out ScopeRequest scope, out _)
            .Should().BeTrue();

        scope.Selector.Should().BeOfType<ProcessIdSelector>().Which.ProcessIds.Should().Equal(7, 42);
        scope.IncludeChildren.Should().BeTrue();
    }

    [TestMethod]
    public void TryResolveScope_ChildrenExclude_AppliesToEverySelector()
    {
        RankRequestFactory.TryResolveScope("", [42], Children.Exclude, allProcesses: false, out ScopeRequest byId, out _)
            .Should().BeTrue();

        bool byNameResolved = RankRequestFactory.TryResolveScope(
            "MyApp", processIds: null, Children.Exclude, allProcesses: false, out ScopeRequest byName, out _);

        bool automaticResolved = RankRequestFactory.TryResolveScope(
            "", processIds: null, Children.Exclude, allProcesses: false, out ScopeRequest automatic, out _);

        byNameResolved.Should().BeTrue();
        automaticResolved.Should().BeTrue();
        byId.IncludeChildren.Should().BeFalse();
        byName.IncludeChildren.Should().BeFalse();
        automatic.IncludeChildren.Should().BeFalse("the automatic scope picks a process, and that choice is still a tree");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-3)]
    public void TryResolveScope_NonPositivePid_IsAUsageError(int processId)
    {
        RankRequestFactory.TryResolveScope("", [processId], Children.Include, allProcesses: false, out _, out string? error)
            .Should().BeFalse();

        error.Should().Contain("not a valid process id");
    }

    [TestMethod]
    public void TryResolveScope_ChildrenWithAllProcesses_IsAUsageError()
    {
        bool resolved = RankRequestFactory.TryResolveScope(
            "", processIds: null, Children.Exclude, allProcesses: true, out _, out string? error);

        resolved.Should().BeFalse();

        error.Should().Contain("--all-processes already reads every process");
    }

    [TestMethod]
    [DataRow("MyApp", true, false)]
    [DataRow("MyApp", false, true)]
    [DataRow("", true, true)]
    public void TryResolveScope_ConflictingSelectors_IsAUsageError(string process, bool withPid, bool allProcesses)
    {
        RankRequestFactory.TryResolveScope(
            process, withPid ? [42] : null, Children.Include, allProcesses, out _, out string? error)
            .Should().BeFalse();

        error.Should().Contain("only one of --process, --pid, and --all-processes");
    }

    [TestMethod]
    public void TryResolveRoot_NoOptions_KeepsTheEmptyRoot()
    {
        RankRequestFactory.TryResolveRoot("", benchmark: false, out string root, out string? error)
            .Should().BeTrue();

        error.Should().BeNull();
        root.Should().BeEmpty();
    }

    [TestMethod]
    public void TryResolveRoot_ExplicitRoot_IsPassedThrough()
    {
        RankRequestFactory.TryResolveRoot("MyMethod", benchmark: false, out string root, out _)
            .Should().BeTrue();

        root.Should().Be("MyMethod");
    }

    [TestMethod]
    public void TryResolveRoot_Benchmark_PresetsTheWorkloadFrame()
    {
        RankRequestFactory.TryResolveRoot("", benchmark: true, out string root, out _)
            .Should().BeTrue();

        root.Should().Be(FrameNames.BenchmarkWorkloadFrame);
    }

    [TestMethod]
    public void TryResolveRoot_BothOptions_IsAUsageError()
    {
        RankRequestFactory.TryResolveRoot("MyMethod", benchmark: true, out _, out string? error)
            .Should().BeFalse();

        error.Should().Contain("only one of --root and --benchmark");
    }

    [TestMethod]
    public void TryResolveSymbolOptions_Default_IsManagedOnly()
    {
        // No --native-symbols: the offline managed-only default, so the CPU read never
        // reaches a symbol server.
        RankRequestFactory.TryResolveSymbolOptions(nativeSymbols: false, symbolCache: "", out SymbolOptions options, out _)
            .Should().BeTrue();

        options.Should().BeSameAs(SymbolOptions.None);
    }

    [TestMethod]
    public void TryResolveSymbolOptions_NativeSymbols_OptsInToNativeResolution()
    {
        RankRequestFactory.TryResolveSymbolOptions(nativeSymbols: true, symbolCache: "", out SymbolOptions options, out _)
            .Should().BeTrue();

        options.ResolveNativeRuntime.Should().BeTrue();
    }

    [TestMethod]
    public void TryResolveSymbolOptions_SymbolCache_IsCarriedThrough()
    {
        RankRequestFactory.TryResolveSymbolOptions(nativeSymbols: true, symbolCache: @"C:\sym", out SymbolOptions options, out _)
            .Should().BeTrue();

        options.ResolveNativeRuntime.Should().BeTrue();
        options.CacheDirectory.Should().Be(@"C:\sym");
    }

    [TestMethod]
    public void TryResolveSymbolOptions_InvalidSymbolCache_IsAUsageError()
    {
        // A '*' in --symbol-cache would corrupt the SymSrv path syntax; SymbolOptions.WithCache
        // rejects it, and this must surface as a clean usage error, not an unhandled exception.
        RankRequestFactory.TryResolveSymbolOptions(nativeSymbols: true, symbolCache: "bad*cache", out _, out string? error)
            .Should().BeFalse();

        error.Should().Contain("cannot contain '*'");
    }

    [TestMethod]
    public void TryResolveFold_NoOptions_LeavesNullForTheBuiltInDefault()
    {
        // Neither --fold nor --no-fold: null patterns signal Create to apply the
        // built-in default fold list.
        RankRequestFactory.TryResolveFold(fold: null, noFold: false, out string[]? patterns, out string? error)
            .Should().BeTrue();

        error.Should().BeNull();
        patterns.Should().BeNull();
    }

    [TestMethod]
    public void TryResolveFold_NoFold_FoldsOnlyTheSyntheticMarkers()
    {
        RankRequestFactory.TryResolveFold(fold: null, noFold: true, out string[]? patterns, out _)
            .Should().BeTrue();

        // Marker-only: the synthetic sample markers stay folded, but the JIT-helper
        // thunks (Memmove, WriteBarrier, JIT_) do not, so native leaves rank raw.
        patterns.Should().BeEquivalentTo(FrameNames.MarkerOnlyFoldPatterns);
        patterns.Should().NotContain("JIT_");
    }

    [TestMethod]
    public void TryResolveFold_ExplicitFold_IsPassedThrough()
    {
        RankRequestFactory.TryResolveFold(fold: ["MyHelper"], noFold: false, out string[]? patterns, out _)
            .Should().BeTrue();

        patterns.Should().BeEquivalentTo(["MyHelper"]);
    }

    [TestMethod]
    public void TryResolveFold_BothOptions_IsAUsageError()
    {
        RankRequestFactory.TryResolveFold(fold: ["MyHelper"], noFold: true, out _, out string? error)
            .Should().BeFalse();

        error.Should().Contain("only one of --fold and --no-fold");
    }
}
