// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  What a process-scope request resolved to against one trace: the selector mode,
///  the roots it matched, and the descendants the read included.
/// </summary>
/// <param name="Mode">
///  The selector mode: <c>all</c>, <c>automatic</c>, <c>name</c>, or <c>ids</c>.
/// </param>
/// <param name="Process">
///  The selected process-name substring for <c>name</c> and <c>automatic</c> modes,
///  or <see langword="null"/> for the other modes.
/// </param>
/// <param name="RequestedProcessIds">
///  The exact ids requested in <c>ids</c> mode, deduplicated and ascending; empty
///  for the other modes.
/// </param>
/// <param name="RootProcessIds">The process ids the selector matched.</param>
/// <param name="DescendantProcessIds">
///  The additional descendant ids included under those roots.
/// </param>
/// <param name="IncludeChildren">Whether descendants were requested.</param>
public sealed record AppliedProcessScope(
    string Mode,
    string? Process,
    IReadOnlyList<int> RequestedProcessIds,
    IReadOnlyList<int> RootProcessIds,
    IReadOnlyList<int> DescendantProcessIds,
    bool IncludeChildren)
{
    /// <summary>The all-processes opt-out from process scoping.</summary>
    public static AppliedProcessScope AllProcesses { get; } =
        new("all", null, [], [], [], true);
}
