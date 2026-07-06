// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A single ranked frame in a self-time or inclusive-time report.
/// </summary>
/// <param name="Frame">The shortened frame name.</param>
/// <param name="Weight">Weight attributed to the frame, in the metric's unit (milliseconds for CPU, bytes for allocations).</param>
/// <param name="PercentOfScope">Share of the scoped total, in percent.</param>
public sealed record RankRow(string Frame, double Weight, double PercentOfScope);
