// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

internal sealed class LocalTestingTargetLock : IDisposable
{
    private readonly FileStream _stream;

    private LocalTestingTargetLock(FileStream stream)
    {
        _stream = stream;
    }

    public static LocalTestingTargetLock Acquire(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
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

    public void Dispose()
    {
        _stream.Dispose();
    }
}