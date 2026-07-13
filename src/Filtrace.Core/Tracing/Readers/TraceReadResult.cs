// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

/// <summary>
///  The normalized output of a trace reader: weighted samples plus the
///  format-specific quality signals the loader folds into a <see cref="TraceInfo"/>.
/// </summary>
/// <param name="Samples">The weighted samples, each ordered outermost-first.</param>
/// <param name="SymbolResolutionRate">Fraction in <c>[0, 1]</c> of frames that resolved to a method name.</param>
/// <param name="Warnings">Format-specific quality warnings.</param>
/// <param name="RecordSemantics">What each normalized record represents.</param>
/// <param name="EtlxCacheState">How the ETLX cache request was satisfied, or <see langword="null"/> when no ETLX is used.</param>
/// <param name="AnalysisEventCounts">Capture-wide source-record counts keyed by analysis selector.</param>
/// <param name="SourceResolution">Sampled managed source/PDB quality, or <see langword="null"/> when unavailable.</param>
internal sealed record TraceReadResult(
    IReadOnlyList<SampleStack> Samples,
    double SymbolResolutionRate,
    IReadOnlyList<string> Warnings,
    StackRecordSemantics RecordSemantics,
    EtlxCacheState? EtlxCacheState = null,
    IReadOnlyDictionary<string, int>? AnalysisEventCounts = null,
    SourceResolutionInfo? SourceResolution = null);
