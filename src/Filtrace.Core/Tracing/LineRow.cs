// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A single source line in a line-level self-time report.
/// </summary>
/// <param name="Method">The shortened method the line belongs to.</param>
/// <param name="Location">The source location (<c>file:line</c>), or <c>&lt;no source&gt;</c> when unresolved.</param>
/// <param name="Weight">Weight attributed to the line, in the metric's unit.</param>
/// <param name="PercentOfScope">Share of the scoped total, in percent.</param>
public sealed record LineRow(string Method, string Location, double Weight, double PercentOfScope);
