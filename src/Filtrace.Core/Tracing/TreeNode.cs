// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A node in a call tree: a frame, the weight of the subtree rooted at it, that
///  subtree's share of the scoped total, and the frames it called.
/// </summary>
/// <remarks>
///  <para>
///   The tree is path-based: a method that appears at two different stack
///   positions is two nodes, and a recursive call shows as a node nested under
///   itself, so a node's <see cref="Children"/> are exactly the frames called at
///   that point on the stack. <see cref="Weight"/> is inclusive - it sums every
///   sample that passed through this node - so a node's weight is at least the sum
///   of its children's.
///  </para>
/// </remarks>
/// <param name="Frame">The shortened frame name, or <c>&lt;root&gt;</c> for the synthetic root.</param>
/// <param name="Weight">The subtree's inclusive weight, in the metric's unit (milliseconds for CPU, bytes for allocations).</param>
/// <param name="PercentOfScope">The subtree's share of the scoped total, in percent.</param>
/// <param name="Children">The called frames, highest weight first.</param>
public sealed record TreeNode(
    string Frame,
    double Weight,
    double PercentOfScope,
    IReadOnlyList<TreeNode> Children);
