// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal abstract partial class TraceLogReader
{
    /// <summary>
    ///  Identifies one rendered frame by its trace-local module and method indexes.
    /// </summary>
    /// <param name="ModuleFileIndex">The trace-local module-file index.</param>
    /// <param name="MethodIndex">The trace-local method index.</param>
    private readonly record struct FrameIdentity(int ModuleFileIndex, int MethodIndex)
    {
        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(ModuleFileIndex, MethodIndex);
    }
}