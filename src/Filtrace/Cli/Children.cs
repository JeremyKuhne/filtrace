// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  Whether a process scope follows the matched processes' descendants.
/// </summary>
internal enum Children
{
    /// <summary>
    ///  Include every descendant of a matched process. The default, because the common
    ///  capture shapes put the measured work in a child the host launched.
    /// </summary>
    Include,

    /// <summary>
    ///  Confine the scope to the matched processes themselves, which is what separates
    ///  a parent's own CPU from a child runtime's.
    /// </summary>
    Exclude
}
