// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal static partial class CliProcessRunner
{
    /// <summary>
    ///  Captures one child-process execution and its sampled resource counters.
    /// </summary>
    private sealed record ProcessObservation(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        TimeSpan TotalProcessorTime,
        long PeakWorkingSetBytes,
        long MaxPrivateMemoryBytes);
}