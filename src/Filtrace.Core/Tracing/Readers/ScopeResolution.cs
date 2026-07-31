// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

/// <summary>
///  What a <see cref="ScopeRequest"/> resolved to against one trace: the process ids
///  to keep, how to name that scope, and anything the caller should be told about how
///  the selector matched.
/// </summary>
internal sealed class ScopeResolution
{
    /// <summary>
    ///  The resolution for a read that keeps every process.
    /// </summary>
    public static ScopeResolution Unscoped { get; } = new(null, null, null, []);

    public ScopeResolution(
        HashSet<int>? processIds,
        string? label,
        string? phrase,
        IReadOnlyList<string> warnings)
    {
        ProcessIds = processIds;
        Label = label;
        Phrase = phrase;
        Warnings = warnings;
    }

    /// <summary>
    ///  The process ids to keep, or <see langword="null"/> when every process is read.
    /// </summary>
    public HashSet<int>? ProcessIds { get; }

    /// <summary>
    ///  A short identity for the scope - the matched process name, or <c>pid 1234</c> /
    ///  <c>pids 1234, 5678</c> - or <see langword="null"/> when no scope applied or an
    ///  automatic scope narrowed nothing and so is not worth reporting.
    /// </summary>
    public string? Label { get; }

    /// <summary>
    ///  The scope as a prose phrase for warning text ("the 'MyApp' process tree"), or
    ///  <see langword="null"/> whenever <see cref="Label"/> is.
    /// </summary>
    public string? Phrase { get; }

    /// <summary>
    ///  Advisories about how the selector matched, such as a name that resolved to more
    ///  than one unrelated root. Never <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }
}
