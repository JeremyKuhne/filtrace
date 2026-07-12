// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class TraceLoaderAvailabilityTests
{
    [TestMethod]
    [DataRow("alloc.nettrace", "alloc")]
    [DataRow("exceptions.nettrace", "exceptions")]
    [DataRow("contention.nettrace", "contention")]
    [DataRow("wait.nettrace", "wait")]
    [DataRow("activity.nettrace", "activity")]
    [DataRow("alloc.nettrace", "gcstats")]
    [DataRow("jit.nettrace", "jitstats")]
    [DataRow("threadpool.nettrace", "threadpool")]
    public void Load_ObservedAnalysisEvents_ReportEnabledNonzero(string fixture, string analysis)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);

        TraceInfo info = new TraceLoader().Load(path).Info;

        info.Analyses[analysis].FormatSupported.Should().BeTrue();
        info.Analyses[analysis].CaptureStatus.Should().Be(CaptureStatus.Enabled);
        info.Analyses[analysis].EventCount.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Load_UnobservedAnalysisWithoutMetadata_ReportsUnknown()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "alloc.nettrace");

        TraceInfo info = new TraceLoader().Load(path).Info;

        info.Analyses["wait"].Should().Be(
            new AnalysisAvailability(true, CaptureStatus.Unknown, null));
    }
}