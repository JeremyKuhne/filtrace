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
internal sealed record TraceReadResult(
    IReadOnlyList<SampleStack> Samples,
    double SymbolResolutionRate,
    IReadOnlyList<string> Warnings);
