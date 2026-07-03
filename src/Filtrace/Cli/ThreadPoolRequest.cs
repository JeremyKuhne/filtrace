// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The validated inputs to a thread-pool run: which trace to read and how to
///  render it.
/// </summary>
/// <remarks>
///  <para>
///   This is the boundary between command-line parsing and the execution in
///   <see cref="ThreadPoolExecutor"/>; keeping it a plain record lets the executor be
///   exercised directly in tests without driving the parser. The report is a small,
///   fixed-size summary, so unlike the ranking verbs it needs no row cap.
///  </para>
/// </remarks>
/// <param name="Path">The trace file path.</param>
/// <param name="Format">The render format.</param>
internal sealed record ThreadPoolRequest(
    string Path,
    OutputFormat Format);
