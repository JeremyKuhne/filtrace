// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Server;

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures parsed reuse, ETLX replay, conversion, and recovery separately.
/// </summary>
[MemoryDiagnoser]
public class TraceCacheBenchmarks
{
    private string _directory = null!;
    private string _warmPath = null!;
    private string _coldPath = null!;
    private string _recoveryPath = null!;
    private TraceStore _store = null!;

    /// <summary>
    ///  The provider whose view is loaded.
    /// </summary>
    [Params(TraceMetric.Cpu, TraceMetric.Allocations)]
    public TraceMetric Metric { get; set; }

    /// <summary>
    ///  Creates isolated input copies and primes the parsed and disk caches.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        string input = Environment.GetEnvironmentVariable("FILTRACE_CACHE_BENCHMARK_TRACE_PATH")
            ?? Path.Join(AppContext.BaseDirectory, "Fixtures", Metric == TraceMetric.Cpu ? "activity.nettrace" : "alloc.nettrace");

        _directory = Path.Join(Path.GetTempPath(), $"filtrace-cache-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _warmPath = Path.Join(_directory, "warm.nettrace");
        _coldPath = Path.Join(_directory, "cold.nettrace");
        _recoveryPath = Path.Join(_directory, "recovery.nettrace");
        File.Copy(input, _warmPath);
        File.Copy(input, _coldPath);
        File.Copy(input, _recoveryPath);
        _store = new TraceStore();
        TraceStoreLoadResult loaded = _store.GetAsync(_warmPath, metric: Metric).GetAwaiter().GetResult();
        if (loaded.Trace.Info.SampleCount <= 0)
        {
            throw new InvalidOperationException($"The benchmark input contains no {Metric} samples.");
        }
    }

    /// <summary>
    ///  Removes only the benchmark's isolated cold cache before a conversion iteration.
    /// </summary>
    [IterationSetup(Target = nameof(ConvertAndLoad))]
    public void RemoveColdCache() => File.Delete(TraceConverter.EtlxPathFor(_coldPath));

    /// <summary>
    ///  Prepares a timestamp-current incompatible cache outside the measured operation.
    /// </summary>
    [IterationSetup(Target = nameof(RecoverAndLoad))]
    public void CorruptRecoveryCache()
    {
        string cachePath = TraceConverter.EtlxPathFor(_recoveryPath);
        File.WriteAllText(cachePath, "incompatible cache");
        File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow.AddMinutes(1));
    }

    /// <summary>
    ///  Releases the private input and ETLX copies.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    ///  Returns an already parsed view through the asynchronous store API.
    /// </summary>
    /// <returns>The cached model and request state.</returns>
    [Benchmark]
    public Task<TraceStoreLoadResult> ParsedHit() => _store.GetAsync(_warmPath, metric: Metric);

    /// <summary>
    ///  Loads a fresh view from an existing, valid ETLX cache.
    /// </summary>
    /// <returns>The newly parsed model and request state.</returns>
    [Benchmark]
    public Task<TraceStoreLoadResult> DiskHit() => new TraceStore().GetAsync(_warmPath, metric: Metric);

    /// <summary>
    ///  Converts an input without ETLX and loads the requested view.
    /// </summary>
    /// <returns>The converted model and request state.</returns>
    [Benchmark]
    public Task<TraceStoreLoadResult> ConvertAndLoad() => new TraceStore().GetAsync(_coldPath, metric: Metric);

    /// <summary>
    ///  Rebuilds an incompatible ETLX cache and loads the requested view.
    /// </summary>
    /// <returns>The recovered model and request state.</returns>
    [Benchmark]
    public Task<TraceStoreLoadResult> RecoverAndLoad() => new TraceStore().GetAsync(_recoveryPath, metric: Metric);

    /// <summary>
    ///  Exercises the cache command's preparation API on a valid current cache.
    /// </summary>
    /// <returns>The prepared cache path and request state.</returns>
    [Benchmark]
    public EtlxCacheResult PrepareCurrentCache() => TraceConverter.ConvertWithState(_warmPath);
}