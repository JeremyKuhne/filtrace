// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Collections;
using System.Text.Json.Serialization;

namespace Filtrace.Output;

/// <summary>An operation-neutral follow-up that retains the human reason for it.</summary>
/// <param name="Reason">Why this follow-up is useful.</param>
public sealed record AnalysisNextStep(string Reason)
{
    /// <summary>
    ///  The surface-neutral operation name, or <see langword="null"/> for explanatory
    ///  guidance that is not a filtrace operation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }

    /// <summary>The follow-up arguments, or <see langword="null"/> when none apply.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisNextStepArguments? Arguments { get; init; }
}

/// <summary>Typed arguments shared by structured next-step operations.</summary>
public sealed record AnalysisNextStepArguments
{
    /// <summary>The trace or manifest path.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>The metric selector.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Metric { get; init; }

    /// <summary>The measure selector.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Measure { get; init; }

    /// <summary>The frame selector.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Frame { get; init; }

    /// <summary>The root-frame selector.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; init; }

    /// <summary>The process-name selector.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Process { get; init; }

    /// <summary>The exact process-id selectors.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? ProcessIds { get; init; }

    /// <summary>The full exact-process-id count before output bounding.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessIdCount { get; init; }

    /// <summary>Whether the exact-process-id list was shortened for output.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProcessIdsTruncated { get; init; }

    /// <summary>Whether process descendants are included.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeChildren { get; init; }

    /// <summary>Whether every process is included.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllProcesses { get; init; }

    /// <summary>The activity selector.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Activity { get; init; }

    /// <summary>The time-window start in milliseconds.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FromMs { get; init; }

    /// <summary>The time-window end in milliseconds.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ToMs { get; init; }

    /// <summary>Whether the callers operation also returns callees.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Callees { get; init; }

    /// <summary>The event-page skip count.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Skip { get; init; }

    /// <summary>The maximum rows or events to return.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Take { get; init; }

    /// <summary>The maximum rendered event-payload characters.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPayload { get; init; }

    /// <summary>The event-name filter.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>The event-payload filter.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Payload { get; init; }

    /// <summary>The event process-id filter.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessId { get; init; }

    /// <summary>The event thread-id filter.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ThreadId { get; init; }
}

/// <summary>
///  Text hints plus the structured next steps they represent. Exposed as a read-only
///  string list so existing renderers and callers remain source-compatible.
/// </summary>
internal sealed class SteeringHintSet : IReadOnlyList<string>
{
    private readonly IReadOnlyList<string> _messages;

    public SteeringHintSet(
        IEnumerable<string> messages,
        IEnumerable<AnalysisNextStep>? nextSteps = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = [.. messages];
        NextSteps = nextSteps is null
            ? [.. _messages.Select(static message => new AnalysisNextStep(message))]
            : [.. nextSteps];
    }

    public IReadOnlyList<AnalysisNextStep> NextSteps { get; }

    public int Count => _messages.Count;

    public string this[int index] => _messages[index];

    public IEnumerator<string> GetEnumerator() => _messages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
