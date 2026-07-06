// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A top-down call tree over a scoped trace: each node's children are the frames
///  it called, weighted by the metric spent in them, so an agent can follow the
///  hot path from the root down to the work that dominates it.
/// </summary>
/// <remarks>
///  <para>
///   The tree is rooted at a synthetic <c>&lt;root&gt;</c> node whose weight is the
///   scoped total; its children are the outermost frames of the scoped samples.
///   Folded frames (JIT helpers, the synthetic CPU marker) are skipped so the tree
///   shows only real methods, and the tree is bounded by a maximum depth and a
///   minimum per-node share so it stays readable and within an agent's token budget.
///  </para>
/// </remarks>
/// <param name="ScopeWeight">The scoped total, in the metric's unit (the percent denominator).</param>
/// <param name="RootFrame">The root frame the tree was scoped to, or empty for the whole trace.</param>
/// <param name="Root">The synthetic root node whose children are the scoped top-level frames.</param>
public sealed record CallTreeResult(
    double ScopeWeight,
    string RootFrame,
    TreeNode Root);
