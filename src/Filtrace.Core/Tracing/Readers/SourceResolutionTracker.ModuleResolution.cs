// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information


namespace Filtrace.Tracing.Readers;

internal sealed partial class SourceResolutionTracker
{
    /// <summary>
    ///  Accumulates source-resolution evidence for one managed module.
    /// </summary>
    /// <param name="name">The normalized module name used for reporting and consolidation.</param>
    /// <param name="module">TraceEvent module metadata, or <see langword="null"/> when unavailable.</param>
    private sealed class ModuleResolution(string name, TraceModuleFile? module)
    {
        /// <summary>
        ///  Gets the normalized module name.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        ///  Gets the TraceEvent module metadata used for PDB identity lookup.
        /// </summary>
        public TraceModuleFile? Module { get; } = module;

        /// <summary>
        ///  Gets the saturating count of sampled frames attributed to the module.
        /// </summary>
        public int SampledFrames => SaturatingAdd(MappedFrames, UnmappedFrames);

        /// <summary>
        ///  Gets or sets the saturating count of frames that resolved to source.
        /// </summary>
        public int MappedFrames { get; set; }

        /// <summary>
        ///  Gets or sets the saturating count of frames that did not resolve to source.
        /// </summary>
        public int UnmappedFrames { get; set; }

        /// <summary>
        ///  Gets the mapped count scaled to preserve both categories when their combined count exceeds <see cref="int.MaxValue"/>.
        /// </summary>
        public int ReportedMappedFrames => GetReportedMappedFrames(MappedFrames, UnmappedFrames);

        /// <summary>
        ///  Gets or sets the strongest local PDB identity outcome established for the module.
        /// </summary>
        public PdbMatchStatus PdbStatus { get; set; }
    }
}
