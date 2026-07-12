// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Format support, capture enablement, and observed source-event count for one
///  analysis.
/// </summary>
/// <param name="FormatSupported">Whether filtrace supports the analysis for this trace format.</param>
/// <param name="CaptureStatus">Whether capture metadata establishes provider/keyword enablement.</param>
/// <param name="EventCount">
///  The observed source-event count when enabled, including zero; otherwise
///  <see langword="null"/> because absence is not evidence.
/// </param>
public sealed record AnalysisAvailability(
    bool FormatSupported,
    CaptureStatus CaptureStatus,
    int? EventCount);