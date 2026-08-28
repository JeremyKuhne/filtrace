// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal static partial class CliTelemetryCommand
{
    /// <summary>
    ///  Contains validated command-line options for a telemetry run.
    /// </summary>
    private sealed record TelemetryOptions(
        string Scenario,
        string TracePath,
        string OutputPath,
        string? FiltracePath,
        int Iterations);
}