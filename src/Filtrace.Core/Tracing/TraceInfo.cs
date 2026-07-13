// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Metadata and quality signals describing a loaded trace, returned up front so
///  a caller can pick its next query from real signals rather than empty results.
/// </summary>
public sealed class TraceInfo
{
    /// <summary>
    ///  Initializes a new <see cref="TraceInfo"/>.
    /// </summary>
    public TraceInfo(
        string path,
        TraceFormat format,
        double totalWeight,
        int sampleCount,
        double symbolResolutionRate,
        IReadOnlyList<ThreadSampleInfo> threads,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> availableAnalyses,
        EtlxCacheState? etlxCacheState = null)
        : this(
            path,
            format,
            totalWeight,
            sampleCount,
            symbolResolutionRate,
            threads,
            warnings,
            availableAnalyses,
            new Dictionary<string, AnalysisAvailability>(),
            etlxCacheState)
    {
    }

    /// <summary>
    ///  Initializes a new <see cref="TraceInfo"/> with per-analysis availability.
    /// </summary>
    public TraceInfo(
        string path,
        TraceFormat format,
        double totalWeight,
        int sampleCount,
        double symbolResolutionRate,
        IReadOnlyList<ThreadSampleInfo> threads,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> availableAnalyses,
        IReadOnlyDictionary<string, AnalysisAvailability> analyses,
        EtlxCacheState? etlxCacheState = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(availableAnalyses);
        ArgumentNullException.ThrowIfNull(analyses);

        Path = path;
        Format = format;
        TotalWeight = totalWeight;
        SampleCount = sampleCount;
        SymbolResolutionRate = symbolResolutionRate;
        Threads = threads;
        Warnings = warnings;
        AvailableAnalyses = availableAnalyses;
        Analyses = analyses;
        EtlxCacheState = etlxCacheState;
    }

    /// <summary>
    ///  The absolute path the trace was loaded from.
    /// </summary>
    public string Path { get; }

    /// <summary>
    ///  The on-disk format the trace was read from.
    /// </summary>
    public TraceFormat Format { get; }

    /// <summary>
    ///  Sum of the per-sample weights across all samples, in the source metric's
    ///  unit - milliseconds of CPU time for a CPU trace, bytes for an allocation
    ///  trace, one count per event for the exceptions trace. For CPU this is busy
    ///  time, not wall-clock: because every thread's samples are included, the value
    ///  can exceed the trace's wall-clock span when multiple threads ran concurrently.
    /// </summary>
    public double TotalWeight { get; }

    /// <summary>
    ///  Number of weighted samples in the normalized model.
    /// </summary>
    public int SampleCount { get; }

    /// <summary>
    ///  Fraction in <c>[0, 1]</c> of stack frames whose symbol resolved to a
    ///  method name. A value below <c>0.8</c> fires a quality warning; unresolved
    ///  native frames can lower the aggregate even when managed-method rankings
    ///  remain usable.
    /// </summary>
    public double SymbolResolutionRate { get; }

    /// <summary>
    ///  Per-thread sample counts, useful for picking a root frame or spotting
    ///  idle thread-pool noise.
    /// </summary>
    public IReadOnlyList<ThreadSampleInfo> Threads { get; }

    /// <summary>
    ///  Human-readable quality warnings (low symbol resolution, no samples, etc.).
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    ///  The analyses filtrace can run against this trace format. This is a format
    ///  constraint only; use <see cref="Analyses"/> for capture enablement and event
    ///  counts.
    /// </summary>
    public IReadOnlyList<string> AvailableAnalyses { get; }

    /// <summary>
    ///  Per-analysis format support, capture enablement, and observed source-record
    ///  count. Unlike <see cref="AvailableAnalyses"/>, this does not infer provider
    ///  availability from the file extension alone.
    /// </summary>
    public IReadOnlyDictionary<string, AnalysisAvailability> Analyses { get; }

    /// <summary>
    ///  How this request obtained the ETLX cache, or <see langword="null"/> for
    ///  formats such as speedscope that do not use ETLX.
    /// </summary>
    public EtlxCacheState? EtlxCacheState { get; }
}
