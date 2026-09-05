// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  The validated inputs to a lifecycle run: which trace to read, which processes are
///  the invocation roots, which loader milestones to time, and how to render it.
/// </summary>
/// <remarks>
///  <para>
///   This is the boundary between command-line parsing and the execution in
///   <see cref="LifecycleExecutor"/>; keeping it a plain record lets the executor be
///   exercised directly in tests without driving the parser.
///  </para>
/// </remarks>
/// <param name="Path">The trace file path.</param>
/// <param name="Scope">Which processes are the invocation roots.</param>
/// <param name="Images">Module-name substrings to time as loader milestones.</param>
/// <param name="Top">Maximum number of per-invocation rows to show.</param>
/// <param name="Format">The render format.</param>
internal sealed record LifecycleRequest(
    string Path,
    ScopeRequest Scope,
    IReadOnlyList<string> Images,
    int Top,
    OutputFormat Format);
