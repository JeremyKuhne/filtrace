// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The ETLX cache operation selected by the <c>cache</c> command.
/// </summary>
internal enum CacheAction
{
    /// <summary>
    ///  Build or reuse the ETLX cache.
    /// </summary>
    Convert,

    /// <summary>
    ///  Remove the ETLX cache.
    /// </summary>
    Clean
}
