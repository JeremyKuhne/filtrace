// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing.Providers;

namespace Filtrace.Tracing;

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
    /// <summary>
    ///  The maximum caller-supplied process-name selector length.
    /// </summary>
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
        if (nameSubstring.Length > MaxNameSubstringLength)
        {
            throw new ArgumentException(
                $"Process-name selectors may not exceed {MaxNameSubstringLength} characters.",
                nameof(nameSubstring));
        }

        if (nameSubstring.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Process-name selectors may not contain control characters.",
                nameof(nameSubstring));
        }

        NameSubstring = nameSubstring;
        DisplayName = nameSubstring;
        DisplayNameChanged = false;
    }

    // A trace-derived name describes what the capture contains rather than what the caller
    // asked for, so it skips selector validation and is bounded where it is reported.
    private ProcessNameSelector(string nameSubstring, bool traceDerived)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameSubstring);
        NameSubstring = nameSubstring;
        DisplayName = TimelineProvider.BoundSnapshotName(nameSubstring, out bool displayNameChanged);
        DisplayNameChanged = displayNameChanged;
    }

    internal static ProcessNameSelector FromTraceName(string name) => new(name, traceDerived: true);

    internal string DisplayName { get; }

    internal bool DisplayNameChanged { get; }

    /// <summary>
    ///  The case-insensitive substring matched against process names to find the scope
    ///  roots.
    /// </summary>
    public string NameSubstring { get; }
}
