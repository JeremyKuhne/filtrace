// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  A temporal correlation of what a trace was doing. Bucket mode returns aligned
///  arrays for requested lanes; snapshot mode returns one bounded cross-lane
///  <see cref="Snapshot"/> around a selected timestamp and leaves the lane arrays
///  <see langword="null"/>.
/// </summary>
/// <param name="FromMs">Window start, in milliseconds from the trace start.</param>
/// <param name="ToMs">Window end, in milliseconds from the trace start.</param>
/// <param name="BucketSizeMs">
///  Width of each aligned bucket in bucket mode; resolved window width in snapshot mode.
/// </param>
/// <param name="BucketCount">
///  Number of aligned buckets in bucket mode; one in snapshot mode.
/// </param>
/// <param name="Process">Process tree scoped to (explicit or auto-busiest), or <see langword="null"/> for every process.</param>
/// <param name="Gc">GC lane, or <see langword="null"/> when not requested.</param>
/// <param name="Cpu">CPU lane, or <see langword="null"/> when not requested.</param>
/// <param name="Exceptions">Exceptions lane, or <see langword="null"/> when not requested.</param>
/// <param name="Alloc">Allocation lane, or <see langword="null"/> when not requested.</param>
/// <param name="Jit">JIT lane, or <see langword="null"/> when not requested.</param>
public sealed record TimelineResult(
    double FromMs,
    double ToMs,
    double BucketSizeMs,
    int BucketCount,
    string? Process,
    IReadOnlyList<GcBucket>? Gc,
    IReadOnlyList<CpuBucket>? Cpu,
    IReadOnlyList<ExceptionBucket>? Exceptions,
    IReadOnlyList<AllocBucket>? Alloc,
    IReadOnlyList<JitBucket>? Jit)
{
    /// <summary>
    ///  The non-default timeline representation, or <see langword="null"/> for the
    ///  ordinary aligned-bucket result.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    ///  Bounded cross-lane evidence when <see cref="Mode"/> is <c>snapshot</c>;
    ///  otherwise <see langword="null"/>.
    /// </summary>
    public TimelineSnapshot? Snapshot { get; init; }

    /// <summary>
    ///  The exact process scope applied to this result, retained for follow-up routing
    ///  and omitted from result JSON. Timeline heads currently use it only to preserve
    ///  scope in generated follow-ups.
    /// </summary>
    [JsonIgnore]
    public AppliedProcessScope? AppliedProcessScope { get; init; }

    /// <summary>
    ///  Advisories produced while resolving the process scope, retained for the CLI
    ///  and MCP envelopes and omitted from result JSON.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> ScopeWarnings { get; init; } = [];
}
