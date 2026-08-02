// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace;

/// <summary>
///  Marker for the Filtrace analysis core assembly.
/// </summary>
/// <remarks>
///  <para>
///   The analysis itself lives in the readers, providers, and aggregator in this
///   assembly. This type survives only as the scaffold marker the earliest
///   contract tests assert against.
///  </para>
/// </remarks>
public static class FiltraceCore
{
    /// <summary>
    ///  The scaffold generation this assembly corresponds to.
    /// </summary>
    public static string Milestone => "M1";
}
