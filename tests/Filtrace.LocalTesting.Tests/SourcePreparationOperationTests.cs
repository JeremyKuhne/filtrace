// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class SourcePreparationOperationTests
{
    [TestMethod]
    public void Acquire_ValidGitDirectory_CreatesLockPackagesAndInitialMarker()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);

        using SourcePreparationOperation operation = SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        operation.GitDirectory.Should().Be(gitDirectory);
        operation.LockPath.Should().Be(Path.Join(gitDirectory, SourcePreparationOperation.LockFileName));
        operation.OperationDirectory.Should().Be(
            Path.Join(gitDirectory, SourcePreparationOperation.OperationDirectoryName));

        File.Exists(operation.LockPath).Should().BeTrue();
        Directory.Exists(operation.PackageDirectory).Should().BeTrue();
        File.ReadAllText(operation.MarkerPath).Should()
            .Contain("Status: running")
            .And.Contain("Command: not started")
            .And.Contain("Root process ID: not recorded")
            .And.Contain($"Operation directory: {operation.OperationDirectory}");
    }

    [TestMethod]
    public void Acquire_ActiveOwner_BlocksConcurrentOperation()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        using SourcePreparationOperation operation = SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        Action acquireAgain = () => SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        acquireAgain.Should().Throw<InvalidOperationException>()
            .WithMessage("Could not acquire the source-preparation lock*");
    }

    [TestMethod]
    public void Acquire_MissingGitDirectory_ThrowsWithoutCreatingState()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = Path.Join(directory.Path, "missing", ".git");

        Action acquire = () => SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        acquire.Should().Throw<DirectoryNotFoundException>()
            .WithMessage("Source Git directory does not exist:*");

        Directory.Exists(gitDirectory).Should().BeFalse();
        File.Exists(Path.Join(gitDirectory, SourcePreparationOperation.LockFileName)).Should().BeFalse();
    }

    [TestMethod]
    public void Acquire_GitPathIsFile_ThrowsWithoutCreatingSiblingState()
    {
        using TemporaryDirectory directory = new();
        string gitPath = Path.Join(directory.Path, ".git");
        File.WriteAllText(gitPath, "not a directory");

        Action acquire = () => SourcePreparationOperation.Acquire(gitPath, DeleteOperationDirectory);

        acquire.Should().Throw<DirectoryNotFoundException>()
            .WithMessage("Source Git directory does not exist:*");

        Directory.GetFileSystemEntries(directory.Path).Should().ContainSingle().Which.Should().Be(gitPath);
    }

    [TestMethod]
    public void Acquire_ExistingOperationWithMalformedMarker_RefusesWithoutDeletingIt()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        string operationDirectory = Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName);

        Directory.CreateDirectory(operationDirectory);
        string markerPath = Path.Join(operationDirectory, "operation.txt");
        File.WriteAllText(markerPath, "not a marker");

        Action acquire = () => SourcePreparationOperation.Acquire(gitDirectory, DeleteOperationDirectory);

        acquire.Should().Throw<InvalidOperationException>()
            .WithMessage($"*requires manual recovery: '{operationDirectory}'*");

        File.ReadAllText(markerPath).Should().Be("not a marker");
    }

    [TestMethod]
    public void Acquire_ExistingPartialOperationDirectory_RefusesWithoutDeletingIt()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        string operationDirectory = Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName);

        Directory.CreateDirectory(operationDirectory);
        string retainedPath = Path.Join(operationDirectory, "partial-output");
        File.WriteAllText(retainedPath, "retained");

        Action acquire = () => SourcePreparationOperation.Acquire(gitDirectory, DeleteOperationDirectory);

        acquire.Should().Throw<InvalidOperationException>()
            .WithMessage($"*requires manual recovery: '{operationDirectory}'*");

        File.ReadAllText(retainedPath).Should().Be("retained");
    }

    [TestMethod]
    public void Acquire_OperationPathIsFile_ThrowsWithoutDeletingIt()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        string operationPath = Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName);

        File.WriteAllText(operationPath, "retained");

        Action acquire = () => SourcePreparationOperation.Acquire(gitDirectory, DeleteOperationDirectory);

        acquire.Should().Throw<InvalidDataException>()
            .WithMessage("Source-preparation operation path is a file*");

        File.ReadAllText(operationPath).Should().Be("retained");
    }

    [TestMethod]
    public void Acquire_LockPathIsDirectory_ThrowsWithoutDeletingIt()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        string lockPath = Path.Join(gitDirectory, SourcePreparationOperation.LockFileName);
        Directory.CreateDirectory(lockPath);

        Action acquire = () => SourcePreparationOperation.Acquire(gitDirectory, DeleteOperationDirectory);

        acquire.Should().Throw<InvalidDataException>()
            .WithMessage("Source-preparation lock is a directory*");

        Directory.Exists(lockPath).Should().BeTrue();
    }

    [TestMethod]
    public void Acquire_LockPathIsLink_ThrowsWithoutChangingDestination()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        string destination = Path.Join(directory.Path, "external-lock");
        File.WriteAllText(destination, "external");
        string lockPath = Path.Join(gitDirectory, SourcePreparationOperation.LockFileName);
        CreateFileSymbolicLinkOrInconclusive(lockPath, destination);

        Action acquire = () => SourcePreparationOperation.Acquire(gitDirectory, DeleteOperationDirectory);

        acquire.Should().Throw<InvalidDataException>()
            .WithMessage("Managed path must not contain links:*");

        File.ReadAllText(destination).Should().Be("external");
    }

    [TestMethod]
    public void Acquire_OperationPathIsLink_ThrowsWithoutChangingDestination()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        string destination = Path.Join(directory.Path, "external-operation");
        Directory.CreateDirectory(destination);
        string retainedPath = Path.Join(destination, "retained");
        File.WriteAllText(retainedPath, "external");
        string operationPath = Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName);

        CreateDirectorySymbolicLinkOrInconclusive(operationPath, destination);

        Action acquire = () => SourcePreparationOperation.Acquire(gitDirectory, DeleteOperationDirectory);

        acquire.Should().Throw<InvalidDataException>()
            .WithMessage("Managed path must not contain links:*");

        File.ReadAllText(retainedPath).Should().Be("external");
    }

    [TestMethod]
    public void RecordRunning_UpdatesCurrentCommandWithoutClaimingProcessId()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        using SourcePreparationOperation operation = SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        ProcessInvocation invocation = new(
            "local dotnet",
            ["build", "src/Filtrace/Filtrace.csproj"],
            directory.Path,
            TimeSpan.FromMinutes(10));

        operation.RecordRunning(invocation);

        File.ReadAllText(operation.MarkerPath).Should()
            .Contain("Status: running")
            .And.Contain("Command: local dotnet build src/Filtrace/Filtrace.csproj")
            .And.Contain("Root process ID: not recorded");
    }

    [TestMethod]
    public void Quarantine_UnknownLifetime_RetainsOperationAndBlocksRetryAfterDispose()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        SourcePreparationOperation operation = SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        ProcessInvocation invocation = CreateInvocation(directory.Path);
        operation.Quarantine(invocation, CreateUncertainResult(rootProcessId: 4242));

        File.ReadAllText(operation.MarkerPath).Should()
            .Contain("Status: quarantined")
            .And.Contain("Command: dotnet build")
            .And.Contain("Root process ID: 4242");

        operation.Dispose();
        Directory.Exists(operation.OperationDirectory).Should().BeTrue();

        Action acquireAgain = () => SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        acquireAgain.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires manual recovery*");
    }

    [TestMethod]
    public void Quarantine_MarkerUpdateFails_RetainsOperationBeforeThrowing()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        SourcePreparationOperation operation = SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        File.Delete(operation.MarkerPath);
        Directory.CreateDirectory(operation.MarkerPath);

        Action quarantine = () => operation.Quarantine(
            CreateInvocation(directory.Path),
            CreateUncertainResult(rootProcessId: null));

        quarantine.Should().Throw<InvalidOperationException>()
            .WithMessage("Process lifetime is uncertain for root process unknown*marker could not be updated*");

        operation.Dispose();
        Directory.Exists(operation.OperationDirectory).Should().BeTrue();

        Action acquireAgain = () => SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        acquireAgain.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires manual recovery*");
    }

    [TestMethod]
    public void Dispose_CleanupFails_RecordsFailureRetainsStateAndReleasesLock()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        string? cleanupPath = null;
        SourcePreparationOperation operation = SourcePreparationOperation.Acquire(
            gitDirectory,
            path =>
            {
                cleanupPath = path;
                throw new IOException("Injected cleanup failure.");
            });

        Action acquireWhileOwned = () => SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        acquireWhileOwned.Should().Throw<InvalidOperationException>()
            .WithMessage("Could not acquire the source-preparation lock*");

        operation.Dispose();

        cleanupPath.Should().Be(operation.OperationDirectory);
        operation.CleanupFailure.Should().BeOfType<IOException>()
            .Which.Message.Should().Be("Injected cleanup failure.");

        Directory.Exists(operation.OperationDirectory).Should().BeTrue();
        Action acquireAfterFailure = () => SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        acquireAfterFailure.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires manual recovery*");
    }

    [TestMethod]
    public void Dispose_CalledTwice_CleansFixedOperationOnlyOnce()
    {
        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        int cleanupCalls = 0;
        string? cleanupPath = null;
        SourcePreparationOperation operation = SourcePreparationOperation.Acquire(
            gitDirectory,
            path =>
            {
                cleanupCalls++;
                cleanupPath = path;
                Directory.Delete(path, recursive: true);
            });

        operation.Dispose();
        operation.Dispose();

        cleanupCalls.Should().Be(1);
        cleanupPath.Should().Be(
            Path.Join(gitDirectory, SourcePreparationOperation.OperationDirectoryName));

        using SourcePreparationOperation reacquired = SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);
    }

    [TestMethod]
    [DoNotParallelize]
    public void Acquire_RuntimeFileLockingDisabledOnUnix_ThrowsBeforeCreatingState()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);
        const string variable = "DOTNET_SYSTEM_IO_DISABLEFILELOCKING";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");

            Action acquire = () => SourcePreparationOperation.Acquire(
                gitDirectory,
                DeleteOperationDirectory);

            acquire.Should().Throw<InvalidOperationException>()
                .WithMessage("*requires .NET file locking*DOTNET_SYSTEM_IO_DISABLEFILELOCKING*");

            File.Exists(Path.Join(gitDirectory, SourcePreparationOperation.LockFileName)).Should().BeFalse();
            Directory.Exists(Path.Join(
                gitDirectory,
                SourcePreparationOperation.OperationDirectoryName)).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [TestMethod]
    public void Acquire_UnixLockMode_IsOwnerReadWriteOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string gitDirectory = CreateGitDirectory(directory.Path);

        using SourcePreparationOperation operation = SourcePreparationOperation.Acquire(
            gitDirectory,
            DeleteOperationDirectory);

        File.GetUnixFileMode(operation.LockPath).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string CreateGitDirectory(string root)
    {
        string gitDirectory = Path.Join(root, ".git");
        Directory.CreateDirectory(gitDirectory);
        return gitDirectory;
    }

    private static ProcessInvocation CreateInvocation(string workingDirectory)
    {
        return new(
            "dotnet",
            ["build"],
            workingDirectory,
            TimeSpan.FromMinutes(10));
    }

    private static ProcessResult CreateUncertainResult(int? rootProcessId)
    {
        return new(
            ExitCode: null,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            ExecutionTimedOut: true,
            OutputCaptureIncomplete: true,
            RootProcessId: rootProcessId);
    }

    private static void DeleteOperationDirectory(string path)
    {
        Directory.Delete(path, recursive: true);
    }

    private static void CreateFileSymbolicLinkOrInconclusive(string path, string destination)
    {
        try
        {
            File.CreateSymbolicLink(path, destination);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }
    }

    private static void CreateDirectorySymbolicLinkOrInconclusive(string path, string destination)
    {
        try
        {
            Directory.CreateSymbolicLink(path, destination);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }
    }
}