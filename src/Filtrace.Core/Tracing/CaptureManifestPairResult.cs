// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Paired cases plus bounded pairing warnings.
/// </summary>
/// <param name="Pairs">Cases matched by exact benchmark and parameters.</param>
/// <param name="Warnings">Unresolved and unmatched case diagnostics.</param>
public sealed record CaptureManifestPairResult(
    IReadOnlyList<CaptureManifestCasePair> Pairs,
    IReadOnlyList<string> Warnings);
