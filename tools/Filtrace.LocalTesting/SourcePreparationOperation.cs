// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text;

namespace Filtrace.LocalTesting;

/// <summary>
///  Owns one serialized source preparation and its fixed private operation tree.
/// </summary>
internal sealed class SourcePreparationOperation : IDisposable
{
    private const string DisableFileLockingSwitch = "System.IO.DisableFileLocking";
    private const string DisableFileLockingVariable = "DOTNET_SYSTEM_IO_DISABLEFILELOCKING";

    /// <summary>
    ///  The fixed source-preparation lock file name.
    /// </summary>
    internal const string LockFileName = ".filtrace-local-testing-preparation.lock";

    /// <summary>
    ///  The fixed private source-preparation operation directory name.
    /// </summary>
    internal const string OperationDirectoryName = ".filtrace-local-testing-preparation";
    private const string MarkerFileName = "operation.txt";
    private const string PackageDirectoryName = "packages";

    private readonly Action<string> _deleteOperationDirectory;
    private readonly FileStream _lockStream;
    private bool _disposed;
    private bool _retain;

    private SourcePreparationOperation(
        string gitDirectory,
        FileStream lockStream,
        Action<string> deleteOperationDirectory)
    {
        GitDirectory = gitDirectory;
        LockPath = Path.Join(gitDirectory, LockFileName);
        OperationDirectory = Path.Join(gitDirectory, OperationDirectoryName);
        MarkerPath = Path.Join(OperationDirectory, MarkerFileName);
        PackageDirectory = Path.Join(OperationDirectory, PackageDirectoryName);
        _lockStream = lockStream;
        _deleteOperationDirectory = deleteOperationDirectory;
    }

    /// <summary>
    ///  Gets the resolved Git directory that owns the preparation state.
    /// </summary>
    public string GitDirectory { get; }

    /// <summary>
    ///  Gets the fixed exclusive-lock path.
    /// </summary>
    public string LockPath { get; }

    /// <summary>
    ///  Gets the fixed private operation directory.
    /// </summary>
    public string OperationDirectory { get; }

    /// <summary>
    ///  Gets the durable operation marker path.
    /// </summary>
    public string MarkerPath { get; }

    /// <summary>
    ///  Gets the package output directory owned by this operation.
    /// </summary>
    public string PackageDirectory { get; }

    /// <summary>
    ///  Gets the nonfatal cleanup failure observed during disposal.
    /// </summary>
    public Exception? CleanupFailure { get; private set; }

    /// <summary>
    ///  Acquires the fixed source lock and creates a new durable operation marker.
    /// </summary>
    /// <param name="gitDirectory">The resolved source repository Git directory.</param>
    /// <param name="deleteOperationDirectory">The fixed-tree cleanup operation.</param>
    /// <returns>Exclusive ownership of a new source preparation.</returns>
    public static SourcePreparationOperation Acquire(
        string gitDirectory,
        Action<string> deleteOperationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitDirectory);
        ArgumentNullException.ThrowIfNull(deleteOperationDirectory);
        string git = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitDirectory));
        if (!Directory.Exists(git))
        {
            throw new DirectoryNotFoundException($"Source Git directory does not exist: '{git}'.");
        }

        if (!OperatingSystem.IsWindows() && IsRuntimeFileLockingDisabled())
        {
            throw new InvalidOperationException(
                $"Source preparation requires .NET file locking. Disable '{DisableFileLockingSwitch}' "
                    + $"and unset '{DisableFileLockingVariable}'.");
        }

        string lockPath = Path.Join(git, LockFileName);
        string operationDirectory = Path.Join(git, OperationDirectoryName);
        ManagedPathGuard.EnsureNoLinks(git, lockPath);
        ManagedPathGuard.EnsureNoLinks(git, operationDirectory);
        if (Directory.Exists(lockPath))
        {
            throw new InvalidDataException(
                $"Source-preparation lock is a directory, not a file: '{lockPath}'.");
        }

        if (File.Exists(lockPath))
        {
            RegularFileGuard.Exists(lockPath, "Source-preparation lock");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    lockPath,
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

            stream = new(lockPath, options);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Could not acquire the source-preparation lock for '{git}'. "
                    + "Another source preparation may still own its artifacts.",
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
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        SourcePreparationOperation operation = new(git, stream, deleteOperationDirectory);
        try
        {
            operation.ThrowIfExistingOperation();
            Directory.CreateDirectory(operation.PackageDirectory);
            operation.WriteMarker("running", "not started", rootProcessId: null);
            return operation;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    ///  Durably records the command that is about to run.
    /// </summary>
    /// <param name="invocation">The process invocation about to start.</param>
    public void RecordRunning(ProcessInvocation invocation)
    {
        WriteMarker("running", FormatCommand(invocation), rootProcessId: null);
    }

    /// <summary>
    ///  Retains the operation and records an uncertain process lifetime for manual recovery.
    /// </summary>
    /// <param name="invocation">The process invocation whose lifetime is uncertain.</param>
    /// <param name="result">The bounded process result.</param>
    public void Quarantine(ProcessInvocation invocation, ProcessResult result)
    {
        _retain = true;
        try
        {
            WriteMarker("quarantined", FormatCommand(invocation), result.RootProcessId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Process lifetime is uncertain for root process "
                    + $"{result.RootProcessId?.ToString() ?? "unknown"}, and its marker could not be updated. "
                    + $"The source preparation was retained for manual recovery: '{OperationDirectory}'.",
                exception);
        }
    }

    /// <summary>
    ///  Deletes a completed operation when safe, then releases the source lock.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_retain && Directory.Exists(OperationDirectory))
            {
                _deleteOperationDirectory(OperationDirectory);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CleanupFailure = exception;
        }
        finally
        {
            _lockStream.Dispose();
        }
    }

    private void ThrowIfExistingOperation()
    {
        if (File.Exists(OperationDirectory))
        {
            throw new InvalidDataException(
                $"Source-preparation operation path is a file, not a directory: '{OperationDirectory}'.");
        }

        if (Directory.Exists(OperationDirectory))
        {
            throw new InvalidOperationException(
                $"An incomplete source preparation requires manual recovery: '{OperationDirectory}'. "
                    + $"Inspect '{MarkerPath}' for the recorded command and root process ID. "
                    + "Confirm all related processes have stopped, then remove that exact private directory.");
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

    private void WriteMarker(string status, string command, int? rootProcessId)
    {
        string temporaryPath = Path.Join(OperationDirectory, ".operation.tmp");
        string processId = rootProcessId?.ToString() ?? "not recorded";
        string content = $"Status: {status}{Environment.NewLine}"
            + $"Command: {command}{Environment.NewLine}"
            + $"Root process ID: {processId}{Environment.NewLine}"
            + $"Operation directory: {OperationDirectory}{Environment.NewLine}";

        using (FileStream stream = new(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough))
        {
            using StreamWriter writer = new(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4096,
                leaveOpen: true);

            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, MarkerPath, overwrite: true);
    }

    private static string FormatCommand(ProcessInvocation invocation)
    {
        return $"{invocation.FileName} {string.Join(' ', invocation.Arguments)}";
    }
}