// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  The validated inputs to an export run: which trace to load, the flame-graph
///  format to emit, where to write it, and the profile name shown in the viewer.
/// </summary>
/// <param name="Path">The trace file path.</param>
/// <param name="Format">The flame-graph format to write.</param>
/// <param name="Output">The output file path, or <see langword="null"/> to write to standard output.</param>
/// <param name="Symbols">Optional build-output directory whose embedded PDBs resolve managed frames.</param>
/// <param name="Name">The profile name shown in the viewer.</param>
/// <param name="Scope">
///  The process scope: an explicit process tree, every process, or the automatic
///  busiest-process default for a machine-wide <c>.etl</c>.
/// </param>
/// <param name="Root">
///  Substring scoping the export to the subtree under a frame, or empty for the
///  whole sample source.
/// </param>
/// <param name="SymbolOptions">
///  Native-symbol resolution. <see langword="null"/> or <see cref="SymbolOptions.None"/>
///  resolves managed frames from the rundown only (offline, the default); <see
///  cref="SymbolOptions.WithCache"/> additionally fetches native runtime PDBs from the
///  public symbol server.
/// </param>
internal sealed record ExportRequest(
    string Path,
    ExportFormat Format,
    string? Output,
    string? Symbols,
    string Name,
    ScopeRequest Scope,
    string Root = "",
    SymbolOptions? SymbolOptions = null);
