// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>Validated inputs for one ranking query across a capture manifest.</summary>
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