// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

internal enum LocalTestingAction
{
    Unknown,
    Install,
    Restore
}

internal enum LocalTestingOperation
{
    FreshInstall,
    ResumeInstall,
    Refresh,
    Restore,
    CleanupRetry
}

internal static class LocalTestingOperationClassifier
{
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