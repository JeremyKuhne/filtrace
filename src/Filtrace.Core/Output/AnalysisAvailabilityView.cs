// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Output;

/// <summary>
///  Agent-facing availability state for one analysis.
/// </summary>
/// <param name="CaptureStatus">Provider/keyword state: enabled, disabled, or unknown.</param>
/// <param name="EventCount">Observed source records, including zero when enabled; otherwise null.</param>
public sealed record AnalysisAvailabilityView(
    string CaptureStatus,
    int? EventCount);
