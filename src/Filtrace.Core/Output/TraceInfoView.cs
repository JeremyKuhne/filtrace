// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;
using EtlxCacheStateValue = Filtrace.Tracing.EtlxCacheState;

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
///  Sum of the per-sample weights, in the metric's unit (CPU milliseconds or samples, bytes
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
/// <param name="EtlxCacheState">
///  How this request obtained the ETLX cache, or <see langword="null"/> when ETLX is not used.
/// </param>
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
    ///  Capture status and observed source-record count for each selector in
    ///  <see cref="AvailableAnalyses"/>. Loader-produced views populate this;
    ///  manually constructed legacy views may leave it <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<string, AnalysisAvailabilityView>? Analyses { get; init; }

    /// <summary>
    ///  Sampled managed source/PDB quality, separate from method-name
    ///  <see cref="SymbolResolutionRate"/>.
    /// </summary>
    public SourceResolutionInfo? SourceResolution { get; init; }

    /// <summary>
    ///  What the local native symbol pass covered, omitted when no symbol directory was
    ///  supplied or the trace had no unresolved native frames.
    /// </summary>
    public NativeSymbolInfo? NativeSymbols { get; init; }

    /// <summary>
    ///  Provenance and uncertainty for CPU sample weights.
    /// </summary>
    public CpuSampleProvenance? CpuSampling { get; init; }

    /// <summary>
    ///  Creates the shared CLI/MCP view of <paramref name="info"/>.
    /// </summary>
    /// <param name="info">The loaded trace information to map.</param>
    /// <param name="etlxCacheState">How this request obtained the ETLX cache.</param>
    /// <returns>The agent-facing trace-information view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is <see langword="null"/>.</exception>
    public static TraceInfoView FromTraceInfo(TraceInfo info, EtlxCacheStateValue? etlxCacheState)
    {
        ArgumentNullException.ThrowIfNull(info);

        Dictionary<string, AnalysisAvailabilityView> analyses = new(StringComparer.Ordinal);
        foreach ((string name, AnalysisAvailability availability) in info.Analyses)
        {
            if (!availability.FormatSupported)
            {
                continue;
            }

            analyses[name] = new AnalysisAvailabilityView(
                CaptureStatusText(availability.CaptureStatus),
                availability.EventCount);
        }

        return new TraceInfoView(
            info.Path,
            info.Format.ToString(),
            info.TotalWeight,
            info.SampleCount,
            info.SymbolResolutionRate,
            info.Threads,
            info.AvailableAnalyses,
            CacheStateText(etlxCacheState))
        {
            Analyses = analyses,
            SourceResolution = info.SourceResolution,
            NativeSymbols = info.NativeSymbols,
            CpuSampling = info.CpuSampling
        };
    }

    /// <summary>
    ///  Bounds the per-thread detail to the shared agent-output budget while retaining
    ///  the complete capture totals and quality metadata.
    /// </summary>
    /// <param name="view">The complete trace information view.</param>
    /// <param name="warning">
    ///  The warning naming the retained and available thread counts, or
    ///  <see langword="null"/> when every thread fit.
    /// </param>
    /// <returns>The bounded view, or <paramref name="view"/> when every thread fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="view"/> is <see langword="null"/>.</exception>
    public static TraceInfoView LimitThreads(TraceInfoView view, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(view);

        List<ThreadSampleInfo> boundedThreads = new(view.Threads.Count);
        bool labelsChanged = false;
        foreach (ThreadSampleInfo thread in view.Threads)
        {
            string label = CaptureManifestOutput.BoundFrame(thread.Thread);
            labelsChanged |= !string.Equals(label, thread.Thread, StringComparison.Ordinal);
            boundedThreads.Add(
                ReferenceEquals(label, thread.Thread)
                    ? thread
                    : new ThreadSampleInfo(label, thread.SampleCount));
        }

        List<ThreadSampleInfo> kept = OutputBudget.TakeWithinBudget(
            boundedThreads,
            static thread => OutputBudget.EstimateTokens(
                OutputJson.SerializeThreadSampleInfo(thread)),
            OutputBudget.DefaultRowBudgetTokens,
            out bool truncated,
            takeAtLeastOne: false);

        if (!truncated && !labelsChanged)
        {
            warning = null;
            return view;
        }

        warning = truncated
            ? $"Showing {kept.Count} of {view.Threads.Count} threads; more would exceed the response budget."
            : "";

        if (labelsChanged)
        {
            warning +=
                $"{(warning.Length == 0 ? "" : " ")}One or more thread labels were sanitized "
                    + $"or truncated to at most {CaptureManifestOutput.MaxFrameLength} characters.";
        }

        return view with { Threads = kept };
    }

    private static string? CacheStateText(EtlxCacheStateValue? state) => state switch
    {
        EtlxCacheStateValue.Hit => "hit",
        EtlxCacheStateValue.Waited => "waited",
        EtlxCacheStateValue.Converted => "converted",
        EtlxCacheStateValue.Recovered => "recovered",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown ETLX cache state.")
    };

    private static string CaptureStatusText(CaptureStatus status) => status switch
    {
        CaptureStatus.Enabled => "enabled",
        CaptureStatus.Disabled => "disabled",
        CaptureStatus.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown capture status.")
    };
}
