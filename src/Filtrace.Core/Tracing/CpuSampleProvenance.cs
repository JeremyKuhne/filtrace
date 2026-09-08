// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Tracing;

/// <summary>
///  Describes what establishes the weights of CPU sample records and whether
///  those weights can be interpreted as time.
/// </summary>
/// <param name="WeightUnit">The unit used by CPU sample weights.</param>
/// <param name="Source">The trace evidence from which the weights were derived.</param>
/// <param name="TimeWeightsEstablished">
///  Whether the trace establishes a time weight for every included CPU sample.
/// </param>
/// <param name="UnknownIntervalSampleCount">
///  Number of included periodic CPU samples for which no interval was recorded.
/// </param>
/// <param name="Intervals">Trace-recorded ETW timer intervals applied to included samples.</param>
public sealed record CpuSampleProvenance(
    string WeightUnit,
    string Source,
    bool TimeWeightsEstablished,
    int UnknownIntervalSampleCount,
    IReadOnlyList<CpuSampleIntervalSegment> Intervals)
{
    /// <summary>
    ///  Gets the number of later interval segments omitted from <see cref="Intervals"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OmittedIntervalSegmentCount { get; init; }

    /// <summary>
    ///  Gets the number of samples belonging to omitted interval segments.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OmittedIntervalSampleCount { get; init; }

    /// <summary>
    ///  Gets whether <see cref="Intervals"/> omits later segments because its retention limit was reached.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IntervalsTruncated { get; init; }
}
