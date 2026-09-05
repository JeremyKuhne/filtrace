// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Scopes a trace read to a workload process tree: the process(es) the
///  <see cref="Selector"/> matches plus, when <see cref="IncludeChildren"/> is set,
///  all of their descendants.
/// </summary>
/// <remarks>
///  <para>
///   This is how an analysis is confined to the work that matters without
///   physically rewriting the trace. A machine-wide ETW capture holds every
///   process on the box; scoping at read time keeps only the samples that belong
///   to the workload, losslessly, because the trace is fully symbol-resolved
///   before any sample is dropped.
///  </para>
///  <para>
///   Following children is the default because the common capture shapes need it.
///   BenchmarkDotNet runs each workload in a child process that the orchestrating
///   host launches, so scoping to the host without its children would miss the
///   measured code entirely. Profiling an application the same way - launch it
///   under a capture - puts the real work in the launched process and its
///   children. Set <see cref="IncludeChildren"/> to <see langword="false"/> to
///   confine the scope to the matched processes alone, which is what separates a
///   native parent's own CPU from a child runtime's.
///  </para>
/// </remarks>
/// <param name="Selector">How the tree roots are chosen: by name substring or by exact process id.</param>
/// <param name="IncludeChildren">
///  Whether to also include every descendant of a matched process. Defaults to
///  <see langword="true"/>.
/// </param>
public sealed record ProcessScope(ProcessSelector Selector, bool IncludeChildren = true)
{
    /// <summary>
    ///  How the tree roots are chosen: by name substring or by exact process id.
    /// </summary>
    public ProcessSelector Selector { get; } = Selector ?? throw new ArgumentNullException(nameof(Selector));
}
