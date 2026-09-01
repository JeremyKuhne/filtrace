// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Records resource use and output identity for one filtrace telemetry launch.
/// </summary>
/// <param name="Iteration">The one-based launch number within the campaign.</param>
/// <param name="Arguments">The exact argument tokens passed to filtrace.</param>
/// <param name="TotalProcessorMilliseconds">The child process CPU time in milliseconds.</param>
/// <param name="PeakWorkingSetBytes">The largest working set reported by the child process.</param>
/// <param name="MaxPrivateMemoryBytes">The largest private-byte value observed by polling.</param>
/// <param name="ExitCode">The child process exit code.</param>
/// <param name="StandardOutputLength">The number of bounded stdout characters captured.</param>
/// <param name="StandardErrorLength">The number of bounded stderr characters captured.</param>
/// <param name="OutputSha256">The hexadecimal digest used to compare stdout and stderr across launches.</param>
internal sealed record CliProcessTelemetry(
    int Iteration,
    IReadOnlyList<string> Arguments,
    double TotalProcessorMilliseconds,
    long PeakWorkingSetBytes,
    long MaxPrivateMemoryBytes,
    int ExitCode,
    int StandardOutputLength,
    int StandardErrorLength,
    string OutputSha256);
