// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Selects the safe workflow for a requested action and the target's persisted state.
/// </summary>
internal static class LocalTestingOperationClassifier
{
    /// <summary>
    ///  Resolves a requested action to the workflow valid for the current installation state.
    /// </summary>
    /// <param name="action">The install or restore action requested by the user.</param>
    /// <param name="state">
    ///  The persisted state, or <see langword="null"/> when local testing has not been installed.
    /// </param>
    /// <returns>The workflow that can continue from the supplied state.</returns>
    public static LocalTestingOperation Classify(
        LocalTestingAction action,
        LocalTestingState? state)
    {
        if (action is not LocalTestingAction.Install and not LocalTestingAction.Restore)
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action.");
        }

        if (state is null)
        {
            return action is LocalTestingAction.Install
                ? LocalTestingOperation.FreshInstall
                : throw new InvalidOperationException("Restore requires existing local-testing state.");
        }

        if (state.Status is LocalTestingStatus.Unknown || !Enum.IsDefined(state.Status))
        {
            throw new InvalidDataException("Local-testing state has an unknown status.");
        }

        return (state.Status, action) switch
        {
            (LocalTestingStatus.Installing, LocalTestingAction.Install) =>
                LocalTestingOperation.ResumeInstall,
            (LocalTestingStatus.Installing, LocalTestingAction.Restore) =>
                LocalTestingOperation.Restore,
            (LocalTestingStatus.Active, LocalTestingAction.Install) =>
                LocalTestingOperation.Refresh,
            (LocalTestingStatus.Active, LocalTestingAction.Restore) =>
                LocalTestingOperation.Restore,
            (LocalTestingStatus.Restoring, LocalTestingAction.Restore) =>
                LocalTestingOperation.Restore,
            (LocalTestingStatus.Cleanup, LocalTestingAction.Restore) =>
                LocalTestingOperation.CleanupRetry,
            _ => throw new InvalidOperationException(
                $"Action '{action}' is not valid while local-testing state is '{state.Status}'.")
        };
    }
}
