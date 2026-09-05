// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    internal sealed partial class SnapshotGcCollector
    {
        /// <summary>
        ///  Retains the raw event state needed to summarize one GC collection.
        /// </summary>
        private sealed class RawGcCollection
        {
            /// <summary>
            ///  Captures the runtime start-event fields needed to associate pauses and decide snapshot overlap.
            /// </summary>
            /// <param name="identity">The process, CLR instance, and collection sequence identity.</param>
            /// <param name="startMs">The collection start in trace-relative milliseconds.</param>
            /// <param name="generation">The condemned generation reported by the runtime.</param>
            /// <param name="kind">The runtime GC type name.</param>
            /// <param name="reason">The runtime trigger reason name.</param>
            /// <param name="isBackground">Whether the collection can continue outside its stop-the-world pause.</param>
            public RawGcCollection(
                GcIdentity identity,
                double startMs,
                int generation,
                string kind,
                string reason,
                bool isBackground)
            {
                Identity = identity;
                StartMs = startMs;
                Generation = generation;
                Kind = kind;
                Reason = reason;
                IsBackground = isBackground;
            }

            /// <summary>
            ///  Gets the collection's process and CLR-local sequence identity.
            /// </summary>
            public GcIdentity Identity { get; }

            /// <summary>
            ///  Gets the collection start in trace-relative milliseconds.
            /// </summary>
            public double StartMs { get; }

            /// <summary>
            ///  Gets the condemned generation reported by the runtime.
            /// </summary>
            public int Generation { get; }

            /// <summary>
            ///  Gets the runtime GC type name.
            /// </summary>
            public string Kind { get; }

            /// <summary>
            ///  Gets the runtime reason that triggered the collection.
            /// </summary>
            public string Reason { get; }

            /// <summary>
            ///  Gets whether collection work can extend beyond its stop-the-world pause.
            /// </summary>
            public bool IsBackground { get; }

            /// <summary>
            ///  Gets or sets the collection end in trace-relative milliseconds when observed.
            /// </summary>
            public double? EndMs { get; set; }

            /// <summary>
            ///  Gets or sets the sum of pause intervals attributed to this collection.
            /// </summary>
            public double PauseMs { get; set; }

            /// <summary>
            ///  Gets or sets the most recent attributed pause start, or <see cref="double.NaN"/> before any pause.
            /// </summary>
            public double LastPauseStartMs { get; set; } = double.NaN;

            /// <summary>
            ///  Gets or sets the most recent attributed pause end, or <see cref="double.NaN"/> before any pause.
            /// </summary>
            public double LastPauseEndMs { get; set; } = double.NaN;

            /// <summary>
            ///  Gets or sets whether an in-window attributed pause contains the collection start.
            /// </summary>
            public bool PauseContainsStart { get; set; }

            /// <summary>
            ///  Gets or sets whether an in-window attributed pause contains the collection end.
            /// </summary>
            public bool PauseContainsEnd { get; set; }
        }
    }
}
