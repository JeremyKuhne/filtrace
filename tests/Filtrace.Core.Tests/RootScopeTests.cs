// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class RootScopeTests
{
    [TestMethod]
    public void Apply_EmptyRootFrame_ReturnsSourceUnchanged()
    {
        StackSampleSource source = new(MetricInfo.Cpu, [new(["Program.Main", "MyApp.Work"], 5.0, "1")]);

        StackSampleSource scoped = RootScope.Apply(source, "");

        scoped.Should().BeSameAs(source);
    }

    [TestMethod]
    public void Apply_SampleWithoutRootFrame_IsDropped()
    {
        StackSampleSource source = new(
            MetricInfo.Cpu,
            [
                new(["Program.Main", "MyApp.Work"], 5.0, "1"),
                new(["Program.Main", "MyApp.Other"], 3.0, "1")
            ]);

        StackSampleSource scoped = RootScope.Apply(source, "MyApp.Work");

        scoped.Samples.Should().ContainSingle();
        scoped.Samples[0].Weight.Should().Be(5.0);
    }

    [TestMethod]
    public void Apply_SampleWithRootFrame_TrimsFramesToStartAtIt()
    {
        StackSampleSource source = new(
            MetricInfo.Cpu,
            [new(["Program.Main", "MyApp.Work", "MyApp.Inner"], 5.0, "1")]);

        StackSampleSource scoped = RootScope.Apply(source, "MyApp.Work");

        scoped.Samples.Should().ContainSingle();
        scoped.Samples[0].Frames.Should().Equal("MyApp.Work", "MyApp.Inner");
    }

    [TestMethod]
    public void Apply_RootFrameAtStackRoot_KeepsWholeStack()
    {
        StackSampleSource source = new(MetricInfo.Cpu, [new(["MyApp.Work", "MyApp.Inner"], 5.0, "1")]);

        StackSampleSource scoped = RootScope.Apply(source, "MyApp.Work");

        scoped.Samples[0].Frames.Should().Equal("MyApp.Work", "MyApp.Inner");
    }

    [TestMethod]
    public void Apply_RootFrameAtStackRoot_ReusesTheSampleInstanceWithoutCopying()
    {
        // No trimming is needed when the root frame is already the stack root (e.g. a
        // BenchmarkDotNet capture whose WorkloadAction wrapper is the outermost frame),
        // so Apply must reuse the original SampleStack rather than allocate a copy.
        SampleStack original = new(["MyApp.Work", "MyApp.Inner"], 5.0, "1");
        StackSampleSource source = new(MetricInfo.Cpu, [original]);

        StackSampleSource scoped = RootScope.Apply(source, "MyApp.Work");

        scoped.Samples[0].Should().BeSameAs(original);
    }

    [TestMethod]
    public void Apply_WithFrameLocations_TrimsLocationsInParallel()
    {
        StackSampleSource source = new(
            MetricInfo.Cpu,
            [
                new(
                    ["Program.Main", "MyApp.Work", "MyApp.Inner"],
                    5.0,
                    "1",
                    frameLocations: ["Program.cs:1", "MyApp.cs:10", "MyApp.cs:20"])
            ]);

        StackSampleSource scoped = RootScope.Apply(source, "MyApp.Work");

        scoped.Samples[0].FrameLocations.Should().Equal("MyApp.cs:10", "MyApp.cs:20");
    }

    [TestMethod]
    public void Apply_PreservesWeightThreadAndProcess()
    {
        StackSampleSource source = new(
            MetricInfo.Cpu,
            [new(["Program.Main", "MyApp.Work"], 7.5, "thread-1", process: "app.exe")]);

        StackSampleSource scoped = RootScope.Apply(source, "MyApp.Work");

        SampleStack sample = scoped.Samples[0];
        sample.Weight.Should().Be(7.5);
        sample.Thread.Should().Be("thread-1");
        sample.Process.Should().Be("app.exe");
    }

    [TestMethod]
    public void Apply_MatchesFoldingAggregatorScopeExactly()
    {
        // RootScope.Apply must select the same samples (by total weight) that the
        // ranking verbs' inline root scoping does, so `export --root` matches
        // `cpu --root` on the same trace.
        List<SampleStack> samples =
        [
            new(["Program.Main", "MyApp.Work", "MyApp.Inner"], 10.0, "1"),
            new(["Program.Main", "MyApp.Other"], 5.0, "1")
        ];
        StackSampleSource source = new(MetricInfo.Cpu, samples);

        StackSampleSource scoped = RootScope.Apply(source, "MyApp.Work");
        double scopedWeight = 0.0;
        foreach (SampleStack sample in scoped.Samples)
        {
            scopedWeight += sample.Weight;
        }

        RankingResult ranked = new FoldingAggregator(source).SelfTime("MyApp.Work", FrameNames.DefaultFoldPatterns, 25);

        scopedWeight.Should().Be(ranked.ScopeWeight);
    }
}
