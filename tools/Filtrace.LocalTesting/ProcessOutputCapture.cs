// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text;

namespace Filtrace.LocalTesting;

/// <summary>
///  Retains a bounded, thread-safe snapshot of one redirected process stream.
/// </summary>
internal sealed class ProcessOutputCapture
{
    private readonly Lock _lock = new();
    private readonly int _maximumCharacters;
    private readonly StringBuilder _text = new();
    private bool _truncated;

    /// <summary>
    ///  Creates an output capture with the given retained-character limit.
    /// </summary>
    /// <param name="maximumCharacters">The maximum number of characters to retain.</param>
    public ProcessOutputCapture(int maximumCharacters)
    {
        _maximumCharacters = maximumCharacters;
    }

    /// <summary>
    ///  Appends characters while retaining at most the configured limit.
    /// </summary>
    /// <param name="buffer">The source buffer.</param>
    /// <param name="count">The number of source characters to append.</param>
    public void Append(char[] buffer, int count)
    {
        lock (_lock)
        {
            int remaining = _maximumCharacters - _text.Length;
            if (remaining > 0)
            {
                _text.Append(buffer, 0, Math.Min(count, remaining));
            }

            _truncated |= count > remaining;
        }
    }

    /// <summary>
    ///  Gets a stable snapshot of the retained output and size-limit state.
    /// </summary>
    /// <returns>The retained text and whether additional characters were discarded.</returns>
    public (string Text, bool Truncated) Snapshot()
    {
        lock (_lock)
        {
            return (_text.ToString(), _truncated);
        }
    }
}