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

    /// <summary>The profile the capture was recorded with.</summary>
    public required CollectProfile Profile { get; init; }

    /// <summary>
    ///  The kernel provider keywords the capture enabled, comma-separated.
    /// </summary>
    public required string KernelKeywords { get; init; }

    /// <summary>
    ///  The CLR provider keywords the capture enabled, comma-separated, or <c>"none"</c>
    ///  when the CLR provider was left off.
    /// </summary>
    public required string ClrKeywords { get; init; }

    /// <summary>
    ///  The CPU sample interval the session actually applied, in milliseconds, read back
    ///  after the request was set. Windows clamps the interval to what the platform and
    ///  the caller's privileges allow, so this can differ from what was asked for.
    /// </summary>
    public required double EffectiveCpuSampleMSec { get; init; }
}
