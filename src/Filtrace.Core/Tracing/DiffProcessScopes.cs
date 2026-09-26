// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics.CodeAnalysis;

namespace Filtrace.Tracing;

/// <summary>
///  The process scopes for the baseline and current sides of a diff.
/// </summary>
public sealed class DiffProcessScopes
{
    private DiffProcessScopes(ScopeRequest? before, ScopeRequest? after, bool hasPerArmIds)
    {
        Before = before;
        After = after;
        HasPerArmIds = hasPerArmIds;
    }

    /// <summary>
    ///  The baseline scope.
    /// </summary>
    public ScopeRequest? Before { get; }

    /// <summary>
    ///  The current scope.
    /// </summary>
    public ScopeRequest? After { get; }

    /// <summary>
    ///  Whether each trace has its own exact process-id selector.
    /// </summary>
    public bool HasPerArmIds { get; }

    /// <summary>
    ///  Applies one existing scope to both sides of a diff.
    /// </summary>
    /// <param name="scope">The shared process scope.</param>
    /// <returns>Two identical scopes.</returns>
    public static DiffProcessScopes Shared(ScopeRequest? scope) => new(scope, scope, hasPerArmIds: false);

    /// <summary>
    ///  Resolves a shared scope or a pair of exact process-id scopes.
    /// </summary>
    /// <param name="shared">The scope requested through the existing process, pid, or all-processes selector.</param>
    /// <param name="beforePid">Exact baseline process ids, or <see langword="null"/> when omitted.</param>
    /// <param name="afterPid">Exact current process ids, or <see langword="null"/> when omitted.</param>
    /// <param name="includeChildren">Whether to include descendants in both scopes.</param>
    /// <param name="scopes">The resolved scopes, when valid.</param>
    /// <param name="error">The usage error, when invalid.</param>
    /// <returns>Whether the requested combination is valid.</returns>
    public static bool TryResolve(
        ScopeRequest? shared,
        int[]? beforePid,
        int[]? afterPid,
        bool includeChildren,
        out DiffProcessScopes scopes,
        [NotNullWhen(returnValue: false)] out string? error)
    {
        scopes = Shared(shared);
        if (beforePid is null && afterPid is null)
        {
            error = null;
            return true;
        }

        if (beforePid is null || afterPid is null)
        {
            error = "Specify both beforePid and afterPid to compare different process ids.";
            return false;
        }

        if (shared is { Selector: not null } or { IncludeAll: true })
        {
            error = "beforePid and afterPid cannot be combined with process, pid, or allProcesses.";
            return false;
        }

        if (beforePid.Length == 0 || afterPid.Length == 0)
        {
            error = "beforePid and afterPid must each contain at least one process id.";
            return false;
        }

        foreach ((int[] ids, string name) in new[] { (beforePid, "beforePid"), (afterPid, "afterPid") })
        {
            foreach (int id in ids)
            {
                if (id <= 0)
                {
                    error = $"{name} {id} is not a valid process id; ids must be positive.";
                    return false;
                }
            }
        }

        scopes = new(
            ScopeRequest.ForProcessIds(beforePid, includeChildren),
            ScopeRequest.ForProcessIds(afterPid, includeChildren),
            hasPerArmIds: true);

        error = null;
        return true;
    }

    /// <summary>
    ///  Checks that a loaded trace honored its per-arm exact process-id selector.
    /// </summary>
    /// <param name="info">The metadata of the loaded trace.</param>
    /// <param name="before">Whether this is the baseline rather than the current trace.</param>
    /// <returns>An input error, or <see langword="null"/> when the scope was applied.</returns>
    public string? TraceError(TraceInfo info, bool before)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!HasPerArmIds)
        {
            return null;
        }

        string side = before ? "Baseline" : "Current";
        if (info.Format != TraceFormat.Etl)
        {
            return $"{side} per-arm PID selection requires an .etl trace; {info.Format} has no process-scope axis.";
        }

        if (info.AppliedProcessScope is not { Mode: "ids" } applied)
        {
            return $"{side} per-arm PID selection was not applied to this .etl trace.";
        }

        ScopeRequest scope = (before ? Before : After)!;
        ProcessIdSelector selector = (ProcessIdSelector)scope.Selector!;
        int[] missing = [.. selector.ProcessIds.Except(applied.RootProcessIds)];
        return missing.Length == 0
            ? null
            : $"{side} process {(missing.Length == 1 ? "id" : "ids")} {string.Join(", ", missing)} "
                + $"{(missing.Length == 1 ? "was" : "were")} not found in this trace.";
    }
}
