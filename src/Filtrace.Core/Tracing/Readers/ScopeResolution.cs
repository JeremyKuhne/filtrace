// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  What a <see cref="ScopeRequest"/> resolved to against one trace: exact process
///  instances, PID summaries, how to name that scope, and selector advisories.
/// </summary>
internal sealed class ScopeResolution
{
    /// <summary>
    ///  The resolution for a read that keeps every process.
    /// </summary>
    public static ScopeResolution Unscoped { get; } =
        new(null, null, null, null, [], AppliedProcessScope.AllProcesses, processNameBounded: false);

    public ScopeResolution(
        HashSet<int>? processIds,
        HashSet<ProcessIndex>? processInstanceIndexes,
        string? label,
        string? phrase,
        IReadOnlyList<string> warnings,
        AppliedProcessScope appliedScope,
        bool processNameBounded)
    {
        ProcessIds = processIds;
        ProcessInstanceIndexes = processInstanceIndexes;
        Label = label;
        Phrase = phrase;
        Warnings = warnings;
        AppliedScope = appliedScope;
        ProcessNameBounded = processNameBounded;
    }

    /// <summary>
    ///  The process-id summary, or <see langword="null"/> when every process is read.
    /// </summary>
    public HashSet<int>? ProcessIds { get; }

    /// <summary>
    ///  The exact process instances to keep, or <see langword="null"/> when every
    ///  process is read.
    /// </summary>
    public HashSet<ProcessIndex>? ProcessInstanceIndexes { get; }

    /// <summary>
    ///  Whether the process instance associated with an event is in scope.
    /// </summary>
    public bool Includes(TraceEvent data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (ProcessInstanceIndexes is null)
        {
            return true;
        }

        TraceProcess? process = data.ProcessID <= 0 ? null : TraceLogExtensions.Process(data);
        return process is not null && ProcessInstanceIndexes.Contains(process.ProcessIndex);
    }

    /// <summary>
    ///  Whether one resolved ETLX process instance is in scope.
    /// </summary>
    public bool Includes(TraceProcess? process) =>
        ProcessInstanceIndexes is null
        || (process is not null && ProcessInstanceIndexes.Contains(process.ProcessIndex));

    internal bool Includes(ProcessIndex processIndex) =>
        ProcessInstanceIndexes is null || ProcessInstanceIndexes.Contains(processIndex);

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

    /// <summary>
    ///  The machine-readable process scope the read applied.
    /// </summary>
    public AppliedProcessScope AppliedScope { get; }

    /// <summary>
    ///  Whether the reported trace-derived process name was bounded or escaped.
    /// </summary>
    public bool ProcessNameBounded { get; }
}
