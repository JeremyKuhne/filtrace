// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal sealed partial class SourceResolutionTracker
{
    /// <summary>
    ///  Accumulates source-resolution evidence for one managed method.
    /// </summary>
    /// <param name="name">The normalized method identity, or <see langword="null"/> when unnamed.</param>
    private sealed class MethodResolution(string? name)
    {
        /// <summary>
        ///  Gets the normalized method identity when the frame had a name.
        /// </summary>
        public string? Name { get; } = name;

        /// <summary>
        ///  Gets or sets the saturating count of sampled frames attributed to this method.
        /// </summary>
        public int SampledFrames { get; set; }

        /// <summary>
        ///  Gets or sets the saturating subset of sampled frames that resolved to source.
        /// </summary>
        public int MappedFrames { get; set; }
    }
}
