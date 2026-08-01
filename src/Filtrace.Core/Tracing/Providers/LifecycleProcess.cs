// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One process in a lifecycle report - a root invocation or one of its descendants -
///  with the wall-clock window the trace observed it in.
/// </summary>
/// <remarks>
///  <para>
///   <see cref="StartObserved"/> and <see cref="StopObserved"/> are what make the
///   window trustworthy. A process that was already running when the capture started,
///   or that had not exited when it stopped, is clipped to the capture window, so its
///   lifetime is a lower bound rather than a measurement. Those invocations are
///   excluded from the report's phase statistics.
///  </para>
///  <para>
///   <see cref="CpuMs"/> is sampled CPU time and answers a different question than
///   <see cref="LifetimeMs"/>: a process blocked in the loader or waiting on a child
///   spends wall-clock time without spending CPU.
///  </para>
/// </remarks>
/// <param name="ProcessId">The operating-system process id.</param>
/// <param name="Name">The process name as the trace recorded it.</param>
/// <param name="StartMs">When the process started, in milliseconds from the capture start.</param>
/// <param name="StopMs">When the process stopped, in milliseconds from the capture start.</param>
/// <param name="LifetimeMs">The observed wall-clock lifetime, in milliseconds.</param>
/// <param name="CpuMs">The process's sampled CPU time, in milliseconds.</param>
/// <param name="StartObserved">Whether the capture recorded the process starting.</param>
/// <param name="StopObserved">Whether the capture recorded the process exiting.</param>
/// <param name="ExitStatus">The process exit code, when the capture recorded the exit.</param>
public sealed record LifecycleProcess(
    int ProcessId,
    string Name,
    double StartMs,
    double StopMs,
    double LifetimeMs,
    double CpuMs,
    bool StartObserved,
    bool StopObserved,
    int? ExitStatus);
