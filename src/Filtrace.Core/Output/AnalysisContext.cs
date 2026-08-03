// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;
using Filtrace.Tracing;

namespace Filtrace.Output;

/// <summary>
///  What an analysis actually ran: its operation, metric semantics, and effective
///  query scope.
/// </summary>
/// <param name="Operation">The surface-neutral operation name.</param>
public sealed record AnalysisContext(string Operation)
{
    /// <summary>The metric selector, or <see langword="null"/> when not applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Metric { get; init; }

    /// <summary>The measure selector, or <see langword="null"/> when not applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Measure { get; init; }

    /// <summary>The unit of metric weights, or <see langword="null"/> when not applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    /// <summary>The effective scope, or <see langword="null"/> when unscoped.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisScopeContext? Scope { get; init; }

    /// <summary>Builds context for an operation over one loaded stack source.</summary>
    /// <param name="operation">The surface-neutral operation name.</param>
    /// <param name="trace">The loaded trace whose metric and resolved scope actually ran.</param>
    /// <param name="measure">The measure selector, when the operation has one.</param>
    /// <param name="root">The applied root-frame selector, or empty for none.</param>
    /// <returns>The populated context.</returns>
    public static AnalysisContext ForTrace(
        string operation,
        LoadedTrace trace,
        string? measure = null,
        string root = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(trace);

        return new AnalysisContext(operation)
        {
            Metric = MetricSelector(trace.Aggregator.Metric),
            Measure = measure,
            Unit = trace.Aggregator.Metric.Unit,
            Scope = AnalysisScopeContext.Create(
                root,
                trace.Info.AppliedProcessScope,
                trace.Info.AppliedActivityName,
                trace.Info.AppliedTimeWindow)
        };
    }

    /// <summary>Builds context from known metric semantics without one effective trace scope.</summary>
    /// <param name="operation">The surface-neutral operation name.</param>
    /// <param name="metric">The metric whose weights the operation reports.</param>
    /// <param name="measure">The measure selector, when the operation has one.</param>
    /// <param name="root">The applied root-frame selector, or empty for none.</param>
    /// <returns>The populated context.</returns>
    public static AnalysisContext ForMetric(
        string operation,
        MetricInfo metric,
        string? measure = null,
        string root = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(metric);

        return new AnalysisContext(operation)
        {
            Metric = MetricSelector(metric),
            Measure = measure,
            Unit = metric.Unit,
            Scope = AnalysisScopeContext.Create(root, null, null, null)
        };
    }

    private static string MetricSelector(MetricInfo metric) => metric.Name switch
    {
        "CPU" => "cpu",
        "ThreadTime" => "threadtime",
        "Allocations" => "alloc",
        "Exceptions" => "exceptions",
        "Contention" => "contention",
        "Wait" => "wait",
        "Activity" => "activity",
        _ => metric.Name.ToLowerInvariant()
    };
}

/// <summary>The effective frame, process, activity, and time scope of one query.</summary>
public sealed record AnalysisScopeContext
{
    /// <summary>The maximum ids serialized for each process-id category.</summary>
    public const int MaxReportedProcessIds = 32;

    /// <summary>The root-frame selector, or <see langword="null"/> when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; init; }

    /// <summary>The process selector mode: all, automatic, name, or ids.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessMode { get; init; }

    /// <summary>The applied process-name selector, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Process { get; init; }

    /// <summary>The exact process ids requested, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? RequestedProcessIds { get; init; }

    /// <summary>The process ids the selector matched, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? RootProcessIds { get; init; }

    /// <summary>The additional descendant ids included, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? DescendantProcessIds { get; init; }

    /// <summary>The total requested process-id count, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestedProcessIdCount { get; init; }

    /// <summary>The total matched-root count, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RootProcessIdCount { get; init; }

    /// <summary>The total included-descendant count, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DescendantProcessIdCount { get; init; }

    /// <summary>Whether any process-id list was shortened for output.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProcessIdsTruncated { get; init; }

    /// <summary>Whether descendants were requested, or <see langword="null"/> without process scope.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeChildren { get; init; }

    /// <summary>The activity selector, or <see langword="null"/> when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Activity { get; init; }

    /// <summary>The time-window start in milliseconds, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FromMs { get; init; }

    /// <summary>The time-window end in milliseconds, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ToMs { get; init; }

    internal static AnalysisScopeContext? Create(
        string root,
        AppliedProcessScope? processScope,
        string? activityName,
        TimeWindow? window)
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
            ProcessMode = hasProcessSelector ? processScope?.Mode : null,
            Process = processScope?.Process,
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
