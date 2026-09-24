// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing.Providers;

namespace Filtrace.Tracing;

[TestClass]
public sealed class ProcessInventoryProviderTests
{
    private static string FixturePath(string name) =>
        Path.Join(AppContext.BaseDirectory, "Fixtures", name);

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Read_EtlFixture_MatchesFullStackInventory()
    {
        string path = FixturePath("etw.etl");
        LoadedTrace loaded = new TraceLoader().Load(
            path,
            scope: ScopeRequest.AllProcesses);

        ProcessInventorySnapshot inventory = new ProcessInventoryProvider().Read(path);

        inventory.Result.Should().BeEquivalentTo(
            loaded.Aggregator.Processes(),
            options => options.WithStrictOrdering());

        inventory.Metric.Should().Be(loaded.Aggregator.Metric);
        inventory.CpuSampling.Should().BeEquivalentTo(loaded.Info.CpuSampling);
        inventory.Warnings.Should().NotContain(
            warning => warning.Contains("frames resolved", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_NetTraceFixture_MatchesFullStackInventory()
    {
        string path = FixturePath("threadpool.nettrace");
        LoadedTrace loaded = new TraceLoader().Load(
            path,
            scope: ScopeRequest.AllProcesses);

        ProcessInventorySnapshot inventory = new ProcessInventoryProvider().Read(path);

        inventory.Result.Should().BeEquivalentTo(
            loaded.Aggregator.Processes(),
            options => options.WithStrictOrdering());

        inventory.Metric.Should().Be(loaded.Aggregator.Metric);
        inventory.CpuSampling.Should().BeEquivalentTo(loaded.Info.CpuSampling);
    }

    [TestMethod]
    public void Read_SpeedscopeFixture_MatchesFullStackInventory()
    {
        string path = FixturePath("folding.speedscope.json");
        LoadedTrace loaded = new TraceLoader().Load(
            path,
            scope: ScopeRequest.AllProcesses);

        ProcessInventorySnapshot inventory = new ProcessInventoryProvider().Read(path);

        inventory.Result.Should().BeEquivalentTo(
            loaded.Aggregator.Processes(),
            options => options.WithStrictOrdering());
    }

    [TestMethod]
    public void Read_MissingFile_Throws()
    {
        Action act = () => new ProcessInventoryProvider().Read(
            FixturePath("does-not-exist.etl"));

        act.Should().Throw<FileNotFoundException>();
    }
}
