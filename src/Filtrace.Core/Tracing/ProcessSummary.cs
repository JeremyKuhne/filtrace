// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  One process in a trace's CPU-sample inventory: its label, how many samples it
///  owns, the weight those samples carry, and that weight's share of the whole
///  capture.
/// </summary>
/// <param name="Process">
///  The process label (<c>name(pid)</c> for a multi-process capture), or empty for a
///  single-process trace format.
/// </param>
/// <param name="SampleCount">The number of CPU samples attributed to the process.</param>
/// <param name="Weight">The summed sample weight, in the metric's unit (milliseconds for CPU time).</param>
/// <param name="PercentOfScope">The process's share of the whole capture's weight, in percent.</param>
public sealed record ProcessSummary(
    string Process,
    int SampleCount,
    double Weight,
    double PercentOfScope);
