// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>A bounded BenchmarkDotNet capture manifest.</summary>
/// <param name="Path">Canonical manifest path.</param>
/// <param name="Process">Optional process selector recorded by the capture.</param>
/// <param name="Cases">Captured benchmark cases in manifest order.</param>
public sealed record CaptureManifest(
    string Path,
    string? Process,
    IReadOnlyList<CaptureManifestCase> Cases);

/// <summary>One captured benchmark case from a capture manifest.</summary>
/// <param name="Id">Run-unique case identifier.</param>
/// <param name="Benchmark">Exact benchmark name, or <see langword="null"/> when unresolved.</param>
/// <param name="Parameters">Stable parameter display, empty for an unparameterized benchmark.</param>
/// <param name="BenchmarkDisplay">Human-readable BenchmarkDotNet display text.</param>
/// <param name="TracePath">Preferred raw trace path, or the speedscope path when no raw trace exists.</param>
/// <param name="SymbolsDirectory">Exact local symbol directory, when verified.</param>
/// <param name="OperationCount">Operations represented by the case, when supplied.</param>
/// <param name="OperationUnit">Operation unit, when supplied.</param>
public sealed record CaptureManifestCase(
    string Id,
    string? Benchmark,
    string Parameters,
    string BenchmarkDisplay,
    string TracePath,
    string? SymbolsDirectory,
    double? OperationCount,
    string? OperationUnit)
{
    /// <summary>
    ///  Stable benchmark-and-parameter key used for cross-manifest pairing, or
    ///  <see langword="null"/> when the capture could not resolve benchmark identity.
    /// </summary>
    public string? PairingKey => Benchmark is null ? null : $"{Benchmark}\0{Parameters}";

    /// <summary>Whether count and unit are both present and usable.</summary>
    public bool HasCompleteOperationMetadata =>
        OperationCount is > 0.0 && OperationUnit is not null;
}