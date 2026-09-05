// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Describes how an ETLX cache request was satisfied.
/// </summary>
public enum EtlxCacheState
{
    /// <summary>
    ///  An up-to-date cache was already available.
    /// </summary>
    Hit,

    /// <summary>
    ///  The request waited for same-trace work, then reused its parsed result or ETLX cache.
    /// </summary>
    Waited,

    /// <summary>
    ///  The request converted and published a new cache.
    /// </summary>
    Converted,

    /// <summary>
    ///  The request rebuilt an unreadable cache, removed stale conversion state,
    ///  or took over an abandoned conversion lock.
    /// </summary>
    Recovered
}
