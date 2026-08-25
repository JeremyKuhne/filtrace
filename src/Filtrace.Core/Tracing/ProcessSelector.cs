// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  How the roots of a <see cref="ProcessScope"/> are chosen: by name substring
///  (<see cref="ProcessNameSelector"/>) or by exact process id
///  (<see cref="ProcessIdSelector"/>).
/// </summary>
/// <remarks>
///  <para>
///   A name substring is the right selector for interactive discovery - it finds the
///   workload without the caller knowing its process ids. It is the wrong selector for
///   automation: <c>--process dotnet</c> on a development machine matches every
///   unrelated host of that name in a machine-wide capture, and nothing in the result
///   says so. An exact id set is what a capture manifest can record and replay, so the
///   two selectors exist side by side rather than one being encoded into the other.
///  </para>
///  <para>
///   The hierarchy is closed: the constructor is <see langword="private protected"/>,
///   so a <see langword="switch"/> over the two derived types covers every case.
///  </para>
/// </remarks>
public abstract record ProcessSelector
{
    private protected ProcessSelector()
    {
    }
}

/// <summary>
///  Selects scope roots by case-insensitive process-name substring.
/// </summary>
/// <remarks>
///  <para>
///   Validated at construction: an empty substring would match every process and
///   silently disable scoping, and a <see langword="null"/> one would throw a less
///   clear exception later, so a malformed selector fails fast and predictably here.
///  </para>
/// </remarks>
public sealed record ProcessNameSelector : ProcessSelector
{
    /// <summary>The maximum caller-supplied process-name selector length.</summary>
    public const int MaxNameSubstringLength = 256;

    /// <summary>
    ///  Initializes a new instance of the <see cref="ProcessNameSelector"/> class.
    /// </summary>
    /// <param name="nameSubstring">A case-insensitive process-name substring.</param>
    /// <exception cref="ArgumentException">
    ///  <paramref name="nameSubstring"/> is <see langword="null"/>, empty, contains
    ///  control characters, or is longer than <see cref="MaxNameSubstringLength"/>
    ///  characters.
    /// </exception>
    public ProcessNameSelector(string nameSubstring)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameSubstring);
        if (nameSubstring.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Process-name selectors may not contain control characters.",
                nameof(nameSubstring));
        }

        if (nameSubstring.Length > MaxNameSubstringLength)
        {
            throw new ArgumentException(
                $"Process-name selectors may not exceed {MaxNameSubstringLength} characters.",
                nameof(nameSubstring));
        }

        NameSubstring = nameSubstring;
    }

    private ProcessNameSelector(string nameSubstring, bool traceDerived)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameSubstring);
        NameSubstring = nameSubstring;
    }

    internal static ProcessNameSelector FromTraceName(string name) => new(name, traceDerived: true);

    /// <summary>
    ///  The case-insensitive substring matched against process names to find the scope
    ///  roots.
    /// </summary>
    public string NameSubstring { get; }
}

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
    /// <exception cref="ArgumentException"><paramref name="processIds"/> is empty or contains a non-positive id.</exception>
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
