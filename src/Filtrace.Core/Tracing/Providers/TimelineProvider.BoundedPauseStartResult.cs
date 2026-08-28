// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  The outcome of adding one pending GC pause start to bounded state.
    /// </summary>
    internal enum BoundedPauseStartResult
    {
        /// <summary>
        ///  The start was retained.
        /// </summary>
        Added,

        /// <summary>
        ///  The same process/thread already had a pending start.
        /// </summary>
        Duplicate,

        /// <summary>
        ///  The pending-start budget was full.
        /// </summary>
        CapacityExceeded,

        /// <summary>
        ///  The start occurred after the selected window.
        /// </summary>
        AfterWindow,

        /// <summary>
        ///  The start carried a non-finite timestamp and was not retained.
        /// </summary>
        InvalidTimestamp
    }
}