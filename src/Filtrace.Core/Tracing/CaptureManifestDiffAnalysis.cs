// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Manifest diff payload plus cross-manifest pairing warnings.
/// </summary>
/// <param name="Result">Case-keyed ranking diff result.</param>
/// <param name="Warnings">Bounded pairing and output-cap warnings.</param>
public sealed record CaptureManifestDiffAnalysis(
    RankingDiffResult Result,
    IReadOnlyList<string> Warnings);
