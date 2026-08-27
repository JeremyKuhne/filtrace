// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Outcome of a local native symbol lookup for a single module.
/// </summary>
internal enum NativeSymbolStatus
{
    /// <summary>
    ///  The module's symbols were found on the local path and applied.
    /// </summary>
    Resolved,

    /// <summary>
    ///  No symbol file for the module was found on the local path.
    /// </summary>
    NoSymbolFile,

    /// <summary>
    ///  A symbol file with the module's PDB name exists locally, but its identity
    ///  (signature and age) does not match the module in the trace.
    /// </summary>
    IdentityMismatch,

    /// <summary>
    ///  The module's share of unresolved samples was too small to spend a lookup on.
    /// </summary>
    NotAttempted,

    /// <summary>
    ///  The lookup was attempted and failed.
    /// </summary>
    LookupFailed
}
