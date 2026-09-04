// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures warm CPU trace replay from a restored external scale input.
/// </summary>
[MemoryDiagnoser]
public class TraceReadScaleBenchmarks
{
    private string _trace = null!;

    /// <summary>
    ///  Resolves and preconverts the externally supplied trace.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _trace = Environment.GetEnvironmentVariable("FILTRACE_BENCHMARK_TRACE_PATH")
            ?? throw new InvalidOperationException(
                "FILTRACE_BENCHMARK_TRACE_PATH must identify the restored scale trace.");

        if (!File.Exists(_trace))
        {
            throw new FileNotFoundException("The restored scale trace was not found.", _trace);
        }

        TraceConverter.Clean(_trace);
        TraceConverter.Convert(_trace);
        LoadedTrace loaded = new TraceLoader().Load(_trace, TraceMetric.Cpu);
        if (loaded.Info.SampleCount <= 0)
        {
            throw new InvalidOperationException("The scale trace contains no CPU samples.");
        }
    }

    /// <summary>
    ///  Loads all CPU samples from the warm ETLX cache with a fresh loader.
    /// </summary>
    /// <returns>The loaded trace and its complete CPU sample source.</returns>
    [Benchmark]
    public LoadedTrace ReadCpu() => new TraceLoader().Load(_trace, TraceMetric.Cpu);
}