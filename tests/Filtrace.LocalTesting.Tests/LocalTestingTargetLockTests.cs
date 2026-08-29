// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Reflection;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingTargetLockTests
{
    [TestMethod]
    public void Acquire_UnlockedTarget_CreatesFixedLockFile()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");

        using LocalTestingTargetLock targetLock = LocalTestingTargetLock.Acquire(plan);

        File.Exists(plan.LockPath).Should().BeTrue();
    }

    [TestMethod]
    public void Acquire_MissingGitDirectory_ThrowsWithoutCreatingIt()
    {
        using TemporaryDirectory directory = new();
        string targetRoot = Path.Join(directory.Path, "target");
        string gitDirectory = Path.Join(directory.Path, "missing-git");
        Directory.CreateDirectory(targetRoot);
        ResourcePlan plan = ResourcePlan.Create(targetRoot, gitDirectory);

        Action acquire = () => LocalTestingTargetLock.Acquire(plan);

        acquire.Should().Throw<DirectoryNotFoundException>()
            .WithMessage("*Git directory does not exist*");
        Directory.Exists(gitDirectory).Should().BeFalse();
    }

    [TestMethod]
    public void Acquire_GitDirectoryIsFile_Throws()
    {
        using TemporaryDirectory directory = new();
        string targetRoot = Path.Join(directory.Path, "target");
        string gitPath = Path.Join(directory.Path, "git-file");
        Directory.CreateDirectory(targetRoot);
        File.WriteAllText(gitPath, string.Empty);
        ResourcePlan plan = ResourcePlan.Create(targetRoot, gitPath);

        Action acquire = () => LocalTestingTargetLock.Acquire(plan);

        acquire.Should().Throw<DirectoryNotFoundException>()
            .WithMessage("*Git directory does not exist*");
    }

    [TestMethod]
    public void Acquire_LockHeld_ThrowsActionableError()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        using LocalTestingTargetLock targetLock = LocalTestingTargetLock.Acquire(plan);

        Action acquireAgain = () => LocalTestingTargetLock.Acquire(plan);

        acquireAgain.Should().Throw<InvalidOperationException>()
            .WithMessage("*Could not acquire*may already be running*");
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Acquire_SeparateProcessHoldsLock_ThrowsActionableError()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        string readyPath = Path.Join(directory.Path, "ready");
        string releasePath = Path.Join(directory.Path, "release");
        ProcessStartInfo startInfo = new(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        startInfo.Environment[LocalTestingTargetLockProcessProbe.EnabledVariable] = "1";
        startInfo.Environment[LocalTestingTargetLockProcessProbe.TargetRootVariable] =
            plan.TargetRoot;
        startInfo.Environment[LocalTestingTargetLockProcessProbe.GitDirectoryVariable] =
            plan.GitDirectory;
        startInfo.Environment[LocalTestingTargetLockProcessProbe.ReadyPathVariable] = readyPath;
        startInfo.Environment[LocalTestingTargetLockProcessProbe.ReleasePathVariable] = releasePath;
        using Process child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the lock probe.");
        try
        {
            bool ready = SpinWait.SpinUntil(
                () => File.Exists(readyPath) || child.HasExited,
                TimeSpan.FromSeconds(10));
            if (!ready || child.HasExited)
            {
                string error = await child.StandardError.ReadToEndAsync();
                Assert.Fail($"Lock probe failed before signaling readiness: {error}");
            }

            Action acquire = () => LocalTestingTargetLock.Acquire(plan);

            acquire.Should().Throw<InvalidOperationException>()
                .WithMessage("*Could not acquire*may already be running*");
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
            if (!child.WaitForExit(5_000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }

        child.ExitCode.Should().Be(0, await child.StandardError.ReadToEndAsync());
    using LocalTestingTargetLock reacquired = LocalTestingTargetLock.Acquire(plan);
    }

    [TestMethod]
    public void Acquire_PreviousLockDisposed_ReacquiresSameFile()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        LocalTestingTargetLock.Acquire(plan).Dispose();
        FileInfo lockFile = new(plan.LockPath);

        using LocalTestingTargetLock reacquired = LocalTestingTargetLock.Acquire(plan);

        File.Exists(lockFile.FullName).Should().BeTrue();
    }

    [TestMethod]
    public void Acquire_PreexistingRegularFile_UsesSameLockFile()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        File.WriteAllText(plan.LockPath, "stale");

        using (LocalTestingTargetLock targetLock = LocalTestingTargetLock.Acquire(plan))
        {
        }

        File.ReadAllText(plan.LockPath).Should().Be("stale");
    }

    [TestMethod]
    public void Acquire_IndependentWorktrees_AllowsConcurrentLocks()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan first = CreatePlan(directory.Path, "target-one", "git-one");
        ResourcePlan second = CreatePlan(directory.Path, "target-two", "git-two");

        using LocalTestingTargetLock firstLock = LocalTestingTargetLock.Acquire(first);
        using LocalTestingTargetLock secondLock = LocalTestingTargetLock.Acquire(second);

        File.Exists(first.LockPath).Should().BeTrue();
        File.Exists(second.LockPath).Should().BeTrue();
    }

    [TestMethod]
    public void Acquire_LockPathIsDirectory_Throws()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        Directory.CreateDirectory(plan.LockPath);

        Action acquire = () => LocalTestingTargetLock.Acquire(plan);

        acquire.Should().Throw<InvalidDataException>()
            .WithMessage("*lock is a directory*");
    }

    [TestMethod]
    public void Acquire_LockPathIsLink_Throws()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        string destination = Path.Join(directory.Path, "external-lock");
        File.WriteAllText(destination, string.Empty);
        try
        {
            File.CreateSymbolicLink(plan.LockPath, destination);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        Action acquire = () => LocalTestingTargetLock.Acquire(plan);

        acquire.Should().Throw<InvalidDataException>()
            .WithMessage("*must not contain links*");
    }

    [TestMethod]
    public void Acquire_UnixLockMode_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");

        using LocalTestingTargetLock targetLock = LocalTestingTargetLock.Acquire(plan);

        File.GetUnixFileMode(plan.LockPath).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [TestMethod]
    public void Acquire_PreexistingUnixLockMode_NormalizesToOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        File.WriteAllText(plan.LockPath, string.Empty);
        File.SetUnixFileMode(
            plan.LockPath,
            UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead
                | UnixFileMode.OtherRead);

        using LocalTestingTargetLock targetLock = LocalTestingTargetLock.Acquire(plan);

        File.GetUnixFileMode(plan.LockPath).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [TestMethod]
    public void Acquire_PreexistingOwnerReadOnlyUnixLock_NormalizesToOwnerReadWrite()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        File.WriteAllText(plan.LockPath, string.Empty);
        File.SetUnixFileMode(plan.LockPath, UnixFileMode.UserRead);

        using LocalTestingTargetLock targetLock = LocalTestingTargetLock.Acquire(plan);

        File.GetUnixFileMode(plan.LockPath).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [TestMethod]
    [Timeout(5_000)]
    public void Acquire_UnixFifoLock_ThrowsWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory.Path, "target", "git");
        UnixTestFile.CreateFifo(plan.LockPath);

        Action acquire = () => LocalTestingTargetLock.Acquire(plan);

        acquire.Should().Throw<InvalidDataException>()
            .WithMessage("*regular file*");
    }

    private static ResourcePlan CreatePlan(string root, string targetName, string gitName)
    {
        string targetRoot = Path.Join(root, targetName);
        string gitDirectory = Path.Join(root, gitName);
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(gitDirectory);
        return ResourcePlan.Create(targetRoot, gitDirectory);
    }
}