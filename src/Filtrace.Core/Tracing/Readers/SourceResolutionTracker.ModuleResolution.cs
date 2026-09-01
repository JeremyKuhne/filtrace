// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing.Etlx;

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
        ///  Gets or sets the saturating count of sampled frames attributed to the module.
        /// </summary>
        public int SampledFrames { get; set; }

        /// <summary>
        ///  Gets or sets the saturating subset of frames that resolved to source.
        /// </summary>
        public int MappedFrames { get; set; }

        /// <summary>
        ///  Gets or sets the strongest local PDB identity outcome established for the module.
        /// </summary>
        public PdbMatchStatus PdbStatus { get; set; }
    }
}
