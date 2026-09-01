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
    /// <summary>
    ///  Restricts selector implementations to the name and process-id variants defined by this assembly.
    /// </summary>
    private protected ProcessSelector()
    {
    }
}
