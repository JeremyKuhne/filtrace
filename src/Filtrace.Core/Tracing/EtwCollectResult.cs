// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The outcome of an ETW capture.
/// </summary>
public sealed class EtwCollectResult
{
    /// <summary>
    ///  The <c>.etl</c> file the capture was written to (absolute path).
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    ///  The process id of the launched process.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   The first launch's id when the capture ran several; <see cref="Invocations"/>
    ///   carries them all.
    ///  </para>
    /// </remarks>
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
    /// <remarks>
    ///  <para>
    ///   When the capture ran several launches this is the first failing exit code, so a
    ///   caller checking one value still sees that something failed; it is the last
    ///   launch's code when they all succeeded.
    ///  </para>
    /// </remarks>
    public required int ProcessExitCode { get; init; }

    /// <summary>
    ///  Every launch the session captured, in order. Always at least one entry.
    /// </summary>
    public required IReadOnlyList<EtwInvocation> Invocations { get; init; }

    /// <summary>
    ///  The size of the written <c>.etl</c> in bytes.
    /// </summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>
    ///  The profile the capture was recorded with.
    /// </summary>
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
    ///  The CPU sample interval this capture asked for and the one the operating system
    ///  will honor, with the bounds it reported.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Windows accepts and echoes back any interval but only honors it inside the
    ///   profile source's bounds, so the applied rate cannot be read back from the
    ///   session - it is derived from those bounds. When
    ///   <see cref="CpuSampleInterval.Clamped"/> is set the capture sampled at a
    ///   different rate than requested, and every weight derived from it is scaled to
    ///   the effective interval rather than the requested one.
    ///  </para>
    /// </remarks>
    public required CpuSampleInterval CpuSample { get; init; }
}
