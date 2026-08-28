// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal sealed partial class SourceResolutionTracker
{
    /// <summary>
    ///  Accumulates source-resolution evidence for one managed method.
    /// </summary>
    private sealed class MethodResolution(string? name)
    {
        public string? Name { get; } = name;
        public int SampledFrames { get; set; }
        public int MappedFrames { get; set; }
    }
}