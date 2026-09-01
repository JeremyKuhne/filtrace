// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

public sealed partial class FoldingAggregator
{
    /// <summary>
    ///  A mutable call-tree node used while aggregating: a frame, the accumulating
    ///  inclusive weight of its subtree, and its child frames keyed by name. Converted
    ///  to the immutable <see cref="TreeNode"/> once aggregation completes.
    /// </summary>
    private sealed class TreeBuilder
    {
        /// <summary>
        ///  Creates an initially empty aggregation node for a normalized frame.
        /// </summary>
        /// <param name="frame">The frame represented by this node.</param>
        public TreeBuilder(string frame) => Frame = frame;

        /// <summary>
        ///  Gets the normalized frame represented by this node.
        /// </summary>
        public string Frame { get; }

        /// <summary>
        ///  Gets or sets the inclusive sample weight accumulated beneath this node.
        /// </summary>
        public double Weight { get; set; }

        // Allocated lazily: a leaf node (the common case at the bottom of every stack)
        // never calls anything, so most nodes keep this null.
        /// <summary>
        ///  Gets child nodes keyed by frame, or <see langword="null"/> while this node remains a leaf.
        /// </summary>
        public Dictionary<string, TreeBuilder>? Children { get; private set; }

        /// <summary>
        ///  Gets the existing child for a frame or creates it on first use.
        /// </summary>
        /// <param name="frame">The child frame to locate.</param>
        /// <returns>The mutable child aggregation node.</returns>
        public TreeBuilder Child(string frame)
        {
            Children ??= new Dictionary<string, TreeBuilder>(StringComparer.Ordinal);
            if (!Children.TryGetValue(frame, out TreeBuilder? child))
            {
                child = new TreeBuilder(frame);
                Children[frame] = child;
            }

            return child;
        }
    }
}
