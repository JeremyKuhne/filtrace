// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    internal sealed partial class SnapshotGcCollector
    {
        /// <summary>
        ///  Identifies one collection within a process and CLR instance.
        /// </summary>
        private readonly record struct GcIdentity(
            int ProcessInstanceIndex,
            int ClrInstanceId,
            int CollectionNumber);
    }
}