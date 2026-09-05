// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  One baseline/current capture case pair.
/// </summary>
/// <param name="Before">Baseline case.</param>
/// <param name="After">Current case.</param>
public sealed record CaptureManifestCasePair(
    CaptureManifestCase Before,
    CaptureManifestCase After);
