// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  The validated inputs to a trace-info run: which trace to load, how to resolve
///  symbols, how to scope a multi-process capture, and how to render the result.
/// </summary>
/// <param name="Path">The trace file path.</param>
/// <param name="Symbols">
///  Optional build-output directory whose embedded PDBs resolve managed frames to
///  source lines. <see langword="null"/> when not given.
/// </param>
/// <param name="Format">The render format.</param>
/// <param name="Scope">
///  The process scope for a multi-process <c>.etl</c> - an explicit name, or the
///  automatic busiest-process default when <see langword="null"/>.
/// </param>
internal sealed record InfoRequest(
    string Path,
    string? Symbols,
    OutputFormat Format,
    ScopeRequest? Scope = null);
