// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Self-time attributed to a single source line of a file in a heat map.
/// </summary>
/// <param name="Line">The 1-based source line number.</param>
/// <param name="Weight">Self-weight attributed to the line, in the metric's unit.</param>
/// <param name="PercentOfScope">Share of the whole-trace self-weight, in percent.</param>
/// <param name="SampleCount">Number of leaf samples attributed to the line.</param>
/// <param name="Method">The shortened method that dominates the line's self-weight.</param>
public sealed record HeatLine(int Line, double Weight, double PercentOfScope, int SampleCount, string Method);
