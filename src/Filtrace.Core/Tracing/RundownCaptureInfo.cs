// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Provenance for an opt-in CLR naming rundown appended to an ETW capture.
/// </summary>
/// <param name="FileSizeBytes">The temporary rundown ETL size before merge.</param>
/// <param name="EventsLost">Events the rundown session reported losing.</param>
/// <param name="PollCount">Two-second file-growth polls required to reach quiescence.</param>
/// <param name="DurationMilliseconds">Rundown capture and merge duration.</param>
public sealed record RundownCaptureInfo(
    long FileSizeBytes,
    int EventsLost,
    int PollCount,
    double DurationMilliseconds);
