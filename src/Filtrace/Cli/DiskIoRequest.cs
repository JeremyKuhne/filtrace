// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The validated inputs to a disk-I/O run: which trace to read, how many
///  per-file rows to show, and how to render it.
/// </summary>
/// <remarks>
///  <para>
///   This is the boundary between command-line parsing and the execution in
///   <see cref="DiskIoExecutor"/>; keeping it a plain record lets the executor be
///   exercised directly in tests without driving the parser.
///  </para>
/// </remarks>
/// <param name="Path">The trace file path.</param>
/// <param name="Top">Maximum number of per-file rows to show, ranked by disk service time.</param>
/// <param name="Format">The render format.</param>
internal sealed record DiskIoRequest(
    string Path,
    int Top,
    OutputFormat Format);
