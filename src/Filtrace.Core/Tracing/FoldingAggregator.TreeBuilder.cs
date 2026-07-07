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
        public TreeBuilder(string frame) => Frame = frame;

        public string Frame { get; }

        public double Weight { get; set; }

        // Allocated lazily: a leaf node (the common case at the bottom of every stack)
        // never calls anything, so most nodes keep this null.
        public Dictionary<string, TreeBuilder>? Children { get; private set; }

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
