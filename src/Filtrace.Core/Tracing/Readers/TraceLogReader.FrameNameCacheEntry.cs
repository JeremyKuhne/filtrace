// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal abstract partial class TraceLogReader
{
    /// <summary>
    ///  Caches one rendered frame identity and its aggregated source-resolution counts.
    /// </summary>
    /// <param name="name">The canonical rendered frame name.</param>
    /// <param name="methodKey">The trace-local method identity, or the invalid sentinel.</param>
    /// <param name="module">The module metadata used for source-quality reporting.</param>
    /// <param name="moduleName">The module name used for source-quality reporting.</param>
    /// <param name="methodName">The method name used for source-quality reporting.</param>
    private sealed class FrameNameCacheEntry(
        string name,
        int methodKey,
        TraceModuleFile? module,
        string moduleName,
        string methodName)
    {
        /// <summary>
        ///  Gets the canonical rendered frame name.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        ///  Gets the trace-local method identity, or the invalid sentinel.
        /// </summary>
        public int MethodKey { get; } = methodKey;

        /// <summary>
        ///  Gets the module metadata used for source-quality reporting.
        /// </summary>
        public TraceModuleFile? Module { get; } = module;

        /// <summary>
        ///  Gets the module name used for source-quality reporting.
        /// </summary>
        public string ModuleName { get; } = moduleName;

        /// <summary>
        ///  Gets the method name used for source-quality reporting.
        /// </summary>
        public string MethodName { get; } = methodName;

        /// <summary>
        ///  Gets the saturating count of sampled frames that resolved to source.
        /// </summary>
        public int MappedFrames { get; private set; }

        /// <summary>
        ///  Gets the saturating count of sampled frames that did not resolve to source.
        /// </summary>
        public int UnmappedFrames { get; private set; }

        /// <summary>
        ///  Adds sampled frames to the aggregated source-resolution counts.
        /// </summary>
        /// <param name="sampledFrames">The number of sampled frames represented.</param>
        /// <param name="mappedFrames">The subset that resolved to source.</param>
        public void Observe(int sampledFrames, int mappedFrames)
        {
            MappedFrames = SaturatingAdd(MappedFrames, mappedFrames);
            UnmappedFrames = SaturatingAdd(UnmappedFrames, sampledFrames - mappedFrames);
        }

        private static int SaturatingAdd(int left, int right) =>
            left > int.MaxValue - right ? int.MaxValue : left + right;
    }
}