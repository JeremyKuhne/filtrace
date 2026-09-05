// Copyright (c) Jeremy W Kuhne and contributors
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
        /// <summary>
        ///  No local PDB with a verifiable identity was found.
        /// </summary>
        NotFound,

        /// <summary>
        ///  A same-named local PDB exists but does not match the trace-recorded signature and age.
        /// </summary>
        IdentityMismatch,

        /// <summary>
        ///  Local symbol lookup verified the trace-recorded PDB identity.
        /// </summary>
        Matched
    }
}
