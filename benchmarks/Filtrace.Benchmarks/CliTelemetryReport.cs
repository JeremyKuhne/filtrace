// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal sealed record CliTelemetryReport(
    int SchemaVersion,
    string CreatedUtc,
    string Scenario,
    int Iterations,
    string Executable,
    IReadOnlyList<CliProcessTelemetry> Launches);
