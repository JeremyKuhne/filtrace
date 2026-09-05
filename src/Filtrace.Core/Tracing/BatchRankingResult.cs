// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  One ranking query summarized across every case in a capture manifest.
/// </summary>
/// <param name="ManifestPath">Canonical manifest path.</param>
/// <param name="Metric">Requested metric selector.</param>
/// <param name="Measure"><c>self</c> or <c>inclusive</c>.</param>
/// <param name="RootFrame">Optional root selector applied to every case.</param>
/// <param name="Cases">Case-keyed ranking summaries.</param>
public sealed record BatchRankingResult(
    string ManifestPath,
    string Metric,
    string Measure,
    string RootFrame,
    IReadOnlyList<BatchRankingCaseResult> Cases);
