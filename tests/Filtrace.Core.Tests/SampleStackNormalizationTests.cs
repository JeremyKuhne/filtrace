// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class SampleStackNormalizationTests
{
    [TestMethod]
    public void NormalizeCpuWeightToSampleCount_PreservesPayloadWithoutAllocation()
    {
        IReadOnlyList<string> frames = ["Root", "Leaf"];
        IReadOnlyList<string> locations = ["", "source.cs:10"];
        SampleStack[] samples = new SampleStack[10_000];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = new SampleStack(frames, 0.125, "thread", locations, "process");
        }

        samples[0].NormalizeCpuWeightToSampleCount();
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (SampleStack sample in samples)
        {
            sample.NormalizeCpuWeightToSampleCount();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
        samples.Should().OnlyContain(static sample => sample.Weight == 1.0);
        samples[^1].Frames.Should().BeSameAs(frames);
        samples[^1].FrameLocations.Should().BeSameAs(locations);
        samples[^1].Thread.Should().Be("thread");
        samples[^1].Process.Should().Be("process");
    }
}