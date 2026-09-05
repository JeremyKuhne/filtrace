// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Selects scope roots by exact operating-system process id.
/// </summary>
/// <remarks>
///  <para>
///   Ids are deduplicated and ordered on construction so a scope built from a manifest
///   reads and keys the same however the caller listed them.
///  </para>
/// </remarks>
public sealed record ProcessIdSelector : ProcessSelector
{
    /// <summary>
    ///  Initializes a new instance of the <see cref="ProcessIdSelector"/> class.
    /// </summary>
    /// <param name="processIds">The exact process ids to scope to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="processIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    ///  <paramref name="processIds"/> is empty or contains a non-positive id.
    /// </exception>
    public ProcessIdSelector(IEnumerable<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);

        SortedSet<int> unique = [];
        foreach (int processId in processIds)
        {
            // Pid 0 is the Idle pseudo-process and negative ids are not process ids at
            // all; either would produce a scope that silently matches nothing.
            if (processId <= 0)
            {
                throw new ArgumentException(
                    $"Process id {processId} is not a valid process id; ids must be positive.",
                    nameof(processIds));
            }

            unique.Add(processId);
        }

        if (unique.Count == 0)
        {
            throw new ArgumentException("At least one process id is required.", nameof(processIds));
        }

        ProcessIds = [.. unique];
    }

    /// <summary>
    ///  The exact process ids to scope to, deduplicated and ascending.
    /// </summary>
    public IReadOnlyList<int> ProcessIds { get; }
}
