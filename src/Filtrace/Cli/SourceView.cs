// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The source-attribution view selected by the <c>source</c> command.
/// </summary>
internal enum SourceView
{
    /// <summary>
    ///  Rank hottest source lines across matching methods.
    /// </summary>
    Lines,

    /// <summary>
    ///  Build per-line heat for one source file.
    /// </summary>
    Heatmap
}
