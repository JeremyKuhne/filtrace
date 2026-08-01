// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The provider set an ETW capture enables: what the trace can answer, traded against
///  how much the instrumentation perturbs the process being measured.
/// </summary>
/// <remarks>
///  <para>
///   ETW kernel tracing is machine-wide, so every keyword is paid for by the whole box
///   for the whole capture. A profile names one useful point on that curve rather than
///   leaving the caller to assemble keyword sets by hand.
///  </para>
/// </remarks>
public enum CollectProfile
{
    /// <summary>
    ///  CPU sampling with the runtime detail an <c>.etl</c> analysis reads: the sampled
    ///  profiler plus process, thread, and image-load events, and the CLR keywords that
    ///  name managed methods and feed the timeline's GC, allocation, and exception lanes.
    ///  The general-purpose default, and what <c>cpu</c>, <c>classify</c>, and
    ///  <c>timeline</c> read.
    /// </summary>
    Cpu,

    /// <summary>
    ///  <see cref="Cpu"/> plus the context-switch and dispatcher keywords that carry
    ///  blocked intervals, so wall-clock time can be reconstructed. Feeds
    ///  <c>threadtime</c> as well as everything <see cref="Cpu"/> feeds. The most
    ///  expensive profile: a context switch is a far more frequent event than a sample.
    /// </summary>
    ThreadTime,

    /// <summary>
    ///  Low-perturbation CPU sampling for short-lived processes: the same kernel events
    ///  as <see cref="Cpu"/>, but only the CLR keywords that name managed methods - not
    ///  even the GC and exception keywords the timeline lanes read. Use it when the
    ///  capture must not materially change the lifetime of what it measures - a startup
    ///  path, or a native/AOT parent whose runtime events are noise.
    /// </summary>
    Startup,
}
