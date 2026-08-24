// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The process exit codes the CLI verbs return.
/// </summary>
/// <remarks>
///  <para>
///   <see cref="Success"/> for a completed query, <see cref="UsageError"/> for a
///   bad command line, <see cref="InputError"/> when the trace could not be
///   loaded, and <see cref="QualityGate"/> when an otherwise successful run tripped
///   an opt-in quality or capture-acceptance gate.
///  </para>
/// </remarks>
internal static class ExitCodes
{
    /// <summary>
    ///  The verb completed and produced a result.
    /// </summary>
    public const int Success = 0;

    /// <summary>
    ///  The command line was malformed (unknown verb, missing or invalid option).
    /// </summary>
    public const int UsageError = 1;

    /// <summary>
    ///  The trace could not be loaded (missing file or unrecognized format).
    /// </summary>
    public const int InputError = 2;

    /// <summary>
    ///  The run succeeded but an opt-in quality or capture-acceptance policy rejected
    ///  its evidence.
    /// </summary>
    public const int QualityGate = 3;
}
