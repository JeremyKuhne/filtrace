// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Output;

/// <summary>
///  Optional numeric values carried by known diagnostics.
/// </summary>
public sealed record AnalysisDiagnosticData
{
    /// <summary>
    ///  The number of records that contributed to the scoped result.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContributingRecords { get; init; }

    /// <summary>
    ///  The directional minimum recommended for the scoped result.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RecommendedMinimum { get; init; }

    /// <summary>
    ///  The integer percentage of frames that resolved.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ResolutionPercent { get; init; }

    /// <summary>
    ///  The minimum integer frame-resolution percentage expected.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinimumResolutionPercent { get; init; }
}
