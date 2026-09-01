// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures fresh CPU loads from one preconverted activity trace.
/// </summary>
[MemoryDiagnoser]
public class ActivityReadBenchmarks
{
    private string _activityTrace = null!;
    private ScopeRequest _orderScope = null!;

    /// <summary>
    ///  Preconverts the trace and validates the named activity scope.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _activityTrace = Path.Join(AppContext.BaseDirectory, "Fixtures", "activity.nettrace");
        _orderScope = ScopeRequest.Auto.WithActivity("Order");

        LoadedTrace whole = new TraceLoader().Load(_activityTrace, TraceMetric.Cpu);
        LoadedTrace scoped = new TraceLoader().Load(
            _activityTrace,
            TraceMetric.Cpu,
            scope: _orderScope);

        if (whole.Info.SampleCount <= 0
            || scoped.Info.SampleCount <= 0
            || scoped.Info.SampleCount >= whole.Info.SampleCount)
        {
            throw new InvalidOperationException(
                "The activity fixture did not produce a nonempty selective Order scope.");
        }
    }

    /// <summary>
    ///  Loads all CPU samples with a fresh loader.
    /// </summary>
    /// <returns>The loaded trace and its complete CPU sample source.</returns>
    [Benchmark(Baseline = true)]
    public LoadedTrace Unscoped() =>
        new TraceLoader().Load(_activityTrace, TraceMetric.Cpu);

    /// <summary>
    ///  Loads CPU samples inside Order activities with a fresh loader.
    /// </summary>
    /// <returns>The loaded trace whose CPU source retains only matching activity samples.</returns>
    [Benchmark]
    public LoadedTrace OrderScoped() =>
        new TraceLoader().Load(_activityTrace, TraceMetric.Cpu, scope: _orderScope);
}
