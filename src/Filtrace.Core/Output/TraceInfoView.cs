// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Output;

/// <summary>
///  The <c>trace_info</c> payload: a loaded trace's identity and quality signals.
/// </summary>
/// <remarks>
///  <para>
///   The trace's quality warnings are not repeated here; they travel in the
///   <see cref="AnalysisResult{T}.Warnings"/> channel of the envelope this view is
///   wrapped in, the same uniform channel every other tool reports them through.
///  </para>
/// </remarks>
/// <param name="Path">The absolute path the trace was loaded from.</param>
/// <param name="Format">The on-disk format the trace was read from.</param>
/// <param name="TotalWeight">
///  Sum of the per-sample weights, in the metric's unit (CPU milliseconds, bytes
///  allocated, or one count per event).
/// </param>
/// <param name="SampleCount">Number of weighted samples in the normalized model.</param>
/// <param name="SymbolResolutionRate">
///  Fraction in <c>[0, 1]</c> of frames that resolved to a method name. Below
///  <c>0.8</c> fires a quality warning; unresolved native frames can lower the
///  aggregate even when managed-method rankings remain usable.
/// </param>
/// <param name="Threads">Per-thread sample counts, highest first.</param>
/// <param name="AvailableAnalyses">
///  The analyses this trace format supports. This does not establish capture
///  enablement; use <see cref="Analyses"/> for that.
/// </param>
/// <param name="EtlxCacheState">How this request obtained the ETLX cache, or <see langword="null"/> when ETLX is not used.</param>
public sealed record TraceInfoView(
    string Path,
    string Format,
    double TotalWeight,
    int SampleCount,
    double SymbolResolutionRate,
    IReadOnlyList<ThreadSampleInfo> Threads,
    IReadOnlyList<string> AvailableAnalyses,
    string? EtlxCacheState = null)
{
    /// <summary>
    ///  Per-analysis format support, capture status, and observed event count.
    ///  Loader-produced views populate this; manually constructed legacy views may
    ///  leave it <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<string, AnalysisAvailabilityView>? Analyses { get; init; }
}
