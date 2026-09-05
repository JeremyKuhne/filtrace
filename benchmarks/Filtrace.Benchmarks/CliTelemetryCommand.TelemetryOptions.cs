// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal static partial class CliTelemetryCommand
{
    /// <summary>
    ///  Carries the validated paths and launch count used to prepare a telemetry campaign.
    /// </summary>
    /// <param name="Scenario">The registered scenario key to execute.</param>
    /// <param name="TracePath">The source trace supplied to the selected command shape.</param>
    /// <param name="OutputPath">The JSON report path, which must not alias an input or executable.</param>
    /// <param name="FiltracePath">An explicit child executable, or <see langword="null"/> to discover it.</param>
    /// <param name="Iterations">The number of child launches to record.</param>
    private sealed record TelemetryOptions(
        string Scenario,
        string TracePath,
        string OutputPath,
        string? FiltracePath,
        int Iterations);
}
