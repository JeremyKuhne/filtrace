// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  How a trace read should be scoped to processes: the agent-facing intent the
///  loader resolves into a concrete <see cref="ProcessScope"/> (or none).
/// </summary>
/// <remarks>
///  <para>
///   A machine-wide capture holds every process on the box, and an unscoped
///   ranking is the most common way an agent burns its token budget on irrelevant
///   processes. So scenario scope is the default: when neither a selector nor the
///   all-processes opt-out is given, the loader scopes a multi-process capture to
///   the busiest process and its tree automatically. The two explicit modes are an
///   override (<see cref="ForProcess"/> or <see cref="ForProcessIds"/>) and an
///   opt-out (<see cref="AllProcesses"/>).
///  </para>
///  <para>
///   Scoping only applies to a multi-process capture (an ETW <c>.etl</c>); the
///   single-process EventPipe and speedscope formats carry one process, so every
///   mode is a no-op there.
///  </para>
/// </remarks>
public sealed class ScopeRequest
{
    private ScopeRequest(
        bool includeAll,
        ProcessSelector? selector,
        bool includeChildren,
        string? activityName,
        TimeWindow? window)
    {
        IncludeAll = includeAll;
        Selector = selector;
        IncludeChildren = includeChildren;
        ActivityName = activityName;
        Window = window;
    }

    /// <summary>
    ///  The default: let the loader scope a multi-process capture to the busiest
    ///  process tree automatically.
    /// </summary>
    public static ScopeRequest Auto
    {
        get;
    } = new(includeAll: false, selector: null, includeChildren: true, activityName: null, window: null);

    /// <summary>
    ///  The automatic busiest-process scope, choosing whether to follow the chosen
    ///  process's descendants.
    /// </summary>
    /// <param name="includeChildren">Whether to also include every descendant of the chosen process.</param>
    /// <returns>The scope request; <see cref="Auto"/> when children are included.</returns>
    public static ScopeRequest AutoScope(bool includeChildren) =>
        includeChildren ? Auto : new(includeAll: false, selector: null, includeChildren: false, activityName: null, window: null);

    /// <summary>
    ///  Read every process - the opt-out from automatic scenario scoping.
    /// </summary>
    public static ScopeRequest AllProcesses
    {
        get;
    } = new(includeAll: true, selector: null, includeChildren: true, activityName: null, window: null);

    /// <summary>
    ///  Scope to the process(es) whose name contains <paramref name="processName"/>,
    ///  optionally including their descendants.
    /// </summary>
    /// <param name="processName">A case-insensitive process-name substring.</param>
    /// <param name="includeChildren">
    ///  Whether to also include every descendant of a matched process. Defaults to
    ///  <see langword="true"/>, matching the capture shapes (a host that launches the
    ///  measured work in a child).
    /// </param>
    /// <returns>The scope request.</returns>
    /// <exception cref="ArgumentException">
    ///  <paramref name="processName"/> is <see langword="null"/>, empty, contains
    ///  control characters, or is longer than
    ///  <see cref="ProcessNameSelector.MaxNameSubstringLength"/> characters.
    /// </exception>
    public static ScopeRequest ForProcess(string processName, bool includeChildren = true) =>
        new(includeAll: false, selector: new ProcessNameSelector(processName), includeChildren: includeChildren, activityName: null, window: null);

    /// <summary>
    ///  Scope to exactly the processes with <paramref name="processIds"/>, optionally
    ///  including their descendants.
    /// </summary>
    /// <param name="processIds">The exact process ids to scope to.</param>
    /// <param name="includeChildren">
    ///  Whether to also include every descendant of a matched process. Defaults to
    ///  <see langword="true"/>, matching <see cref="ForProcess"/>; pass
    ///  <see langword="false"/> for a parent-only read.
    /// </param>
    /// <returns>The scope request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    ///  <paramref name="processIds"/> is empty or contains a non-positive id.
    /// </exception>
    /// <remarks>
    ///  <para>
    ///   Unlike a name substring, an exact id set cannot silently pick up an unrelated
    ///   process that happens to share a common host name, so it is the selector a
    ///   capture manifest records and an automated run replays.
    ///  </para>
    /// </remarks>
    public static ScopeRequest ForProcessIds(IEnumerable<int> processIds, bool includeChildren = true) =>
        new(includeAll: false, selector: new ProcessIdSelector(processIds), includeChildren: includeChildren, activityName: null, window: null);

    /// <summary>
    ///  Returns a copy of this request additionally scoped to the start-stop activity
    ///  whose task name matches <paramref name="activityName"/> (case-insensitive): only
    ///  the samples taken while a thread was inside that activity (or one nested under
    ///  it) are kept. An empty or <see langword="null"/> name clears the activity scope.
    /// </summary>
    /// <param name="activityName">The activity task name to scope to, or <see langword="null"/> for none.</param>
    /// <returns>A copy of this request with the activity scope applied.</returns>
    public ScopeRequest WithActivity(string? activityName) =>
        new(IncludeAll, Selector, IncludeChildren, string.IsNullOrEmpty(activityName) ? null : activityName, Window);

    /// <summary>
    ///  Returns a copy of this request additionally scoped to the time window spanning
    ///  <paramref name="startMSec"/> to <paramref name="endMSec"/> (both inclusive,
    ///  either open): only the samples whose anchor time falls inside the window are
    ///  kept. Passing <see langword="null"/> for both bounds clears the time scope.
    /// </summary>
    /// <param name="startMSec">
    ///  The window start in milliseconds relative to the trace start, or <see langword="null"/> for the trace start.
    /// </param>
    /// <param name="endMSec">
    ///  The window end in milliseconds relative to the trace start, or <see langword="null"/> for the trace end.
    /// </param>
    /// <returns>A copy of this request with the time-window scope applied.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A bound is negative or not a number.</exception>
    /// <exception cref="ArgumentException">
    ///  <paramref name="startMSec"/> is greater than <paramref name="endMSec"/>.
    /// </exception>
    public ScopeRequest WithTimeWindow(double? startMSec, double? endMSec) =>
        new(
            IncludeAll,
            Selector,
            IncludeChildren,
            ActivityName,
            startMSec is null && endMSec is null ? null : new TimeWindow(startMSec, endMSec));

    /// <summary>
    ///  Whether every process is read (the all-processes opt-out).
    /// </summary>
    public bool IncludeAll { get; }

    /// <summary>
    ///  The explicit selector to scope to, or <see langword="null"/> when none was
    ///  given (automatic or all-processes).
    /// </summary>
    public ProcessSelector? Selector { get; }

    /// <summary>
    ///  Whether a matched process's descendants are included in the scope. Applies to
    ///  an explicit <see cref="ForProcess"/> or <see cref="ForProcessIds"/> request and
    ///  to the automatic scope.
    /// </summary>
    public bool IncludeChildren { get; }

    /// <summary>
    ///  The start-stop activity task name to scope samples to (case-insensitive), or
    ///  <see langword="null"/> when no activity scope was requested. When set, only the
    ///  samples taken while a thread was inside that activity (or one nested under it)
    ///  are kept. Honored by the CPU reader; other metrics ignore it.
    /// </summary>
    public string? ActivityName { get; }

    /// <summary>
    ///  The time window to scope samples to, or <see langword="null"/> when no time
    ///  scope was requested. When set, only the samples whose anchor time falls inside
    ///  the window are kept. Unlike the process and activity scopes, this applies to
    ///  every metric, since every sampled event carries a timestamp.
    /// </summary>
    public TimeWindow? Window { get; }
}
