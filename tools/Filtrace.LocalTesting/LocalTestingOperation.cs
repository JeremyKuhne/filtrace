// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Identifies the concrete workflow selected from an action and the persisted state.
/// </summary>
internal enum LocalTestingOperation
{
    /// <summary>
    ///  Installs into a target that has no local-testing state.
    /// </summary>
    FreshInstall,

    /// <summary>
    ///  Continues an installation that stopped after recording its baseline.
    /// </summary>
    ResumeInstall,

    /// <summary>
    ///  Replaces the resources of an active local-testing installation.
    /// </summary>
    Refresh,

    /// <summary>
    ///  Restores the recorded baseline and removes installed resources.
    /// </summary>
    Restore,

    /// <summary>
    ///  Retries cleanup after baseline restoration has already completed.
    /// </summary>
    CleanupRetry
}
