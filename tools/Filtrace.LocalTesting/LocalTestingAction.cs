// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Identifies the user-requested local-testing transition.
/// </summary>
internal enum LocalTestingAction
{
    /// <summary>
    ///  Represents an absent or unrecognized action.
    /// </summary>
    Unknown,

    /// <summary>
    ///  Installs or refreshes local-testing resources.
    /// </summary>
    Install,

    /// <summary>
    ///  Restores the resources captured before installation.
    /// </summary>
    Restore
}
