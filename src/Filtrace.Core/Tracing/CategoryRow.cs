// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  One runtime work category in a classification: its name, the self-time weight
///  attributed to it, and that weight's share of the scoped total.
/// </summary>
/// <param name="Category">
///  The category name (see <see cref="FrameCategories"/>): zeroing, copying,
///  write-barrier, gc, jit, or other.
/// </param>
/// <param name="Weight">The summed self-time weight, in the metric's unit (milliseconds for CPU).</param>
/// <param name="PercentOfScope">The category's share of the scoped total, in percent.</param>
public sealed record CategoryRow(
    string Category,
    double Weight,
    double PercentOfScope);
