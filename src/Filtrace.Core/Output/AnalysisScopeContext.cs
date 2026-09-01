// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Output;

/// <summary>
///  The effective frame, process, activity, and time scope of one query.
/// </summary>
public sealed record AnalysisScopeContext
{
    /// <summary>
    ///  The serialized root-filter semantic.
    /// </summary>
    public const string StackAncestryRootKind = "stackAncestry";

    /// <summary>
    ///  The maximum ids serialized for each process-id category.
    /// </summary>
    public const int MaxReportedProcessIds = 32;

    /// <summary>
    ///  The root-frame selector, or <see langword="null"/> when none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; init; }

    /// <summary>
    ///  The root-filter semantic, or <see langword="null"/> when no root was applied.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootKind { get; init; }

    /// <summary>
    ///  Pre-root and retained coverage, or <see langword="null"/> when unavailable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RootScopeCoverage? RootCoverage { get; init; }

    /// <summary>
    ///  The process selector mode: all, automatic, name, or ids.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessMode { get; init; }

    /// <summary>
    ///  A bounded display label for name or automatic process scope, or
    ///  <see langword="null"/>. This display value is not a reusable selector.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Process { get; init; }

    /// <summary>
    ///  The exact process ids requested, or <see langword="null"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? RequestedProcessIds { get; init; }

    /// <summary>
    ///  The process ids the selector matched, or <see langword="null"/>. These describe
    ///  the effective scope but may not be replayable when the trace reused an OS id.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? RootProcessIds { get; init; }

    /// <summary>
    ///  The additional descendant ids included, or <see langword="null"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? DescendantProcessIds { get; init; }

    /// <summary>
    ///  The total requested process-id count, or <see langword="null"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestedProcessIdCount { get; init; }

    /// <summary>
    ///  The total matched-root count, or <see langword="null"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RootProcessIdCount { get; init; }

    /// <summary>
    ///  The total included-descendant count, or <see langword="null"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DescendantProcessIdCount { get; init; }

    /// <summary>
    ///  Whether any process-id list was shortened for output.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProcessIdsTruncated { get; init; }

    /// <summary>
    ///  Whether descendants were requested, or <see langword="null"/> without process scope.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeChildren { get; init; }

    /// <summary>
    ///  The activity selector, or <see langword="null"/> when none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Activity { get; init; }

    /// <summary>
    ///  The time-window start in milliseconds, or <see langword="null"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FromMs { get; init; }

    /// <summary>
    ///  The time-window end in milliseconds, or <see langword="null"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ToMs { get; init; }

    /// <summary>
    ///  Builds the serializable effective scope for a completed analysis, omitting the object when no scope applied.
    /// </summary>
    /// <param name="root">The stack-ancestry root selector, or an empty string when unscoped.</param>
    /// <param name="processScope">The resolved process scope, or <see langword="null"/> when unavailable.</param>
    /// <param name="activityName">The activity selector, or <see langword="null"/> when unscoped.</param>
    /// <param name="window">The requested time window, or <see langword="null"/> when unscoped.</param>
    /// <param name="rootCoverage">Coverage retained by the root filter, when measured.</param>
    /// <returns>The effective scope, or <see langword="null"/> when every scope axis is unbounded.</returns>
    internal static AnalysisScopeContext? Create(
        string root,
        AppliedProcessScope? processScope,
        string? activityName,
        TimeWindow? window,
        RootScopeCoverage? rootCoverage)
    {
        bool hasRoot = !string.IsNullOrEmpty(root);
        bool hasProcessSelector = processScope is { Mode: not "all" };
        bool hasRequestedIds = processScope is { Mode: "ids" };
        bool hasDescendants = hasProcessSelector && processScope!.IncludeChildren;
        bool hasActivity = !string.IsNullOrEmpty(activityName);
        bool hasWindow = window is TimeWindow appliedWindow && appliedWindow.IsBounded;
        if (!hasRoot && !hasProcessSelector && !hasActivity && !hasWindow)
        {
            return null;
        }

        return new AnalysisScopeContext
        {
            Root = hasRoot ? root : null,
            RootKind = hasRoot ? StackAncestryRootKind : null,
            RootCoverage = hasRoot ? rootCoverage : null,
            ProcessMode = hasProcessSelector ? processScope?.Mode : null,
            // An automatic scope names the busiest process from the trace itself, so this
            // is untrusted text on every surface that renders the context.
            Process = processScope?.Process is string scopeProcess
                ? TimelineProvider.BoundSnapshotName(scopeProcess, out _)
                : null,
            RequestedProcessIds = hasRequestedIds ? Bounded(processScope!.RequestedProcessIds) : null,
            RootProcessIds = hasProcessSelector ? Bounded(processScope!.RootProcessIds) : null,
            DescendantProcessIds = hasDescendants ? Bounded(processScope!.DescendantProcessIds) : null,
            RequestedProcessIdCount = hasRequestedIds ? processScope!.RequestedProcessIds.Count : null,
            RootProcessIdCount = hasProcessSelector ? processScope!.RootProcessIds.Count : null,
            DescendantProcessIdCount = hasDescendants ? processScope!.DescendantProcessIds.Count : null,
            ProcessIdsTruncated = IsTruncated(processScope?.RequestedProcessIds)
                || IsTruncated(processScope?.RootProcessIds)
                || IsTruncated(processScope?.DescendantProcessIds),
            IncludeChildren = hasProcessSelector ? processScope!.IncludeChildren : null,
            Activity = hasActivity ? activityName : null,
            FromMs = hasWindow ? window!.Value.StartMSec : null,
            ToMs = hasWindow ? window!.Value.EndMSec : null
        };
    }

    private static IReadOnlyList<int> Bounded(IReadOnlyList<int> values) => values switch
    {
        { Count: 0 } => [],
        { Count: <= MaxReportedProcessIds } => values,
        _ => [.. values.Take(MaxReportedProcessIds)]
    };

    private static bool IsTruncated(IReadOnlyList<int>? values) =>
        values is { Count: > MaxReportedProcessIds };
}
