// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The provenance check outcome before an ETLX cache is opened or rebuilt.
/// </summary>
internal enum EtlxProvenanceState
{
    /// <summary>
    ///  No cache or provenance marker exists.
    /// </summary>
    Missing,

    /// <summary>
    ///  The cache matches its marker, backend, and source.
    /// </summary>
    Current,

    /// <summary>
    ///  The cache is owned, but the source file has changed.
    /// </summary>
    StaleSource,

    /// <summary>
    ///  A prior Filtrace publication did not leave a complete cache pair.
    /// </summary>
    Interrupted
}
