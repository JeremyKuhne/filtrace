// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class ThreadPoolProviderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // The threadpool fixture is captured with the runtime pool forced to start at a
    // single worker thread, so a backlog of blocking work items starves it and the
    // runtime records the worker-thread adjustments this provider reads.
    private static ThreadPoolResult LoadThreadPool() =>
        new ThreadPoolProvider().Read(FixturePath("threadpool.nettrace"));

    [TestMethod]
    public void Read_ThreadPoolFixture_ReportsWorkerThreadAdjustments()
    {
        ThreadPoolResult result = LoadThreadPool();

        result.AdjustmentCount.Should().BeGreaterThan(0, "the starvation workload grows the pool");
        result.AdjustmentsByReason.Should().NotBeEmpty();
        // The per-reason breakdown accounts for every adjustment.
        result.AdjustmentsByReason.Sum(static a => a.Count).Should().Be(result.AdjustmentCount);
    }

    [TestMethod]
    public void Read_ThreadPoolFixture_DetectsStarvation()
    {
        ThreadPoolResult result = LoadThreadPool();

        result.StarvationCount.Should().BeGreaterThan(0, "the fixture is captured under thread-pool starvation");
        // The starvation tally matches the Starvation row in the breakdown.
        result.AdjustmentsByReason
            .Should().Contain(a => a.Reason == "Starvation" && a.Count == result.StarvationCount);
    }

    [TestMethod]
    public void Read_ThreadPoolFixture_ReportsWorkerThreadRange()
    {
        ThreadPoolResult result = LoadThreadPool();

        // The pool grows while starving, so the observed range spans at least one thread.
        result.MinWorkerThreadCount.Should().BeGreaterThan(0);
        result.MaxWorkerThreadCount.Should().BeGreaterThanOrEqualTo(result.MinWorkerThreadCount);
        // The configured floor never exceeds the configured ceiling.
        result.ConfiguredMinWorkerThreads.Should().BeGreaterThan(0);
        result.ConfiguredMaxWorkerThreads.Should().BeGreaterThanOrEqualTo(result.ConfiguredMinWorkerThreads);
    }

    [TestMethod]
    public void Read_ThreadPoolFixture_OrdersReasonsByCountDescending()
    {
        ThreadPoolResult result = LoadThreadPool();

        for (int i = 1; i < result.AdjustmentsByReason.Count; i++)
        {
            result.AdjustmentsByReason[i].Count
                .Should().BeLessThanOrEqualTo(result.AdjustmentsByReason[i - 1].Count);
        }
    }

    [TestMethod]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        ThreadPoolProvider provider = new();

        Action act = () => provider.Read(FixturePath("does-not-exist.nettrace"));

        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void Read_NullOrEmptyPath_ThrowsArgument(string? path)
    {
        ThreadPoolProvider provider = new();

        Action act = () => provider.Read(path!);

        act.Should().Throw<ArgumentException>();
    }
}
