// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal abstract partial class TraceLogReader
{
    /// <summary>
    ///  Immutable normalized payload for one trace-local call-stack index.
    /// </summary>
    /// <param name="frames">The immutable outermost-first frame names.</param>
    /// <param name="frameLocations">The optional immutable outermost-first source locations.</param>
    /// <param name="resolvedFrameCount">The number of frames whose method name resolved.</param>
    private sealed class CachedSampleStack(
        IReadOnlyList<string> frames,
        IReadOnlyList<string>? frameLocations,
        int resolvedFrameCount)
    {
        /// <summary>
        ///  Gets the outermost-first frame names.
        /// </summary>
        public IReadOnlyList<string> Frames { get; } = frames;

        /// <summary>
        ///  Gets the optional outermost-first source locations.
        /// </summary>
        public IReadOnlyList<string>? FrameLocations { get; } = frameLocations;

        /// <summary>
        ///  Gets the number of frames whose method name resolved.
        /// </summary>
        public int ResolvedFrameCount { get; } = resolvedFrameCount;

        /// <summary>
        ///  Gets the saturating number of samples that reused this cached payload after its creation.
        /// </summary>
        public int ReusedSampleCount { get; private set; }

        /// <summary>
        ///  Adds one sample that reused this cached payload after its creation.
        /// </summary>
        public void ObserveReuse() =>
            ReusedSampleCount = ReusedSampleCount == int.MaxValue ? int.MaxValue : ReusedSampleCount + 1;
    }
}