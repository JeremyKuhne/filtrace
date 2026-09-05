// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Output;

/// <summary>
///  What an analysis actually ran: its operation, metric semantics, and effective
///  query scope.
/// </summary>
/// <param name="Operation">The surface-neutral operation name.</param>
public sealed record AnalysisContext(string Operation)
{
    /// <summary>
    ///  The metric selector, or <see langword="null"/> when not applicable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Metric { get; init; }

    /// <summary>
    ///  The measure selector, or <see langword="null"/> when not applicable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Measure { get; init; }

    /// <summary>
    ///  The unit of metric weights, or <see langword="null"/> when not applicable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    /// <summary>
    ///  The effective scope, or <see langword="null"/> when unscoped.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisScopeContext? Scope { get; init; }

    /// <summary>
    ///  Builds context for an operation over one loaded stack source.
    /// </summary>
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
                trace.Info.AppliedTimeWindow,
                string.IsNullOrEmpty(root)
                    ? null
                    : trace.Aggregator.GetRootScopeCoverage(root))
        };
    }

    /// <summary>
    ///  Builds context from known metric semantics without one effective trace scope.
    /// </summary>
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
            Scope = AnalysisScopeContext.Create(root, processScope: null, activityName: null, window: null, rootCoverage: null)
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
