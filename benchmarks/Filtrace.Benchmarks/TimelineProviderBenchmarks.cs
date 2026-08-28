// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures timeline aggregation over representative preconverted traces.
/// </summary>
[MemoryDiagnoser]
public class TimelineProviderBenchmarks
{
    private double _snapshotAtMs;
    private string _tracePath = null!;

    /// <summary>
    ///  The trace fixture, selected to exercise CPU/exception, GC/allocation, and JIT lanes.
    /// </summary>
    [Params("alloc", "exceptions", "jit", "threadpool")]
    public string Fixture { get; set; } = null!;

    /// <summary>
    ///  Resolves the committed trace corpus and validates both measured paths against
    ///  the fixture's expected evidence.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _tracePath = Path.Join(AppContext.BaseDirectory, "Fixtures", $"{Fixture}.nettrace");
        if (!File.Exists(_tracePath))
        {
            throw new InvalidOperationException($"The '{Fixture}' trace was not copied to benchmark output.");
        }

        TimelineResult timeline = Timeline();
        _snapshotAtMs = Math.Round(
            timeline.ToMs / 2.0,
            OutputJson.DoublePrecision,
            MidpointRounding.AwayFromZero);
        TimelineResult snapshot = Snapshot();
        if (!File.Exists($"{_tracePath}.etlx")
            || snapshot.Snapshot is not TimelineSnapshot evidence
            || evidence.Events.EventCount == 0
            || !HasExpectedEvidence(timeline, evidence))
        {
            throw new InvalidOperationException($"The '{Fixture}' fixture did not produce a warm cache and expected evidence.");
        }
    }

    /// <summary>
    ///  Builds the default five-lane, 50-bucket timeline over the complete trace.
    /// </summary>
    [Benchmark]
    public TimelineResult Timeline() =>
        new TimelineProvider().Read(_tracePath);

    /// <summary>
    ///  Builds the default bounded cross-lane window at the trace midpoint.
    /// </summary>
    [Benchmark]
    public TimelineResult Snapshot() =>
        new TimelineProvider().ReadSnapshot(
            _tracePath,
            atMs: _snapshotAtMs);

    private bool HasExpectedEvidence(TimelineResult timeline, TimelineSnapshot snapshot) => Fixture switch
    {
        "alloc" => timeline.Gc?.Sum(static bucket => bucket.Count) > 0
            && timeline.Alloc?.Sum(static bucket => bucket.Bytes) > 0
            && snapshot.Gc.CollectionCount > 0
            && snapshot.Alloc.Bytes > 0,
        "exceptions" => timeline.Cpu?.Sum(static bucket => bucket.SampleCount) > 0
            && timeline.Exceptions?.Sum(static bucket => bucket.Count) > 0
            && snapshot.Cpu.SampleCount > 0
            && snapshot.Exceptions.ExceptionCount > 0,
        "jit" => timeline.Jit?.Sum(static bucket => bucket.MethodCount) > 0
            && snapshot.Jit.CompilationCount > 0,
        "threadpool" => timeline.Cpu?.Sum(static bucket => bucket.SampleCount) > 0
            && snapshot.Cpu.SampleCount > 0,
        _ => false
    };
}
