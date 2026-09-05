// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  The outcome of matching one EE restart to pending suspension state.
    /// </summary>
    internal enum PauseRestartResult
    {
        /// <summary>
        ///  A valid GC pair overlaps the selected window.
        /// </summary>
        CompletedGc,

        /// <summary>
        ///  A valid non-GC pair was consumed without contributing pause evidence.
        /// </summary>
        CompletedNonGc,

        /// <summary>
        ///  No pending start exists, so the reason for this restart is unknown.
        /// </summary>
        MissingStart,

        /// <summary>
        ///  The retained pair contains a non-finite or non-monotonic timestamp.
        /// </summary>
        InvalidPair,

        /// <summary>
        ///  The restart cannot establish a pause overlapping the selected window.
        /// </summary>
        OutsideWindow
    }
}