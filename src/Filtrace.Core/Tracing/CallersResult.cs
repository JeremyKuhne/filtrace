// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The immediate callers of a focus frame, with the weight each contributes.
/// </summary>
/// <param name="Focus">The substring the focus frame was matched on.</param>
/// <param name="TargetWeight">Total inclusive weight spent in the focus frame, in the metric's unit.</param>
/// <param name="PercentOfScope">The focus frame's share of the scoped total, in percent.</param>
/// <param name="ScopeWeight">Total scoped weight, in the metric's unit.</param>
/// <param name="Callers">The immediate callers, highest first.</param>
public sealed record CallersResult(
    string Focus,
    double TargetWeight,
    double PercentOfScope,
    double ScopeWeight,
    IReadOnlyList<CallerRow> Callers);
