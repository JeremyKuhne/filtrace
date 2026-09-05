// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  One launch recorded inside a command scenario's trace.
/// </summary>
/// <param name="Ordinal">The one-based position of this launch within the scenario.</param>
/// <param name="ProcessId">The launched root process id; descendants resolve from the trace.</param>
/// <param name="ExitCode">The process's exit code, or <c>-1</c> if a duration cap terminated it.</param>
/// <param name="StartedUtc">When the process was launched.</param>
/// <param name="StoppedUtc">When it was observed to have exited.</param>
public sealed record CaptureInvocation(
    int Ordinal,
    int ProcessId,
    int ExitCode,
    DateTimeOffset StartedUtc,
    DateTimeOffset StoppedUtc);
