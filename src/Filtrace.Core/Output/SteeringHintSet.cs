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

    /// <summary>
    ///  Creates hints whose structured steps either come from the caller or default to message-only guidance.
    /// </summary>
    /// <param name="messages">The human-readable guidance in display order.</param>
    /// <param name="nextSteps">
    ///  Structured equivalents of the messages, or <see langword="null"/> to create message-only steps.
    /// </param>
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

    /// <summary>
    ///  Gets the structured follow-up actions represented by this hint set.
    /// </summary>
    public IReadOnlyList<AnalysisNextStep> NextSteps { get; }

    /// <summary>
    ///  Gets the number of human-readable hints.
    /// </summary>
    public int Count => _messages.Count;

    /// <summary>
    ///  Gets the hint at a zero-based position.
    /// </summary>
    /// <param name="index">The zero-based hint index.</param>
    /// <returns>The human-readable guidance at the requested position.</returns>
    public string this[int index] => _messages[index];

    /// <summary>
    ///  Enumerates the human-readable hints in display order.
    /// </summary>
    /// <returns>An enumerator over the hint messages.</returns>
    public IEnumerator<string> GetEnumerator() => _messages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
