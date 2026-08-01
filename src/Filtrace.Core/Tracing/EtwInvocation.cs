// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  One launched process inside a capture session.
/// </summary>
/// <remarks>
///  <para>
///   A session costs roughly a second of startup and flush, which dwarfs a 30-100 ms
///   command, so a short scenario is captured by running it repeatedly inside one
///   session. The trace then holds every run and this record is what separates them:
///   analysis scopes by root process id, and the timestamps bound each run's window.
///  </para>
///  <para>
///   The executable and its arguments are not repeated here because one capture launches
///   one command; they are on the result that carries these.
///  </para>
/// </remarks>
/// <param name="Ordinal">The one-based position of this launch within the session.</param>
/// <param name="ProcessId">The launched root process id. Descendants resolve from the trace.</param>
/// <param name="ExitCode">The process's exit code, or <c>-1</c> if the duration cap terminated it.</param>
/// <param name="StartedUtc">When the process was launched.</param>
/// <param name="StoppedUtc">When it was observed to have exited.</param>
public sealed record EtwInvocation(
    int Ordinal,
    int ProcessId,
    int ExitCode,
    DateTimeOffset StartedUtc,
    DateTimeOffset StoppedUtc)
{
    /// <summary>
    ///  Wall-clock time from launch to exit. This spans the whole process lifetime, which
    ///  is not the same as its CPU time and overlaps with the other invocations only if
    ///  something ran them concurrently - the capture runs them in sequence.
    /// </summary>
    public TimeSpan Duration => StoppedUtc - StartedUtc;
}
