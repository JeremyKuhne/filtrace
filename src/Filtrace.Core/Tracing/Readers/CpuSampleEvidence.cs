// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Tracks contributing ETW and SampleProfiler records without treating thread-stack
///  samples as time-weighted CPU work.
/// </summary>
internal struct CpuSampleEvidence
{
    /// <summary>
    ///  Provenance of SampleProfiler-only CPU-labeled counts.
    /// </summary>
    internal const string SampleProfilerSource = "sampleprofiler";

    /// <summary>
    ///  Provenance of CPU-labeled counts from both sample providers.
    /// </summary>
    internal const string MixedSource = "mixed-etw-sampleprofiler";

    private const string SampleProfilerWarning =
        "SampleProfiler thread-stack samples can include waits or native work; weights are raw counts, not on-core CPU milliseconds or blocked-time percentages. Use ETW PerfInfo for CPU time.";

    private const string MixedWarning =
        "Mixed ETW sampled-profile and SampleProfiler thread stacks: weights are raw counts from different samplers, not on-core CPU milliseconds or blocked-time percentages. Use ETW PerfInfo alone for CPU time.";

    private int _sampleProfilerCount;
    private int _etwSampleCount;

    /// <summary>
    ///  Gets whether at least one SampleProfiler record contributed to this result.
    /// </summary>
    internal bool HasSampleProfiler => _sampleProfilerCount > 0;

    /// <summary>
    ///  Gets the provider-specific advisory, or <see langword="null"/> when no
    ///  SampleProfiler sample contributed.
    /// </summary>
    internal string? Warning => !HasSampleProfiler
        ? null
        : _etwSampleCount > 0 ? MixedWarning : SampleProfilerWarning;

    /// <summary>
    ///  Observes a selected sample with a usable call stack.
    /// </summary>
    /// <param name="sampleProfiler">Whether the sample is a SampleProfiler thread-stack record.</param>
    internal void ObserveSample(bool sampleProfiler)
    {
        if (sampleProfiler)
        {
            if (_sampleProfilerCount < int.MaxValue)
            {
                _sampleProfilerCount++;
            }
        }
        else if (_etwSampleCount < int.MaxValue)
        {
            _etwSampleCount++;
        }
    }

    /// <summary>
    ///  Weights an ETW periodic sample by its own recorded interval, but never applies
    ///  that interval to a SampleProfiler thread-stack record.
    /// </summary>
    /// <param name="weighting">The ETW interval state, if available.</param>
    /// <param name="sampleProfiler">Whether this is a SampleProfiler record.</param>
    /// <returns>The weight of this sample before possible mixed-source normalization.</returns>
    internal double GetSampleWeight(CpuSampleWeighting? weighting, bool sampleProfiler)
    {
        ObserveSample(sampleProfiler);
        return sampleProfiler ? 1.0 : weighting?.GetSampleWeight() ?? 1.0;
    }

    /// <summary>
    ///  Describes the unit and provider evidence of the selected CPU-labeled samples.
    /// </summary>
    /// <param name="weighting">ETW interval evidence, or <see langword="null"/> for EventPipe.</param>
    /// <param name="sampleCount">The total number of selected samples.</param>
    /// <returns>Conservative CPU weight provenance for this result.</returns>
    internal CpuSampleProvenance CreateProvenance(CpuSampleWeighting? weighting, int sampleCount)
    {
        bool established = !HasSampleProfiler && weighting?.HasCompleteIntervalEvidence == true;
        int unknown = weighting is null
            ? sampleCount
            : (int)Math.Min((long)sampleCount, (long)weighting.UnknownIntervalSampleCount + _sampleProfilerCount);

        string source = HasSampleProfiler
            ? _etwSampleCount > 0 ? MixedSource : SampleProfilerSource
            : weighting?.Intervals.Count > 0 ? "etw-perfinfo" : "unavailable";

        return new CpuSampleProvenance(
            established ? "ms" : "samples",
            source,
            established,
            unknown,
            weighting?.Intervals ?? [])
        {
            OmittedIntervalSegmentCount = weighting?.OmittedIntervalSegmentCount ?? 0,
            OmittedIntervalSampleCount = weighting?.OmittedIntervalSampleCount ?? 0,
            IntervalsTruncated = weighting?.OmittedIntervalSegmentCount > 0
        };
    }

    /// <summary>
    ///  Appends the applicable interval or SampleProfiler advisory to the result.
    /// </summary>
    /// <param name="warnings">The result warnings to append to.</param>
    /// <param name="weighting">ETW interval evidence, if any.</param>
    /// <param name="provenance">The selected CPU weight provenance.</param>
    /// <param name="sampleCount">The number of selected samples.</param>
    internal void AddWarnings(
        List<string> warnings,
        CpuSampleWeighting? weighting,
        CpuSampleProvenance provenance,
        int sampleCount)
    {
        if (Warning is string sampleWarning)
        {
            warnings.Add(sampleWarning);
        }
        else if (provenance.TimeWeightsEstablished)
        {
            warnings.Add(
                provenance.Intervals.Count == 1
                    ? $"CPU sample weights use the trace-recorded ETW PerfInfo interval ({provenance.Intervals[0].IntervalMSec.ToString("0.####", CultureInfo.InvariantCulture)} ms)."
                    : "CPU sample weights use the trace-recorded ETW PerfInfo interval active at each sample; the interval changed during the trace.");
        }
        else if (sampleCount > 0)
        {
            warnings.Add(
                "CPU sampling interval is not recorded for every included sample; CPU weights are raw sample counts, not milliseconds.");
        }

        if (weighting?.OmittedIntervalSegmentCount > 0)
        {
            warnings.Add(
                $"CPU sampling provenance retained the first {weighting.Intervals.Count} interval segments and omitted {weighting.OmittedIntervalSegmentCount} later segments covering {weighting.OmittedIntervalSampleCount} samples.");
        }
    }

    /// <summary>
    ///  Tests whether the CPU weight provenance includes SampleProfiler records.
    /// </summary>
    /// <param name="provenance">The source's CPU sample provenance, if known.</param>
    /// <returns><see langword="true"/> when SampleProfiler samples contributed.</returns>
    internal static bool ContainsSampleProfiler(CpuSampleProvenance? provenance) =>
        provenance?.Source is SampleProfilerSource or MixedSource;

    /// <summary>
    ///  Gets the provider-specific warning from established CPU sample provenance.
    /// </summary>
    /// <param name="provenance">The CPU sample provenance, if known.</param>
    /// <returns>The warning when SampleProfiler samples contributed, or <see langword="null"/>.</returns>
    internal static string? WarningFor(CpuSampleProvenance? provenance) =>
        provenance?.Source switch
        {
            SampleProfilerSource => SampleProfilerWarning,
            MixedSource => MixedWarning,
            _ => null
        };
}
