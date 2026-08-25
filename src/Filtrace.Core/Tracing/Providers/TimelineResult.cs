// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  A time-bucketed correlation of what a trace was doing: the window and bucket
///  geometry, the process scoped to, and one aligned array per requested lane. Each
///  lane's array has <see cref="BucketCount"/> entries on the same time axis, so a
///  spike in one lane reads against the others at the same instant. A lane not
///  requested is <see langword="null"/>, distinguishing "not asked for" from "asked
///  for, nothing happened".
/// </summary>
/// <param name="FromMs">Window start, in milliseconds from the trace start.</param>
/// <param name="ToMs">Window end, in milliseconds from the trace start.</param>
/// <param name="BucketSizeMs">Width of each bucket, in milliseconds.</param>
/// <param name="BucketCount">Number of buckets each requested lane is divided into.</param>
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
}
