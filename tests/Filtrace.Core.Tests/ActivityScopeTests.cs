// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class ActivityScopeTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string ActivityTrace => FixturePath("activity.nettrace");

    [TestMethod]
    public void WithActivity_SetsAndClearsTheActivityName()
    {
        ScopeRequest.Auto.ActivityName.Should().BeNull();
        ScopeRequest.Auto.WithActivity("Order").ActivityName.Should().Be("Order");

        // An empty or null name clears the scope rather than filtering to "".
        ScopeRequest.Auto.WithActivity("Order").WithActivity("").ActivityName.Should().BeNull();
        ScopeRequest.Auto.WithActivity(null).ActivityName.Should().BeNull();
    }

    [TestMethod]
    public void WithActivity_PreservesTheProcessScope()
    {
        ScopeRequest scope = ScopeRequest.ForProcess("MyApp").WithActivity("Order");

        scope.ProcessName.Should().Be("MyApp");
        scope.ActivityName.Should().Be("Order");
    }

    [TestMethod]
    public void Load_CpuScopedToActivity_KeepsOnlyThatActivitysSamples()
    {
        TraceLoader loader = new();

        LoadedTrace whole = loader.Load(ActivityTrace, TraceMetric.Cpu);
        LoadedTrace scoped = loader.Load(
            ActivityTrace, TraceMetric.Cpu, scope: ScopeRequest.Auto.WithActivity("Order"));

        // Scoping to the Order activity drops the CPU samples taken outside any Order
        // request, but keeps those taken inside it (including its nested Query / Render).
        scoped.Info.SampleCount.Should().BeGreaterThan(0);
        scoped.Info.SampleCount.Should().BeLessThan(
            whole.Info.SampleCount, "the activity scope drops samples taken outside Order activities");
        scoped.Info.Warnings.Should().Contain(w =>
            w.Contains("Scoped to the 'Order' activity", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Load_CpuScopedToUnknownActivity_DropsEverySampleAndBlamesTheScope()
    {
        TraceLoader loader = new();

        LoadedTrace scoped = loader.Load(
            ActivityTrace, TraceMetric.Cpu, scope: ScopeRequest.Auto.WithActivity("NoSuchActivity"));

        // A scope that matches no activity drops every sample; the warning must blame the
        // scope, not imply the capture carried no CPU samples.
        scoped.Info.SampleCount.Should().Be(0);
        scoped.Info.Warnings.Should().Contain(w =>
            w.Contains("No samples remained inside the 'NoSuchActivity' activity", StringComparison.Ordinal));
    }
}
