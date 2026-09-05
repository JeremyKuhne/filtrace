// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The process lifecycle report: every invocation of the selected command in a
///  capture, the wall-clock phases each one spent, and the medians across them.
/// </summary>
/// <remarks>
///  <para>
///   Every duration here is wall-clock time derived from kernel process and image
///   events, which is a different measure from the sampled CPU time the ranking verbs
///   report. A command that takes 50 ms of wall clock but owns 12 ms of samples spent
///   the difference blocked - in the loader, waiting on a child, or in teardown - and
///   this report is what attributes it.
///  </para>
///  <para>
///   Invocations whose start or stop the capture did not observe are listed but
///   excluded from <see cref="Phases"/> and <see cref="ImageMilestones"/>, because a
///   lifetime clipped to the capture window is a lower bound rather than a value a
///   median should absorb.
///  </para>
/// </remarks>
/// <param name="Scope">
///  How the roots were selected, for the report header; empty when the trace carried no
///  process the report could resolve a selector against.
/// </param>
/// <param name="InvocationCount">How many root invocations matched.</param>
/// <param name="MeasuredCount">How many of those had both a start and a stop observed.</param>
/// <param name="TotalRootCpuMs">The summed sampled CPU time of every matched root.</param>
/// <param name="TotalChildCpuMs">The summed sampled CPU time of every matched descendant.</param>
/// <param name="Phases">The wall-clock phase summaries, in lifecycle order.</param>
/// <param name="Invocations">The per-invocation detail, in start order.</param>
/// <param name="ImageMilestones">The requested loader milestones, in load order.</param>
public sealed record LifecycleResult(
    string Scope,
    int InvocationCount,
    int MeasuredCount,
    double TotalRootCpuMs,
    double TotalChildCpuMs,
    IReadOnlyList<LifecyclePhase> Phases,
    IReadOnlyList<LifecycleInvocation> Invocations,
    IReadOnlyList<LifecycleImageMilestone> ImageMilestones);
