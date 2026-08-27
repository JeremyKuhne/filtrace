// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Collections;

namespace Filtrace.Output;

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
