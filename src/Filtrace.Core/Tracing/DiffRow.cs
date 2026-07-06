// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A single frame's change between a baseline and a current ranking.
/// </summary>
/// <param name="Frame">The shortened frame name.</param>
/// <param name="BeforeWeight">The frame's weight in the baseline ranking, in the metric's unit (0 if absent).</param>
/// <param name="AfterWeight">The frame's weight in the current ranking, in the metric's unit (0 if absent).</param>
/// <param name="Delta">The change in weight (<c>AfterWeight - BeforeWeight</c>); positive is a regression.</param>
public sealed record DiffRow(string Frame, double BeforeWeight, double AfterWeight, double Delta);
