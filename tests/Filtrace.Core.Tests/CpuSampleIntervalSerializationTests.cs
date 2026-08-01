// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using Filtrace.Output;

namespace Filtrace.Tracing;

[TestClass]
public sealed class CpuSampleIntervalSerializationTests
{
    private static JsonElement Serialize(CpuSampleInterval interval)
    {
        EtwCollectResult result = new()
        {
            OutputPath = "out.etl",
            ProcessId = 1,
            ProcessName = "app",
            ProcessExitCode = 0,
            Invocations = [new EtwInvocation(1, 1, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            FileSizeBytes = 1,
            Profile = CollectProfile.Cpu,
            KernelKeywords = "Process",
            ClrKeywords = "none",
            CpuSample = interval,
        };

        string json = OutputJson.Serialize(new AnalysisResult<EtwCollectResult>(result, [], []));
        return JsonDocument.Parse(json).RootElement.GetProperty("result").GetProperty("cpuSample");
    }

    [TestMethod]
    public void Serialize_KeepsTheSubMillisecondFloor()
    {
        // The wire format rounds doubles to two decimals, which turns the measured
        // 0.1221 ms floor into 0.12 - an 18% error on the value every weight in the trace
        // is scaled by, and one that no longer tells the floor from a 0.125 ms request.
        JsonElement cpuSample = Serialize(new CpuSampleInterval(0.0625, 0.1221, 0.1221, 100.0));

        cpuSample.GetProperty("minimumMSec").GetDouble().Should().Be(0.1221);
        cpuSample.GetProperty("effectiveMSec").GetDouble().Should().Be(0.1221);
        cpuSample.GetProperty("requestedMSec").GetDouble().Should().Be(0.0625);
    }

    [TestMethod]
    public void Serialize_DistinguishesTheFloorFromANearbyRequest()
    {
        Serialize(new CpuSampleInterval(0.125, 0.125, 0.1221, 100.0))
            .GetProperty("requestedMSec").GetDouble().Should().NotBe(
                Serialize(new CpuSampleInterval(0.1221, 0.1221, 0.1221, 100.0))
                    .GetProperty("requestedMSec").GetDouble());
    }

    [TestMethod]
    public void Serialize_CarriesTheClampFlag()
    {
        Serialize(new CpuSampleInterval(0.0625, 0.1221, 0.1221, 100.0))
            .GetProperty("clamped").GetBoolean().Should().BeTrue();

        Serialize(new CpuSampleInterval(1.0, 1.0, 0.1221, 100.0))
            .GetProperty("clamped").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public void Serialize_LeavesOtherDoublesAtTheWireFormatPrecision()
    {
        // The converter is property-scoped on purpose: two decimals stays right for
        // sampled weights, where more digits imply precision the sampling does not have.
        OutputJson.DoublePrecision.Should().Be(2);
    }
}
