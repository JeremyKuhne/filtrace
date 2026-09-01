// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Holds an exclusive file handle that serializes local-testing mutations for one target repository.
/// </summary>
internal sealed class LocalTestingTargetLock : IDisposable
{
    private const string DisableFileLockingSwitch = "System.IO.DisableFileLocking";
    private const string DisableFileLockingVariable = "DOTNET_SYSTEM_IO_DISABLEFILELOCKING";

    private readonly FileStream _stream;

    private LocalTestingTargetLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    ///  Opens the target's regular lock file without sharing and rejects runtimes configured to disable Unix locks.
    /// </summary>
    /// <param name="plan">The target plan containing the shared git-directory lock path.</param>
    /// <returns>A disposable owner of the exclusive lock handle.</returns>
    public static LocalTestingTargetLock Acquire(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!OperatingSystem.IsWindows() && IsRuntimeFileLockingDisabled())
        {
            throw new InvalidOperationException(
                $"Local testing requires .NET file locking. Disable '{DisableFileLockingSwitch}' "
                    + $"and unset '{DisableFileLockingVariable}'.");
        }

        if (!Directory.Exists(plan.GitDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Git directory does not exist: '{plan.GitDirectory}'.");
        }

        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.LockPath);
        if (Directory.Exists(plan.LockPath))
        {
            throw new InvalidDataException(
                $"Local-testing lock is a directory, not a file: '{plan.LockPath}'.");
        }

        if (File.Exists(plan.LockPath))
        {
            RegularFileGuard.Exists(plan.LockPath, "Local-testing lock");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    plan.LockPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        FileStream stream;
        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.Read,
                Share = FileShare.None
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            stream = new(plan.LockPath, options);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Could not acquire the local-testing lock for '{plan.TargetRoot}'. "
                    + "Another local-testing operation may already be running.",
                exception);
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    stream.SafeFileHandle,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool IsRuntimeFileLockingDisabled()
    {
        string? configured = Environment.GetEnvironmentVariable(DisableFileLockingVariable);
        if (configured is not null)
        {
            if (configured.Equals("1", StringComparison.Ordinal)
                || configured.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (configured.Equals("0", StringComparison.Ordinal)
                || configured.Equals(bool.FalseString, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return AppContext.TryGetSwitch(DisableFileLockingSwitch, out bool disabled) && disabled;
    }

    /// <summary>
    ///  Closes the exclusive file handle so another process can acquire the target lock.
    /// </summary>
    public void Dispose()
    {
        _stream.Dispose();
    }
}
