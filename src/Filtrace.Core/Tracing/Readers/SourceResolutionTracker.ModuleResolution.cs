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
    private sealed class ModuleResolution(string name, TraceModuleFile? module)
    {
        public string Name { get; } = name;
        public TraceModuleFile? Module { get; } = module;
        public int SampledFrames { get; set; }
        public int MappedFrames { get; set; }
        public PdbMatchStatus PdbStatus { get; set; }
    }
}