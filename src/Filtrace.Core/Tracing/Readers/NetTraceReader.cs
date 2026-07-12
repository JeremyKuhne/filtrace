// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Reads a .NET EventPipe trace (<c>.nettrace</c>) - the net10
///  <c>[EventPipeProfiler]</c> output - into weighted samples.
/// </summary>
internal sealed class NetTraceReader : TraceLogReader
{
    /// <inheritdoc/>
    public override TraceFormat Format => TraceFormat.NetTrace;

    /// <inheritdoc/>
    public override bool CanRead(string path) =>
        path.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    protected override TraceLog OpenTraceLog(string path, out EtlxCacheState cacheState) =>
        TraceConverter.OpenTraceLog(path, out cacheState);
}
