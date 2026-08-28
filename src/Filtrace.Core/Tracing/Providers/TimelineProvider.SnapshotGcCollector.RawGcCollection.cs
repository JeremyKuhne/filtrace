// Copyright (c) 2025 Jeremy W Kuhne
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

            public GcIdentity Identity { get; }

            public double StartMs { get; }

            public int Generation { get; }

            public string Kind { get; }

            public string Reason { get; }

            public bool IsBackground { get; }

            public double? EndMs { get; set; }

            public double PauseMs { get; set; }

            public double LastPauseStartMs { get; set; } = double.NaN;

            public double LastPauseEndMs { get; set; } = double.NaN;

            public bool PauseContainsStart { get; set; }

            public bool PauseContainsEnd { get; set; }
        }
    }
}