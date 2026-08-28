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
        public SemaphoreSlim Semaphore { get; } = new(initialCount: 1, maxCount: 1);

        public int References { get; set; }
    }
}