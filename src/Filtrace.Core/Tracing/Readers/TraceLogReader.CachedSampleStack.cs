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
    internal sealed class CachedSampleStack(
        IReadOnlyList<string> frames,
        IReadOnlyList<string>? frameLocations,
        int resolvedFrameCount)
    {
        private Dictionary<(double Weight, int ThreadId, string Process), SampleStack>? _samples;

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
        ///  Gets the number of retained sample variants.
        /// </summary>
        internal int CachedSampleVariantCount => _samples?.Count ?? 0;

        /// <summary>
        ///  Gets or creates an immutable sample object for one weight, thread, and process combination.
        /// </summary>
        /// <param name="weight">The sample weight in the source metric's unit.</param>
        /// <param name="threadId">The sampled operating-system thread identifier.</param>
        /// <param name="thread">The canonical rendered thread label.</param>
        /// <param name="process">The canonical rendered process label.</param>
        /// <returns>A shared sample while the bounded variant cache has capacity; otherwise a new sample.</returns>
        public SampleStack GetOrCreateSample(double weight, int threadId, string thread, string process)
        {
            _samples ??= [];
            (double Weight, int ThreadId, string Process) key = (weight, threadId, process);
            if (_samples.TryGetValue(key, out SampleStack? sample))
            {
                return sample;
            }

            sample = new SampleStack(Frames, weight, thread, FrameLocations, process);
            if (CanCacheSampleVariant(_samples.Count))
            {
                _samples.Add(key, sample);
            }

            return sample;
        }

        /// <summary>
        ///  Adds one sample that reused this cached payload after its creation.
        /// </summary>
        public void ObserveReuse() =>
            ReusedSampleCount = ReusedSampleCount == int.MaxValue ? int.MaxValue : ReusedSampleCount + 1;

    }

}