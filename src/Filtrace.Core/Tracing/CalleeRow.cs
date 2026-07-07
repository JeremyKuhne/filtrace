// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A single immediate callee of a focus frame - a method the focus frame called,
///  or <c>&lt;self&gt;</c> for the focus frame's own self-time.
/// </summary>
/// <param name="Callee">The shortened callee frame name, or <c>&lt;self&gt;</c> for self-time.</param>
/// <param name="Weight">Weight the focus frame spent in this callee, in the metric's unit.</param>
/// <param name="PercentOfTarget">Share of the focus frame's total, in percent.</param>
public sealed record CalleeRow(string Callee, double Weight, double PercentOfTarget);
