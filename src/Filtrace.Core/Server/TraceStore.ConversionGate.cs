// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Server;

public sealed partial class TraceStore
{
    /// <summary>
    ///  Coordinates conversion and lifetime state for one trace path.
    /// </summary>
    private sealed class ConversionGate
    {
        /// <summary>
        ///  Gets the single-entry semaphore that serializes conversion for this trace path.
        /// </summary>
        public SemaphoreSlim Semaphore { get; } = new(initialCount: 1, maxCount: 1);

        /// <summary>
        ///  Gets or sets the number of callers that hold or are waiting for this gate.
        /// </summary>
        public int References { get; set; }
    }
}
