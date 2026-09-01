// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  Validated inputs for one ranking query across a capture manifest.
/// </summary>
/// <param name="ManifestPath">The capture manifest to analyze.</param>
/// <param name="Metric">The provider metric to rank for each case.</param>
/// <param name="Root">The optional frame substring that scopes each ranking to a subtree.</param>
/// <param name="Fold">The frame patterns folded out of each call stack.</param>
/// <param name="Measure">Whether each ranking reports self or inclusive weight.</param>
/// <param name="Format">The format written to standard output.</param>
/// <param name="Symbols">The optional symbol directory that overrides each case's recorded directory.</param>
/// <param name="Strict">Whether inadequate symbol resolution produces a quality-gate exit.</param>
/// <param name="Scope">The process scope override, or <see langword="null"/> to use each case's recorded scope.</param>
internal sealed record BatchRequest(
    string ManifestPath,
    TraceMetric Metric,
    string Root,
    IReadOnlyList<string> Fold,
    Measure Measure,
    OutputFormat Format,
    string? Symbols,
    bool Strict,
    ScopeRequest? Scope);
