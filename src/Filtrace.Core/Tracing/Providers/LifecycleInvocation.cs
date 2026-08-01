// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One invocation of the selected command: the root process, the descendants it
///  launched, and the wall-clock phases between them.
/// </summary>
/// <remarks>
///  <para>
///   The phases split a command's wall clock into the part before it launched work in
///   a child, the part the children were alive for, and the teardown after the last
///   child exited. That is the split sampled CPU cannot produce, because a parent
///   blocked waiting on a child owns no samples while it waits.
///  </para>
///  <para>
///   With several children the child span runs from the earliest child start to the
///   latest child stop, so the three phases still partition the root's lifetime.
///   <see cref="ChildStopToRootStopMs"/> is signed: a negative value means a child
///   outlived the root, which is normal for a console host or another detached helper.
///  </para>
/// </remarks>
/// <param name="Ordinal">The invocation's position in start order, from 1.</param>
/// <param name="Root">The root process.</param>
/// <param name="Children">The root's descendants, in start order.</param>
/// <param name="RootStartToChildStartMs">
///  Time from the root starting to its first child starting, or <see langword="null"/>
///  when the invocation launched no child.
/// </param>
/// <param name="ChildSpanMs">
///  Time from the first child starting to the last child stopping, or
///  <see langword="null"/> when the invocation launched no child.
/// </param>
/// <param name="ChildStopToRootStopMs">
///  Time from the last child stopping to the root stopping, or <see langword="null"/>
///  when the invocation launched no child.
/// </param>
/// <param name="Measurable">
///  Whether the capture observed both the root's start and its stop, so this
///  invocation's values are measurements rather than lower bounds.
/// </param>
public sealed record LifecycleInvocation(
    int Ordinal,
    LifecycleProcess Root,
    IReadOnlyList<LifecycleProcess> Children,
    double? RootStartToChildStartMs,
    double? ChildSpanMs,
    double? ChildStopToRootStopMs,
    bool Measurable);
