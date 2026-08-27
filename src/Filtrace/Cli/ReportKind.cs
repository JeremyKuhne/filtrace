// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The structured report selected by the <c>report</c> command.
/// </summary>
internal enum ReportKind
{
    /// <summary>
    ///  Garbage-collection counts, pauses, and heap summary.
    /// </summary>
    Gc,

    /// <summary>
    ///  Just-in-time compilation count, time, and generated-code sizes.
    /// </summary>
    Jit,

    /// <summary>
    ///  Thread-pool worker adjustments and starvation.
    /// </summary>
    Threadpool,

    /// <summary>
    ///  Physical disk bytes and service time by file.
    /// </summary>
    Diskio
}
