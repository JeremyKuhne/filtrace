// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A provider's output: the normalized weighted sample stacks for one metric,
///  paired with the <see cref="MetricInfo"/> describing what their weights mean.
/// </summary>
/// <remarks>
///  <para>
///   This is the seam between the investigation families - the CPU provider
///   today; thread-time, allocation, and others later - and the
///   provider-agnostic engine that ranks, drills, and folds them. The CPU
///   provider's output is the trace's sampled stacks paired with
///   <see cref="MetricInfo.Cpu"/>.
///  </para>
///  <para>
///   Until a second family lands there is no provider interface; a family is
///   simply whatever builds one of these from a <c>TraceLog</c>. The interface
///   is deferred until a real second provider can pin its shape.
///  </para>
/// </remarks>
public sealed class StackSampleSource
{
    /// <summary>
    ///  Initializes a new <see cref="StackSampleSource"/>.
    /// </summary>
    /// <param name="metric">The metric the sample weights are measured in.</param>
    /// <param name="samples">The normalized weighted sample stacks.</param>
    /// <param name="recordSemantics">What each normalized record represents.</param>
    public StackSampleSource(
        MetricInfo metric,
        IReadOnlyList<SampleStack> samples,
        StackRecordSemantics recordSemantics = StackRecordSemantics.Unavailable)
    {
        Metric = metric;
        Samples = samples;
        RecordSemantics = recordSemantics;
    }

    /// <summary>
    ///  The metric the sample weights are measured in.
    /// </summary>
    public MetricInfo Metric { get; }

    /// <summary>
    ///  The normalized weighted sample stacks.
    /// </summary>
    public IReadOnlyList<SampleStack> Samples { get; }

    /// <summary>
    ///  What one entry in <see cref="Samples"/> represents for count and quality
    ///  interpretation. This is separate from its metric weight.
    /// </summary>
    public StackRecordSemantics RecordSemantics { get; }
}
