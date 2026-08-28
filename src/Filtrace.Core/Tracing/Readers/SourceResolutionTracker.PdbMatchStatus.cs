// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal sealed partial class SourceResolutionTracker
{
    /// <summary>
    ///  Classifies whether a candidate PDB matches a trace module's identity.
    /// </summary>
    internal enum PdbMatchStatus
    {
        NotFound,
        IdentityMismatch,
        Matched
    }
}