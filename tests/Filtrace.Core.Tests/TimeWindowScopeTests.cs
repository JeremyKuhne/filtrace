// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class TimeWindowScopeTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string ActivityTrace => FixturePath("activity.nettrace");

    [TestMethod]
    public void WithTimeWindow_SetsAndClearsTheWindow()
    {
        ScopeRequest.Auto.Window.Should().BeNull();

        ScopeRequest windowed = ScopeRequest.Auto.WithTimeWindow(1000.0, 5000.0);
        windowed.Window.Should().NotBeNull();
        windowed.Window!.Value.StartMSec.Should().Be(1000.0);
        windowed.Window!.Value.EndMSec.Should().Be(5000.0);

        // Both bounds null clears the window rather than filtering to an empty span.
        windowed.WithTimeWindow(null, null).Window.Should().BeNull();
    }

    [TestMethod]
    public void WithTimeWindow_PreservesTheProcessAndActivityScopes()
    {
        ScopeRequest scope = ScopeRequest.ForProcess("MyApp").WithActivity("Order").WithTimeWindow(1000.0, null);

        scope.ProcessName.Should().Be("MyApp");
        scope.ActivityName.Should().Be("Order");
        scope.Window!.Value.StartMSec.Should().Be(1000.0);
        scope.Window!.Value.EndMSec.Should().BeNull();
    }

    [TestMethod]
    public void Load_CpuScopedToAWindow_KeepsOnlySamplesInsideItAndNotesTheScope()
    {
        TraceLoader loader = new();

        int wholeCount = loader.Load(ActivityTrace, TraceMetric.Cpu).Info.SampleCount;
        wholeCount.Should().BeGreaterThan(0);

        LoadedTrace early = loader.Load(
            ActivityTrace, TraceMetric.Cpu, scope: ScopeRequest.Auto.WithTimeWindow(null, 150.0));

        early.Info.SampleCount.Should().BeInRange(1, wholeCount - 1,
            "an early window keeps some but not all of the samples");
        early.Info.Warnings.Should().Contain(w =>
            w.Contains("Scoped to the [start, 150] ms window", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Load_CpuTwoComplementaryWindows_PartitionTheSamples()
    {
        TraceLoader loader = new();

        int wholeCount = loader.Load(ActivityTrace, TraceMetric.Cpu).Info.SampleCount;

        int earlyCount = loader.Load(
            ActivityTrace, TraceMetric.Cpu, scope: ScopeRequest.Auto.WithTimeWindow(null, 150.0)).Info.SampleCount;
        int lateCount = loader.Load(
            ActivityTrace, TraceMetric.Cpu, scope: ScopeRequest.Auto.WithTimeWindow(150.0, null)).Info.SampleCount;

        earlyCount.Should().BeGreaterThan(0);
        lateCount.Should().BeGreaterThan(0);

        // Every sample is either before or after the split, so the two windows cover the
        // whole trace between them (no sample lands exactly on the split in this fixture).
        (earlyCount + lateCount).Should().Be(wholeCount);
    }

    [TestMethod]
    public void Load_CpuScopedToAFutureWindow_DropsEverySampleAndBlamesTheWindow()
    {
        TraceLoader loader = new();

        LoadedTrace future = loader.Load(
            ActivityTrace, TraceMetric.Cpu, scope: ScopeRequest.Auto.WithTimeWindow(1e9, 1e9 + 1.0));

        future.Info.SampleCount.Should().Be(0);
        future.Info.Warnings.Should().Contain(w =>
            w.Contains("No samples remained inside the [1000000000, 1000000001] ms window", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Load_UnboundedWindow_ReadsTheWholeTrace()
    {
        TraceLoader loader = new();

        int wholeCount = loader.Load(ActivityTrace, TraceMetric.Cpu).Info.SampleCount;

        // WithTimeWindow(null, null) clears the window, so the read matches the unscoped one.
        LoadedTrace unbounded = loader.Load(
            ActivityTrace, TraceMetric.Cpu, scope: ScopeRequest.Auto.WithTimeWindow(null, null));

        unbounded.Info.SampleCount.Should().Be(wholeCount);
        unbounded.Info.Warnings.Should().NotContain(w => w.Contains("window", StringComparison.OrdinalIgnoreCase));
    }

    // The time window is the one scope axis that applies to every metric, since every
    // sampled event carries a timestamp. These confirm each single-process EventPipe
    // provider honors it: a window over the whole capture keeps every sample, and a
    // window past its end keeps none and blames the window.
    [TestMethod]
    public void Load_AllocationScopedToAWindow_IsHonored() =>
        AssertWindowIsUniversal(TraceMetric.Allocations, "alloc.nettrace");

    [TestMethod]
    public void Load_ExceptionsScopedToAWindow_IsHonored() =>
        AssertWindowIsUniversal(TraceMetric.Exceptions, "exceptions.nettrace");

    [TestMethod]
    public void Load_ContentionScopedToAWindow_IsHonored() =>
        AssertWindowIsUniversal(TraceMetric.Contention, "contention.nettrace");

    [TestMethod]
    public void Load_WaitScopedToAWindow_IsHonored() =>
        AssertWindowIsUniversal(TraceMetric.Wait, "wait.nettrace");

    [TestMethod]
    public void Load_ActivityScopedToAWindow_IsHonored() =>
        AssertWindowIsUniversal(TraceMetric.Activity, "activity.nettrace");

    private static void AssertWindowIsUniversal(TraceMetric metric, string fixtureName)
    {
        TraceLoader loader = new();
        string path = FixturePath(fixtureName);

        int wholeCount = loader.Load(path, metric).Info.SampleCount;
        wholeCount.Should().BeGreaterThan(0);

        // A window spanning the whole capture (0 to well past its end) keeps every sample.
        LoadedTrace wide = loader.Load(path, metric, scope: ScopeRequest.Auto.WithTimeWindow(0.0, 1e9));
        wide.Info.SampleCount.Should().Be(wholeCount, "a window over the whole capture drops nothing");

        // A window past the end of the capture keeps none and names the window as the cause.
        LoadedTrace future = loader.Load(path, metric, scope: ScopeRequest.Auto.WithTimeWindow(1e9, 1e9 + 1.0));
        future.Info.SampleCount.Should().Be(0);
        future.Info.Warnings.Should().Contain(w =>
            w.Contains("window", StringComparison.OrdinalIgnoreCase),
            "an empty windowed result blames the window, not the capture");
    }
}
