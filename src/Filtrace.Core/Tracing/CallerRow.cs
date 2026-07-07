// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A single immediate caller of a focus frame.
/// </summary>
/// <param name="Caller">The shortened caller frame name, or <c>&lt;root&gt;</c>.</param>
/// <param name="Weight">Weight this caller contributes to the focus frame, in the metric's unit.</param>
/// <param name="PercentOfTarget">Share of the focus frame's total, in percent.</param>
public sealed record CallerRow(string Caller, double Weight, double PercentOfTarget);
