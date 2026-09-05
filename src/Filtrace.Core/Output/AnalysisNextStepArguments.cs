// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Output;

/// <summary>
///  Typed arguments shared by structured next-step operations.
/// </summary>
public sealed record AnalysisNextStepArguments
{
    /// <summary>
    ///  The trace or manifest path.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>
    ///  The capture manifest path for a case-addressed operation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManifestPath { get; init; }

    /// <summary>
    ///  The exact case identifier within <see cref="ManifestPath"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaseId { get; init; }

    /// <summary>
    ///  The metric selector.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Metric { get; init; }

    /// <summary>
    ///  The measure selector.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Measure { get; init; }

    /// <summary>
    ///  Optional fold patterns overriding the rank defaults.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Fold { get; init; }

    /// <summary>
    ///  Optional symbol directory overriding manifest case symbols.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbols { get; init; }

    /// <summary>
    ///  The frame selector.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Frame { get; init; }

    /// <summary>
    ///  The root-frame selector.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; init; }

    /// <summary>
    ///  The process-name selector.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Process { get; init; }

    /// <summary>
    ///  The exact process-id selectors.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? ProcessIds { get; init; }

    /// <summary>
    ///  The full exact-process-id count before output bounding.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessIdCount { get; init; }

    /// <summary>
    ///  Whether the exact-process-id list was shortened for output.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProcessIdsTruncated { get; init; }

    /// <summary>
    ///  Whether process descendants are included.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeChildren { get; init; }

    /// <summary>
    ///  Whether every process is included.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllProcesses { get; init; }

    /// <summary>
    ///  The activity selector.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Activity { get; init; }

    /// <summary>
    ///  The time-window start in milliseconds.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FromMs { get; init; }

    /// <summary>
    ///  The time-window end in milliseconds.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ToMs { get; init; }

    /// <summary>
    ///  Whether the callers operation also returns callees.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Callees { get; init; }

    /// <summary>
    ///  The event-page skip count.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Skip { get; init; }

    /// <summary>
    ///  The maximum rows or events to return.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Take { get; init; }

    /// <summary>
    ///  The maximum rendered event-payload characters.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPayload { get; init; }

    /// <summary>
    ///  The event-name filter.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>
    ///  The event-payload filter.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Payload { get; init; }

    /// <summary>
    ///  The event process-id filter.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessId { get; init; }

    /// <summary>
    ///  The event thread-id filter.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ThreadId { get; init; }
}
