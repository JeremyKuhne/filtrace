// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Output;

/// <summary>
///  An operation-neutral follow-up that retains the human reason for it.
/// </summary>
/// <param name="Reason">Why this follow-up is useful.</param>
public sealed record AnalysisNextStep(string Reason)
{
    /// <summary>
    ///  The surface-neutral operation name, or <see langword="null"/> for explanatory
    ///  guidance that is not a filtrace operation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }

    /// <summary>
    ///  The follow-up arguments, or <see langword="null"/> when none apply.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisNextStepArguments? Arguments { get; init; }
}
