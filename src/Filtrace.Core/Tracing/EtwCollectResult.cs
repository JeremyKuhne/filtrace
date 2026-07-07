// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The outcome of an ETW capture.
/// </summary>
public sealed class EtwCollectResult
{
    /// <summary>The <c>.etl</c> file the capture was written to (absolute path).</summary>
    public required string OutputPath { get; init; }

    /// <summary>The process id of the launched process.</summary>
    public required int ProcessId { get; init; }

    /// <summary>
    ///  The launched executable's base name, the value to scope analysis with
    ///  <c>--process</c> against the machine-wide capture.
    /// </summary>
    public required string ProcessName { get; init; }

    /// <summary>
    ///  The launched process's exit code, or <c>-1</c> if it was terminated at the
    ///  duration cap.
    /// </summary>
    public required int ProcessExitCode { get; init; }

    /// <summary>The size of the written <c>.etl</c> in bytes.</summary>
    public required long FileSizeBytes { get; init; }
}
