// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information


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
        new(processIds: null, processInstanceIndexes: null, label: null, phrase: null, [], AppliedProcessScope.AllProcesses, processNameBounded: false);

    /// <summary>
    ///  Captures exact membership, output labels, warnings, and replay metadata for a resolved process scope.
    /// </summary>
    /// <param name="processIds">
    ///  The included OS process ids, or <see langword="null"/> when every process is included.
    /// </param>
    /// <param name="processInstanceIndexes">
    ///  The included trace-local process indexes, or <see langword="null"/> when unscoped.
    /// </param>
    /// <param name="label">The bounded short identity shown in structured output.</param>
    /// <param name="phrase">The bounded scope phrase used in diagnostics.</param>
    /// <param name="warnings">Selector ambiguity and missing-id advisories.</param>
    /// <param name="appliedScope">Machine-readable roots, descendants, mode, and replayability.</param>
    /// <param name="processNameBounded">Whether trace-derived process text was escaped or shortened.</param>
    public ScopeResolution(
        HashSet<int>? processIds,
        HashSet<EtlxProcessIndex>? processInstanceIndexes,
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
    public HashSet<EtlxProcessIndex>? ProcessInstanceIndexes { get; }

    /// <summary>
    ///  Whether the process instance associated with an event is in scope.
    /// </summary>
    /// <param name="data">The trace event whose process instance is tested.</param>
    /// <returns>
    ///  <see langword="true"/> when unscoped or when the event belongs to an included process instance.
    /// </returns>
    public bool Includes(TraceEvent data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (ProcessInstanceIndexes is null)
        {
            return true;
        }

        EtlxTraceProcess? process = data.ProcessID <= 0 ? null : TraceLogExtensions.Process(data);
        return process is not null && ProcessInstanceIndexes.Contains(process.ProcessIndex);
    }

    /// <summary>
    ///  Whether one resolved ETLX process instance is in scope.
    /// </summary>
    /// <param name="process">The process instance to test, or <see langword="null"/> when none can be resolved.</param>
    /// <returns><see langword="true"/> when unscoped or when the instance was included.</returns>
    public bool Includes(EtlxTraceProcess? process) =>
        ProcessInstanceIndexes is null
            || (process is not null && ProcessInstanceIndexes.Contains(process.ProcessIndex));

    /// <summary>
    ///  Tests a trace-local process index without resolving its process object.
    /// </summary>
    /// <param name="processIndex">The TraceEvent process index.</param>
    /// <returns><see langword="true"/> when unscoped or when the index was included.</returns>
    internal bool Includes(EtlxProcessIndex processIndex) =>
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
