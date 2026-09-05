// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Defines the versioned, reproducible campaign metadata and its per-launch observations.
/// </summary>
/// <param name="SchemaVersion">The report contract version used by downstream comparisons.</param>
/// <param name="CreatedUtc">The round-trip UTC timestamp recorded after collection.</param>
/// <param name="Scenario">The registered scenario key that was executed.</param>
/// <param name="Iterations">The requested and completed launch count.</param>
/// <param name="Executable">The full path of the measured filtrace executable.</param>
/// <param name="Launches">The ordered observations, one for each campaign iteration.</param>
internal sealed record CliTelemetryReport(
    int SchemaVersion,
    string CreatedUtc,
    string Scenario,
    int Iterations,
    string Executable,
    IReadOnlyList<CliProcessTelemetry> Launches);
