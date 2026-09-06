// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.ComponentModel;

namespace Filtrace.LocalTesting;

/// <summary>
///  Builds, validates, packs, and locates the exact local artifacts used by Install.
/// </summary>
internal sealed class SourceArtifactPreparer
{
    private static readonly TimeSpan s_processTimeout = TimeSpan.FromMinutes(10);
    private readonly IProcessRunner _processRunner;
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;
    private readonly Action<string> _deleteOperationDirectory;

    /// <summary>
    ///  Creates a preparer backed by bounded real child processes.
    /// </summary>
    public SourceArtifactPreparer()
        : this(new BoundedProcessRunner(), Console.Out, Console.Error)
    {
    }

    /// <summary>
    ///  Creates a preparer with testable process and stream dependencies.
    /// </summary>
    /// <param name="processRunner">The child-process boundary.</param>
    /// <param name="standardOutput">The human-readable progress and child output stream.</param>
    /// <param name="standardError">The human-readable child diagnostic stream.</param>
    /// <param name="deletePackageDirectory">An optional prepared-package cleanup operation.</param>
    internal SourceArtifactPreparer(
        IProcessRunner processRunner,
        TextWriter standardOutput,
        TextWriter standardError,
        Action<string>? deletePackageDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        _processRunner = processRunner;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _deleteOperationDirectory = deletePackageDirectory ?? DeleteOperationDirectory;
    }

    /// <summary>
    ///  Prepares local CLI, MCP, and skill inputs before target mutation starts.
    /// </summary>
    /// <param name="sourceCheckout">The Filtrace source checkout to prepare.</param>
    /// <param name="sourceGitDirectory">The source repository's resolved Git directory.</param>
    /// <param name="configuration">The Debug or Release build configuration.</param>
    /// <param name="dotnetPath">The exact dotnet host selected by the wrapper.</param>
    /// <returns>Validated inputs and ownership of the temporary package directory.</returns>
    public async Task<PreparedInstallInputs> PrepareAsync(
        string sourceCheckout,
        string sourceGitDirectory,
        string configuration,
        string dotnetPath)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceCheckout));
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Filtrace source checkout does not exist: '{source}'.");
        }

        if (configuration is not ("Debug" or "Release"))
        {
            throw new ArgumentException("Configuration must be Debug or Release.", nameof(configuration));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);
        SourcePreparationOperation operation = SourcePreparationOperation.Acquire(sourceGitDirectory, _deleteOperationDirectory);
        try
        {
            await RunAsync(operation, dotnetPath, source, "build", "src/Filtrace/Filtrace.csproj", "--configuration", configuration, "--nologo");
            await RunAsync(operation, dotnetPath, source, "build", "src/Filtrace.Mcp/Filtrace.Mcp.csproj", "--configuration", configuration, "--nologo");
            await RunAsync(operation, dotnetPath, source, "test", "tests/Filtrace.Cli.Tests/Filtrace.Cli.Tests.csproj", "--configuration", configuration);
            await RunAsync(operation, dotnetPath, source, "test", "tests/Filtrace.Mcp.Tests/Filtrace.Mcp.Tests.csproj", "--configuration", configuration);
            await RunAsync(
                operation,
                dotnetPath,
                source,
                "pack",
                "src/Filtrace/Filtrace.csproj",
                "--configuration",
                configuration,
                "--no-build",
                "--nologo",
                "--output",
                operation.PackageDirectory,
                "/p:IncludeSymbols=false");

            string[] packages = Directory.GetFiles(
                operation.PackageDirectory,
                $"{LocalTestingCliPackage.PackageId}.*.nupkg",
                SearchOption.TopDirectoryOnly);

            if (packages.Length is not 1)
            {
                throw new InvalidDataException(
                    $"Expected one prepared Filtrace CLI package; found {packages.Length} in '{operation.PackageDirectory}'.");
            }

            string mcpDll = Path.Join(
                source,
                "src",
                "Filtrace.Mcp",
                "bin",
                configuration,
                "net10.0",
                "Filtrace.Mcp.dll");

            LocalTestingInstallInputs inputs = LocalTestingInstallInputs.Create(
                source,
                packages[0],
                dotnetPath,
                mcpDll,
                Path.Join(source, ".agents", "skills", "filtrace"));

            return new(inputs, operation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            operation.Dispose();
            if (operation.CleanupFailure is not null)
            {
                TryWriteFailedPreparationCleanupWarning(operation);
            }

            throw;
        }
    }

    private void TryWriteFailedPreparationCleanupWarning(SourcePreparationOperation operation)
    {
        try
        {
            _standardError.WriteLine(
                $"Warning: Artifact preparation failed, and private operation cleanup also failed "
                    + $"for '{operation.OperationDirectory}': {operation.CleanupFailure!.Message} "
                    + "No active consumer installation was changed. The retained operation blocks another preparation.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private static void DeleteOperationDirectory(string path)
    {
        LocalTestingDirectory.DeleteTree(path);
    }

    private async Task RunAsync(
        SourcePreparationOperation operation,
        string dotnetPath,
        string sourceCheckout,
        params string[] arguments)
    {
        _standardOutput.WriteLine($"> dotnet {string.Join(' ', arguments)}");
        ProcessInvocation invocation = new(
            dotnetPath,
            arguments,
            sourceCheckout,
            s_processTimeout);

        operation.RecordRunning(invocation);
        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(invocation);
        }
        catch (Win32Exception)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            operation.Quarantine(invocation, new ProcessResult(
                ExitCode: null,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                StandardOutputTruncated: false,
                StandardErrorTruncated: false,
                ExecutionTimedOut: false,
                OutputCaptureIncomplete: true));

            throw new InvalidOperationException(
                $"dotnet {arguments[0]} failed before process completion could be confirmed; "
                    + $"root process ID unknown. The source preparation was retained for manual recovery: "
                    + $"'{operation.OperationDirectory}'.",
                exception);
        }

        bool requiresQuarantine = result.ExecutionTimedOut
            || result.OutputCaptureIncomplete
            || result.ExitCode is null;

        if (requiresQuarantine)
        {
            operation.Quarantine(invocation, result);
        }

        _standardOutput.Write(result.StandardOutput);
        _standardError.Write(result.StandardError);
        if (result.StandardOutputTruncated)
        {
            _standardError.WriteLine("Standard output exceeded the 1 MiB diagnostic limit and was truncated.");
        }

        if (result.StandardErrorTruncated)
        {
            _standardError.WriteLine("Standard error exceeded the 1 MiB diagnostic limit and was truncated.");
        }

        if (requiresQuarantine)
        {
            string processId = result.RootProcessId?.ToString() ?? "unknown";
            string reason = result.ExecutionTimedOut
                ? "exceeded its 10 minute execution deadline"
                : result.OutputCaptureIncomplete
                    ? "exited without closing both captured output streams"
                    : "did not report a confirmed exit code";

            throw new InvalidOperationException(
                $"dotnet {arguments[0]} {reason}; root process ID {processId}. "
                    + $"The source preparation was retained for manual recovery: '{operation.OperationDirectory}'. "
                    + "Confirm all related processes have stopped, then remove that exact private directory.");
        }

        if (result.ExitCode is not 0)
        {
            throw new InvalidOperationException(
                $"dotnet {arguments[0]} exited with code {result.ExitCode?.ToString() ?? "unknown"}.");
        }
    }
}