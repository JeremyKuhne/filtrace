// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A process inventory plus the trace and CPU-weight provenance needed to render it.
/// </summary>
/// <param name="Path">The absolute trace path.</param>
/// <param name="Format">The trace format.</param>
/// <param name="Result">The process rows and complete sample/weight totals.</param>
/// <param name="Metric">Whether weights are milliseconds or raw samples.</param>
/// <param name="CpuSampling">The CPU interval provenance.</param>
/// <param name="Warnings">Lost-event and CPU-weight-quality warnings.</param>
/// <param name="EtlxCacheState">How the ETLX cache was obtained, or <see langword="null"/> when not applicable.</param>
public sealed record ProcessInventorySnapshot(
    string Path,
    TraceFormat Format,
    ProcessListResult Result,
    MetricInfo Metric,
    CpuSampleProvenance? CpuSampling,
    IReadOnlyList<string> Warnings,
    EtlxCacheState? EtlxCacheState);
