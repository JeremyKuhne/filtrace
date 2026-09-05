// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;
using Filtrace.Tracing.Readers;

namespace Filtrace.Server;

[TestClass]
public sealed class TraceStoreTests
{
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromSeconds(10);

    private static string FixturePath(string name) =>
        Path.Join(AppContext.BaseDirectory, "Fixtures", name);

    private static string CopyToTemp(string fixture, out string tempDirectory)
    {
        tempDirectory = Path.Join(Path.GetTempPath(), $"filtrace-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string destination = Path.Join(tempDirectory, fixture);
        File.Copy(FixturePath(fixture), destination);
        return destination;
    }

    [TestMethod]
    public void Ctor_NullLoader_ThrowsArgumentNull()
    {
        Action create = () => new TraceStore(TraceStore.DefaultCapacity, loader: null!);

        create.Should().Throw<ArgumentNullException>().WithParameterName("loader");
    }

    [TestMethod]
    public void Get_SamePath_ReturnsCachedInstance()
    {
        TraceStore store = new();
        string path = FixturePath("folding.speedscope.json");

        LoadedTrace first = store.Get(path);
        LoadedTrace second = store.Get(path);

        second.Should().BeSameAs(first);
    }

    [TestMethod]
    public async Task GetAsync_ConcurrentSameTrace_ConvertsOnceAndWaitsAsynchronously()
    {
        TraceStore store = new();
        string path = CopyToTemp("activity.nettrace", out string tempDirectory);
        using Barrier startBarrier = new(participantCount: 5);
        try
        {
            Task<TraceStoreLoadResult>[] loads = Enumerable.Range(0, 4)
                .Select(_ => Task.Run(async () =>
                {
                    startBarrier.SignalAndWait(SynchronizationTimeout).Should().BeTrue();
                    return await store.GetAsync(path);
                }))
                .ToArray();

            startBarrier.SignalAndWait(SynchronizationTimeout).Should().BeTrue();

            TraceStoreLoadResult[] results = await Task.WhenAll(loads);

            results.Should().ContainSingle(result => result.EtlxCacheState == EtlxCacheState.Converted);
            results.Count(result => result.EtlxCacheState == EtlxCacheState.Waited).Should().Be(3);
            results.Select(result => result.Trace).Should().OnlyContain(trace => trace.Info.SampleCount > 0);
            Directory.EnumerateFiles(tempDirectory, "*.new").Should().BeEmpty();
            Directory.EnumerateFiles(tempDirectory, ".filtrace-etlx-*").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(TraceMetric.Cpu, TraceMetric.Allocations)]
    [DataRow(TraceMetric.Allocations, TraceMetric.Cpu)]
    [DataRow(TraceMetric.Cpu, TraceMetric.Cpu)]
    [DataRow(TraceMetric.Allocations, TraceMetric.Allocations)]
    public async Task GetAsync_ConcurrentDifferentViews_ReportsWaitedForTheSecondView(
        TraceMetric firstMetric,
        TraceMetric secondMetric)
    {
        TraceStore store = new();
        string path = CopyToTemp("alloc.nettrace", out string tempDirectory);
        using ManualResetEventSlim mutexHeld = new(initialState: false);
        using ManualResetEventSlim releaseMutex = new(initialState: false);
        Task mutexOwner = Task.Run(() =>
        {
            using Mutex conversionMutex = new(initiallyOwned: false, TraceConverter.LockNameFor(path));
            if (!conversionMutex.WaitOne(SynchronizationTimeout))
            {
                throw new TimeoutException("Timed out acquiring the ETLX conversion mutex.");
            }

            try
            {
                mutexHeld.Set();
                if (!releaseMutex.Wait(SynchronizationTimeout))
                {
                    throw new TimeoutException("Timed out waiting to release the ETLX conversion mutex.");
                }
            }
            finally
            {
                conversionMutex.ReleaseMutex();
            }
        });

        Task<TraceStoreLoadResult[]>? loads = null;
        try
        {
            mutexHeld.Wait(SynchronizationTimeout).Should().BeTrue();
            ScopeRequest? secondScope = firstMetric == secondMetric
                ? ScopeRequest.Auto.WithTimeWindow(0.0, 1e9)
                : null;

            Task<TraceStoreLoadResult> first = store.GetAsync(path, metric: firstMetric);
            Task<TraceStoreLoadResult> second = store.GetAsync(path, metric: secondMetric, scope: secondScope);
            loads = Task.WhenAll(first, second);
            loads.IsCompleted.Should().BeFalse();
            releaseMutex.Set();

            TraceStoreLoadResult[] results = await loads.WaitAsync(SynchronizationTimeout);

            results[0].EtlxCacheState.Should().Be(EtlxCacheState.Converted);
            results[1].EtlxCacheState.Should().Be(EtlxCacheState.Waited);
            results[1].Trace.Should().NotBeSameAs(results[0].Trace);
            results[1].Trace.Info.EtlxCacheState.Should().Be(EtlxCacheState.Hit);
            Directory.EnumerateFiles(tempDirectory, ".filtrace-etlx-*").Should().BeEmpty();
        }
        finally
        {
            releaseMutex.Set();
            await mutexOwner;
            if (loads is not null)
            {
                await loads;
            }

            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(EtlxCacheState.Converted, true)]
    [DataRow(EtlxCacheState.Converted, false)]
    [DataRow(EtlxCacheState.Recovered, true)]
    [DataRow(EtlxCacheState.Recovered, false)]
    [DataRow(EtlxCacheState.Hit, true)]
    [DataRow(EtlxCacheState.Hit, false)]
    public async Task GetAsync_CompetingSynchronousLoad_PreservesOwnStateAndSharesWinningModel(
        EtlxCacheState firstLoadState,
        bool asyncFirst)
    {
        string path = CopyToTemp("activity.nettrace", out string tempDirectory);
        using ManualResetEventSlim firstLoaded = new(initialState: false);
        using ManualResetEventSlim releaseFirst = new(initialState: false);
        GatedTraceReader reader = new(firstLoaded, releaseFirst);
        TraceStore store = new(TraceStore.DefaultCapacity, new TraceLoader([reader]));
        Task<LoadedTrace>? synchronousLoad = null;
        Task<TraceStoreLoadResult>? asynchronousLoad = null;
        try
        {
            if (firstLoadState == EtlxCacheState.Hit)
            {
                TraceConverter.Convert(path);
            }
            else if (firstLoadState == EtlxCacheState.Recovered)
            {
                string cachePath = TraceConverter.EtlxPathFor(path);
                File.WriteAllText(cachePath, "incompatible cache");
                File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow.AddMinutes(1));
            }

            if (asyncFirst)
            {
                asynchronousLoad = store.GetAsync(path);
            }
            else
            {
                synchronousLoad = Task.Run(() => store.Get(path));
            }

            firstLoaded.Wait(SynchronizationTimeout).Should().BeTrue();
            if (asyncFirst)
            {
                synchronousLoad = Task.Run(() => store.Get(path));
                await synchronousLoad.WaitAsync(SynchronizationTimeout);
            }
            else
            {
                asynchronousLoad = store.GetAsync(path);
                await asynchronousLoad.WaitAsync(SynchronizationTimeout);
            }

            releaseFirst.Set();
            LoadedTrace synchronous = await synchronousLoad!.WaitAsync(SynchronizationTimeout);
            TraceStoreLoadResult asynchronous = await asynchronousLoad!.WaitAsync(SynchronizationTimeout);

            reader.ReadCount.Should().Be(2);
            asynchronous.Trace.Should().BeSameAs(synchronous);
            asynchronous.Trace.Info.EtlxCacheState.Should().Be(EtlxCacheState.Hit);
            asynchronous.EtlxCacheState.Should().Be(asyncFirst ? firstLoadState : EtlxCacheState.Hit);

            TraceStoreLoadResult parsedHit = await store.GetAsync(path);

            parsedHit.Trace.Should().BeSameAs(synchronous);
            parsedHit.EtlxCacheState.Should().BeNull();
            reader.ReadCount.Should().Be(2);
        }
        finally
        {
            releaseFirst.Set();
            try
            {
                if (synchronousLoad is not null)
                {
                    await synchronousLoad;
                }

                if (asynchronousLoad is not null)
                {
                    await asynchronousLoad;
                }
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(TraceMetric.Cpu, "activity.nettrace")]
    [DataRow(TraceMetric.Allocations, "alloc.nettrace")]
    [DataRow(TraceMetric.Exceptions, "exceptions.nettrace")]
    [DataRow(TraceMetric.Contention, "contention.nettrace")]
    [DataRow(TraceMetric.Wait, "wait.nettrace")]
    [DataRow(TraceMetric.Activity, "activity.nettrace")]
    [DataRow(TraceMetric.ThreadTime, "etw.etl")]
    public async Task GetAsync_CacheLifecycle_PreservesMetricStateAndSkipsEtlxOnParsedHits(
        TraceMetric metric,
        string fixture)
    {
        if (metric == TraceMetric.ThreadTime && !OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("ETW trace reading requires Windows.");
        }

        TraceStore store = new();
        string path = CopyToTemp(fixture, out string tempDirectory);
        try
        {
            TraceStoreLoadResult first = await store.GetAsync(path, metric: metric);
            TraceStoreLoadResult diskHit = await new TraceStore().GetAsync(path, metric: metric);

            first.EtlxCacheState.Should().Be(EtlxCacheState.Converted);
            first.Trace.Info.SampleCount.Should().BeGreaterThan(0);
            diskHit.EtlxCacheState.Should().Be(EtlxCacheState.Hit);
            diskHit.Trace.Should().NotBeSameAs(first.Trace);
            diskHit.Trace.Info.SampleCount.Should().Be(first.Trace.Info.SampleCount);

            string etlx = TraceConverter.EtlxPathFor(path);
            File.WriteAllText(etlx, "incompatible cache");
            File.SetLastWriteTimeUtc(etlx, DateTime.UtcNow.AddMinutes(1));
            byte[] incompatibleCache = File.ReadAllBytes(etlx);

            TraceStoreLoadResult parsedHit = await store.GetAsync(path, metric: metric);

            parsedHit.Trace.Should().BeSameAs(first.Trace);
            parsedHit.EtlxCacheState.Should().BeNull();
            File.ReadAllBytes(etlx).Should().Equal(incompatibleCache);

            TraceStoreLoadResult recovered = await new TraceStore().GetAsync(path, metric: metric);

            recovered.EtlxCacheState.Should().Be(EtlxCacheState.Recovered);
            recovered.Trace.Info.SampleCount.Should().Be(first.Trace.Info.SampleCount);
            recovered.Trace.Info.TotalWeight.Should().Be(first.Trace.Info.TotalWeight);
            recovered.Trace.Info.Analyses.Should().BeEquivalentTo(first.Trace.Info.Analyses);
            Directory.EnumerateFiles(tempDirectory, ".filtrace-etlx-*").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(TraceMetric.Cpu, "activity.nettrace")]
    [DataRow(TraceMetric.Allocations, "alloc.nettrace")]
    [DataRow(TraceMetric.Exceptions, "exceptions.nettrace")]
    [DataRow(TraceMetric.Contention, "contention.nettrace")]
    [DataRow(TraceMetric.Wait, "wait.nettrace")]
    [DataRow(TraceMetric.Activity, "activity.nettrace")]
    [DataRow(TraceMetric.ThreadTime, "etw.etl")]
    public async Task GetAsync_CanceledWhileWaitingForInterprocessConversion_ThrowsOperationCanceled(
        TraceMetric metric,
        string fixture)
    {
        if (metric == TraceMetric.ThreadTime && !OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("ETW trace reading requires Windows.");
        }

        TraceStore store = new();
        string path = CopyToTemp(fixture, out string tempDirectory);
        using ManualResetEventSlim mutexHeld = new(initialState: false);
        using ManualResetEventSlim releaseMutex = new(initialState: false);
        using CancellationTokenSource cancellation = new();
        Task mutexOwner = Task.Run(() =>
        {
            using Mutex conversionMutex = new(initiallyOwned: false, TraceConverter.LockNameFor(path));
            if (!conversionMutex.WaitOne(SynchronizationTimeout))
            {
                throw new TimeoutException("Timed out acquiring the ETLX conversion mutex.");
            }

            try
            {
                mutexHeld.Set();
                if (!releaseMutex.Wait(SynchronizationTimeout))
                {
                    throw new TimeoutException("Timed out waiting to release the ETLX conversion mutex.");
                }
            }
            finally
            {
                conversionMutex.ReleaseMutex();
            }
        });

        try
        {
            mutexHeld.Wait(SynchronizationTimeout).Should().BeTrue();
            Task<TraceStoreLoadResult> load = store.GetAsync(
                path,
                metric: metric,
                cancellationToken: cancellation.Token);

            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

            Func<Task> wait = async () => await load;

            await wait.Should().ThrowAsync<OperationCanceledException>();
            File.Exists(TraceConverter.EtlxPathFor(path)).Should().BeFalse();

            releaseMutex.Set();
            await mutexOwner;
            TraceStoreLoadResult retry = await store.GetAsync(path, metric: metric).WaitAsync(SynchronizationTimeout);

            retry.EtlxCacheState.Should().Be(EtlxCacheState.Converted);
        }
        finally
        {
            releaseMutex.Set();
            await mutexOwner;
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Get_RelativeAndAbsolutePath_ShareCacheEntry()
    {
        TraceStore store = new();
        string absolute = FixturePath("folding.speedscope.json");
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), absolute);

        // Guard against a degenerate run where the two spellings come out identical:
        // the point is that a genuinely relative path and its absolute form collapse
        // onto a single cache entry.
        relative.Should().NotBe(absolute);

        LoadedTrace viaAbsolute = store.Get(absolute);
        LoadedTrace viaRelative = store.Get(relative);

        viaRelative.Should().BeSameAs(viaAbsolute);
    }

    [TestMethod]
    public void Get_DifferentSymbolsKey_CachesSeparately()
    {
        TraceStore store = new();
        string path = FixturePath("folding.speedscope.json");

        LoadedTrace withoutSymbols = store.Get(path);
        LoadedTrace withSymbols = store.Get(path, AppContext.BaseDirectory);

        withSymbols.Should().NotBeSameAs(withoutSymbols);
    }

    [TestMethod]
    public void Get_DifferentMetricKey_CachesSeparately()
    {
        TraceStore store = new();
        // A .nettrace can be read as either the CPU view or the allocation view, so the
        // same path under two metrics must key to two distinct cache entries - each
        // carrying its own provider source - rather than collapsing onto one.
        string path = FixturePath("alloc.nettrace");

        LoadedTrace cpu = store.Get(path, metric: TraceMetric.Cpu);
        LoadedTrace allocations = store.Get(path, metric: TraceMetric.Allocations);

        allocations.Should().NotBeSameAs(cpu);
        cpu.Source.Metric.Should().Be(MetricInfo.Cpu);
        allocations.Source.Metric.Should().Be(MetricInfo.Allocations);
    }

    [TestMethod]
    public void Get_SameMetric_ReturnsCachedInstance()
    {
        TraceStore store = new();
        string path = FixturePath("alloc.nettrace");

        LoadedTrace first = store.Get(path, metric: TraceMetric.Allocations);
        LoadedTrace second = store.Get(path, metric: TraceMetric.Allocations);

        second.Should().BeSameAs(first);
    }

    [TestMethod]
    public void Get_CpuScopedToActivity_DoesNotCollideWithTheUnscopedRead()
    {
        TraceStore store = new();
        string path = FixturePath("activity.nettrace");

        // Load the unscoped CPU view first so it populates the cache, then the activity-
        // scoped view: the scope must produce a distinct, narrower entry rather than serve
        // the cached unscoped result. Guards the activity axis of the cache key - without
        // it the second read would return the first's unscoped samples.
        LoadedTrace whole = store.Get(path, metric: TraceMetric.Cpu);
        LoadedTrace scoped = store.Get(
            path, metric: TraceMetric.Cpu, scope: ScopeRequest.Auto.WithActivity("Order"));

        scoped.Should().NotBeSameAs(whole);
        scoped.Info.SampleCount.Should().BeLessThan(whole.Info.SampleCount);
    }

    [TestMethod]
    public void Get_CpuScopedToTimeWindow_CachesSeparatelyFromTheUnscopedRead()
    {
        TraceStore store = new();
        string path = FixturePath("activity.nettrace");

        LoadedTrace whole = store.Get(path, metric: TraceMetric.Cpu);
        LoadedTrace windowed = store.Get(
            path, metric: TraceMetric.Cpu, scope: ScopeRequest.Auto.WithTimeWindow(startMSec: null, 150.0));

        windowed.Should().NotBeSameAs(whole);
        windowed.Info.SampleCount.Should().BeLessThan(whole.Info.SampleCount);
    }

    [TestMethod]
    public void Get_ParentOnlyAutomaticScope_CachesSeparatelyFromTheTreeScope()
    {
        TraceStore store = new();
        string path = FixturePath("activity.nettrace");

        // Both requests carry no selector, but they resolve to different process sets -
        // the busiest process's whole tree versus that process alone. Keying only on the
        // selector would let the first read's entry serve the second.
        LoadedTrace tree = store.Get(path, metric: TraceMetric.Cpu, scope: ScopeRequest.Auto);
        LoadedTrace parentOnly = store.Get(
            path, metric: TraceMetric.Cpu, scope: ScopeRequest.AutoScope(includeChildren: false));

        parentOnly.Should().NotBeSameAs(tree);
    }

    [TestMethod]
    public void Get_TimeWindowOnNonCpuMetric_CachesSeparately()
    {
        TraceStore store = new();
        string path = FixturePath("alloc.nettrace");

        // The time window scopes every metric, so an allocation read scoped to a window
        // must key separately from the unscoped one - unlike the process scope, which the
        // single-process EventPipe providers ignore and so do not key on.
        LoadedTrace whole = store.Get(path, metric: TraceMetric.Allocations);
        LoadedTrace windowed = store.Get(
            path, metric: TraceMetric.Allocations, scope: ScopeRequest.Auto.WithTimeWindow(0.0, 1e9));

        windowed.Should().NotBeSameAs(whole);
    }

    [TestMethod]
    public void Get_NonCpuMetric_IgnoresSymbolsDirectoryInCacheKey()
    {
        TraceStore store = new();
        string path = FixturePath("alloc.nettrace");

        // The allocation loader ignores symbolsDirectory (it resolves frames from the
        // trace's own rundown), so two calls that differ only in an ignored symbols
        // directory must dedupe to one cache entry rather than forcing a redundant read.
        LoadedTrace withoutSymbols = store.Get(path, symbolsDirectory: null, metric: TraceMetric.Allocations);
        LoadedTrace withSymbols = store.Get(path, AppContext.BaseDirectory, metric: TraceMetric.Allocations);

        withSymbols.Should().BeSameAs(withoutSymbols);
    }

    [TestMethod]
    public void Get_LoadsTraceWithExpectedInfo()
    {
        TraceStore store = new();

        LoadedTrace trace = store.Get(FixturePath("folding.speedscope.json"));

        trace.Info.Format.Should().Be(TraceFormat.Speedscope);
        trace.Info.SampleCount.Should().Be(4);
    }

    [TestMethod]
    public void Get_BeyondCapacity_EvictsLeastRecentlyUsedTrace()
    {
        // A capacity-1 store can hold one trace; loading a second distinct cache
        // entry evicts the first, so re-loading it produces a fresh instance.
        TraceStore store = new(capacity: 1);
        string path = FixturePath("folding.speedscope.json");

        LoadedTrace first = store.Get(path, AppContext.BaseDirectory);
        // A different symbols key is a separate cache entry; loading it evicts the first.
        store.Get(path, Path.GetTempPath());

        LoadedTrace reloaded = store.Get(path, AppContext.BaseDirectory);

        reloaded.Should().NotBeSameAs(first);
    }

    private sealed class GatedTraceReader(
        ManualResetEventSlim firstLoaded,
        ManualResetEventSlim releaseFirst) : ITraceReader
    {
        private readonly NetTraceReader _reader = new();
        private int _readCount;

        public int ReadCount => _readCount;

        public TraceFormat Format => _reader.Format;

        public bool CanRead(string path) => _reader.CanRead(path);

        public TraceReadResult Read(
            string path,
            string? symbolsDirectory = null,
            ScopeRequest? scope = null,
            SymbolOptions? symbolOptions = null,
            CancellationToken cancellationToken = default)
        {
            TraceReadResult result = _reader.Read(path, symbolsDirectory, scope, symbolOptions, cancellationToken);
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                firstLoaded.Set();
                if (!releaseFirst.Wait(SynchronizationTimeout))
                {
                    throw new TimeoutException("Timed out waiting to release the first trace load.");
                }
            }

            return result;
        }
    }
}
