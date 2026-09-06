// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal static partial class CliProcessRunner
{
    /// <summary>
    ///  Captures one child-process execution and its sampled resource counters.
    /// </summary>
    /// <param name="ExitCode">The child process exit code.</param>
    /// <param name="StandardOutput">The bounded stdout text drained during execution.</param>
    /// <param name="StandardError">The bounded stderr text drained during execution.</param>
    /// <param name="LaunchToExitElapsed">
    ///  The monotonic time from immediately before launch through root process exit.
    /// </param>
    /// <param name="TotalProcessorTime">The largest cumulative CPU time observed by polling.</param>
    /// <param name="PeakWorkingSetBytes">The largest OS-reported peak working set observed by polling.</param>
    /// <param name="MaxPrivateMemoryBytes">The largest sampled private-memory value observed by polling.</param>
    private sealed record ProcessObservation(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        TimeSpan LaunchToExitElapsed,
        TimeSpan TotalProcessorTime,
        long PeakWorkingSetBytes,
        long MaxPrivateMemoryBytes);
}
