// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class SourceArtifactPreparerTests
{
    [TestMethod]
    public async Task PrepareAsync_ValidSource_UsesFocusedCommandsAndLocalArtifacts()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source with spaces"));
        RecordingProcessRunner runner = new(source, failInvocation: null);
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);

        using PreparedInstallInputs prepared = await preparer.PrepareAsync(
            source,
            Path.Join(source, ".git"),
            "Release",
            "local dotnet");

        runner.Invocations.Should().HaveCount(5);
        runner.Invocations.Select(invocation => invocation.Arguments[0]).Should().Equal(
            "build",
            "build",
            "test",
            "test",
            "pack");

        string[][] expectedArguments =
        [
            ["build", "src/Filtrace/Filtrace.csproj", "--configuration", "Release", "--nologo"],
            ["build", "src/Filtrace.Mcp/Filtrace.Mcp.csproj", "--configuration", "Release", "--nologo"],
            ["test", "tests/Filtrace.Cli.Tests/Filtrace.Cli.Tests.csproj", "--configuration", "Release"],
            ["test", "tests/Filtrace.Mcp.Tests/Filtrace.Mcp.Tests.csproj", "--configuration", "Release"],
            [
                "pack",
                "src/Filtrace/Filtrace.csproj",
                "--configuration",
                "Release",
                "--no-build",
                "--nologo",
                "--output",
                prepared.PackageDirectory,
                "/p:IncludeSymbols=false"
            ]
        ];

        for (int invocationIndex = 0; invocationIndex < expectedArguments.Length; invocationIndex++)
        {
            runner.Invocations[invocationIndex].Arguments.Should().Equal(expectedArguments[invocationIndex]);
        }

        foreach (ProcessInvocation invocation in runner.Invocations)
        {
            invocation.FileName.Should().Be("local dotnet");
            invocation.WorkingDirectory.Should().Be(source);
            invocation.Timeout.Should().Be(TimeSpan.FromMinutes(10));
            invocation.EnvironmentVariables.Should().BeNull();
        }

        prepared.Inputs.SourceCheckout.Should().Be(source);
        prepared.Inputs.DotnetPath.Should().Be("local dotnet");
        prepared.Inputs.McpDllPath.Should().StartWith(source);
        prepared.Inputs.SkillDirectory.Should().Be(Path.Join(source, ".agents", "skills", "filtrace"));
    }

    [TestMethod]
    public async Task PrepareAsync_InvalidParameters_DoNotAcquireOrRun()
    {
        using TemporaryDirectory directory = new();
        string missingSource = Path.Join(directory.Path, "missing");
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: null);
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);

        Func<Task> missing = () => preparer.PrepareAsync(missingSource, gitDirectory, "Release", "dotnet");
        Func<Task> configuration = () => preparer.PrepareAsync(source, gitDirectory, "Retail", "dotnet");
        Func<Task> dotnet = () => preparer.PrepareAsync(source, gitDirectory, "Release", " ");

        await missing.Should().ThrowAsync<DirectoryNotFoundException>();
        await configuration.Should().ThrowAsync<ArgumentException>();
        await dotnet.Should().ThrowAsync<ArgumentException>();
        runner.Invocations.Should().BeEmpty();
        Directory.Exists(Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName)).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task PrepareAsync_InvalidPackageCount_CleansOperation(int packageCount)
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: null, packageCount: packageCount);
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);

        Func<Task> prepare = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await prepare.Should().ThrowAsync<InvalidDataException>()
            .WithMessage($"Expected one prepared Filtrace CLI package; found {packageCount}*");

        runner.Invocations.Should().HaveCount(5);
        Directory.Exists(Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName)).Should().BeFalse();
    }

    [TestMethod]
    public async Task PrepareAsync_InvalidPackage_CleansOperation()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: null, invalidPackage: true);
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);

        Func<Task> prepare = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await prepare.Should().ThrowAsync<InvalidDataException>();
        runner.Invocations.Should().HaveCount(5);
        Directory.Exists(Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName)).Should().BeFalse();
    }

    [TestMethod]
    public async Task PrepareAsync_FirstCommandFails_DoesNotProducePreparedInput()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        RecordingProcessRunner runner = new(source, failInvocation: 1);
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);

        Func<Task> prepare = async () =>
        {
            using PreparedInstallInputs prepared = await preparer.PrepareAsync(
                source,
                Path.Join(source, ".git"),
                "Debug",
                "dotnet");
        };

        await prepare.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dotnet build exited with code 9.");

        runner.Invocations.Should().ContainSingle();
        Directory.Exists(Path.Join(
            source,
            ".git",
            SourcePreparationOperation.OperationDirectoryName)).Should().BeFalse();

        using PreparedInstallInputs retried = await preparer.PrepareAsync(
            source,
            Path.Join(source, ".git"),
            "Debug",
            "dotnet");

        runner.Invocations.Should().HaveCount(6);
    }

    [TestMethod]
    public async Task PrepareAsync_IncompleteCapture_RetainsQuarantineAndBlocksRetry()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(
            source,
            failInvocation: null,
            incompleteCapture: true);

        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);
        Func<Task> first = async () =>
        {
            using PreparedInstallInputs prepared = await preparer.PrepareAsync(
                source,
                gitDirectory,
                "Release",
                "dotnet");
        };

        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*root process ID 4242*retained for manual recovery*");

        string operationDirectory = Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName);

        string marker = File.ReadAllText(Path.Join(operationDirectory, "operation.txt"));
        marker.Should()
            .Contain("Status: quarantined")
            .And.Contain("Command: dotnet build")
            .And.Contain("Root process ID: 4242");

        Func<Task> second = async () =>
        {
            using PreparedInstallInputs prepared = await preparer.PrepareAsync(
                source,
                gitDirectory,
                "Release",
                "dotnet");
        };

        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*requires manual recovery: '{operationDirectory}'*");

        runner.Invocations.Should().ContainSingle();
    }

    [TestMethod]
    public async Task PrepareAsync_IncompleteCapture_OutputWriteFails_RetainsQuarantineAndBlocksRetry()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: null, incompleteCapture: true);
        SourceArtifactPreparer preparer = new(runner, new ThrowingWriteTextWriter(), TextWriter.Null);

        Func<Task> first = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await first.Should().ThrowAsync<IOException>()
            .WithMessage("Injected output write failure.");

        string operationDirectory = Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName);

        string marker = File.ReadAllText(Path.Join(operationDirectory, "operation.txt"));
        marker.Should()
            .Contain("Status: quarantined")
            .And.Contain("Command: dotnet build")
            .And.Contain("Root process ID: 4242");

        Func<Task> second = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*requires manual recovery: '{operationDirectory}'*");

        runner.Invocations.Should().ContainSingle();
    }

    [TestMethod]
    public async Task PrepareAsync_ExecutionTimeout_RetainsQuarantineAndBlocksRetry()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: null, executionTimedOut: true);
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);

        Func<Task> first = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeded its 10 minute execution deadline; root process ID 4242*");

        Func<Task> second = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires manual recovery*");

        runner.Invocations.Should().ContainSingle();
    }

    [TestMethod]
    public async Task PrepareAsync_UnconfirmedExitWithoutProcessId_RetainsQuarantine()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: null, exitUnconfirmed: true);
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);

        Func<Task> first = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*did not report a confirmed exit code; root process ID unknown*");

        Func<Task> second = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires manual recovery*");

        runner.Invocations.Should().ContainSingle();
    }

    [TestMethod]
    public async Task PrepareAsync_TruncatedCompleteOutput_WarnsAndSucceeds()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: null, truncatedOutput: true);
        StringWriter error = new();
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, error);

        using (PreparedInstallInputs prepared = await preparer.PrepareAsync(
            source,
            gitDirectory,
            "Release",
            "dotnet"))
        {
            error.ToString().Should()
                .Contain("Standard output exceeded the 1,048,576-character diagnostic limit")
                .And.Contain("Standard error exceeded the 1,048,576-character diagnostic limit");
        }

        Directory.Exists(Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName)).Should().BeFalse();
    }

    [TestMethod]
    public async Task PrepareAsync_KnownStartFailure_CleansAndAllowsRetry()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        System.ComponentModel.Win32Exception nativeFailure = new(2, "Injected start failure.");
        ProcessStartException startFailure = new("Could not start 'dotnet'.", nativeFailure);
        RecordingProcessRunner runner = new(
            source,
            failInvocation: null,
            startException: startFailure);

        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);
        Func<Task> first = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        ProcessStartException thrown = (await first.Should().ThrowAsync<ProcessStartException>()).Which;
        thrown.Should().BeSameAs(startFailure);
        thrown.InnerException.Should().BeSameAs(nativeFailure);

        Directory.Exists(Path.Join(
            gitDirectory,
            SourcePreparationOperation.OperationDirectoryName)).Should().BeFalse();

        using PreparedInstallInputs retried = await preparer.PrepareAsync(
            source,
            gitDirectory,
            "Release",
            "dotnet");

        runner.Invocations.Should().HaveCount(6);
    }

    [TestMethod]
    [DataRow(data: false)]
    [DataRow(data: true)]
    public async Task PrepareAsync_UnexpectedRunnerFailure_RetainsQuarantine(bool useWin32Exception)
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        Exception exception;
        if (useWin32Exception)
        {
            exception = new System.ComponentModel.Win32Exception(6, "Injected uncertain native failure.");
        }
        else
        {
            exception = new IOException("Injected uncertain failure.");
        }

        RecordingProcessRunner runner = new(
            source,
            failInvocation: null,
            startException: exception);

        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);
        Func<Task> first = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*failed before process completion could be confirmed*retained for manual recovery*");

        Func<Task> second = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires manual recovery*");

        runner.Invocations.Should().ContainSingle();
    }

    [TestMethod]
    public async Task PrepareAsync_PreparedOwner_BlocksConcurrentPreparationUntilDisposed()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: null);
        SourceArtifactPreparer preparer = new(runner, TextWriter.Null, TextWriter.Null);
        PreparedInstallInputs prepared = await preparer.PrepareAsync(
            source,
            gitDirectory,
            "Release",
            "dotnet");

        try
        {
            Func<Task> concurrent = async () =>
            {
                using PreparedInstallInputs unused = await preparer.PrepareAsync(
                    source,
                    gitDirectory,
                    "Release",
                    "dotnet");
            };

            await concurrent.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Could not acquire the source-preparation lock*");

            runner.Invocations.Should().HaveCount(5);
        }
        finally
        {
            prepared.Dispose();
        }

        using PreparedInstallInputs retried = await preparer.PrepareAsync(
            source,
            gitDirectory,
            "Release",
            "dotnet");

        runner.Invocations.Should().HaveCount(10);
    }

    [TestMethod]
    public async Task PrepareAsync_FailedCleanup_PreservesFailureAndBlocksRetry()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        RecordingProcessRunner runner = new(source, failInvocation: 1);
        StringWriter error = new();
        SourceArtifactPreparer preparer = new(
            runner,
            TextWriter.Null,
            error,
            _ => throw new IOException("Injected cleanup failure."));

        Func<Task> first = async () =>
        {
            using PreparedInstallInputs prepared = await preparer.PrepareAsync(
                source,
                gitDirectory,
                "Release",
                "dotnet");
        };

        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dotnet build exited with code 9.");

        error.ToString().Should()
            .Contain("Injected cleanup failure.")
            .And.Contain("No active consumer installation was changed")
            .And.Contain("blocks another preparation");

        Func<Task> second = async () =>
        {
            using PreparedInstallInputs prepared = await preparer.PrepareAsync(
                source,
                gitDirectory,
                "Release",
                "dotnet");
        };

        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*incomplete source preparation requires manual recovery*");

        runner.Invocations.Should().ContainSingle();
    }

    [TestMethod]
    public async Task PrepareAsync_FailedCleanupWarningWriteFails_PreservesOriginalFailureAndBlocksRetry()
    {
        using TemporaryDirectory directory = new();
        string source = CreateSource(Path.Join(directory.Path, "source"));
        string gitDirectory = Path.Join(source, ".git");
        System.ComponentModel.Win32Exception originalFailure = new(2, "Injected start failure.");
        ProcessStartException startFailure = new("Could not start 'dotnet'.", originalFailure);
        RecordingProcessRunner runner = new(
            source,
            failInvocation: null,
            startException: startFailure);

        SourceArtifactPreparer preparer = new(
            runner,
            TextWriter.Null,
            new ThrowingWriteLineTextWriter(),
            _ => throw new IOException("Injected cleanup failure."));

        Func<Task> first = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        ProcessStartException thrown =
            (await first.Should().ThrowAsync<ProcessStartException>()).Which;

        thrown.Should().BeSameAs(startFailure);
        thrown.InnerException.Should().BeSameAs(originalFailure);

        Func<Task> second = () => preparer.PrepareAsync(source, gitDirectory, "Release", "dotnet");

        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*incomplete source preparation requires manual recovery*");

        runner.Invocations.Should().ContainSingle();
    }

    private static string CreateSource(string source)
    {
        Directory.CreateDirectory(Path.Join(source, ".git"));
        Directory.CreateDirectory(Path.Join(source, ".agents", "skills", "filtrace"));
        File.WriteAllText(Path.Join(source, ".agents", "skills", "filtrace", "SKILL.md"), "skill");
        return source;
    }

    private sealed class RecordingProcessRunner(
        string source,
        int? failInvocation,
        bool incompleteCapture = false,
        bool executionTimedOut = false,
        bool exitUnconfirmed = false,
        bool truncatedOutput = false,
        int packageCount = 1,
        bool invalidPackage = false,
        Exception? startException = null) : IProcessRunner
    {
        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessInvocation invocation)
        {
            Invocations.Add(invocation);
            if (Invocations.Count is 1 && startException is not null)
            {
                Exception exception = startException;
                startException = null;
                return Task.FromException<ProcessResult>(exception);
            }

            if (Invocations.Count == failInvocation)
            {
                return Task.FromResult(new ProcessResult(
                    9,
                    string.Empty,
                    "failed",
                    StandardOutputTruncated: false,
                    StandardErrorTruncated: false,
                    ExecutionTimedOut: false));
            }

            if (incompleteCapture)
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    "partial output",
                    string.Empty,
                    StandardOutputTruncated: false,
                    StandardErrorTruncated: false,
                    ExecutionTimedOut: false,
                    OutputCaptureIncomplete: true,
                    RootProcessId: 4242));
            }

            if (executionTimedOut)
            {
                return Task.FromResult(new ProcessResult(
                    ExitCode: null,
                    string.Empty,
                    string.Empty,
                    StandardOutputTruncated: false,
                    StandardErrorTruncated: false,
                    ExecutionTimedOut: true,
                    OutputCaptureIncomplete: false,
                    RootProcessId: 4242));
            }

            if (exitUnconfirmed)
            {
                return Task.FromResult(new ProcessResult(
                    ExitCode: null,
                    string.Empty,
                    string.Empty,
                    StandardOutputTruncated: false,
                    StandardErrorTruncated: false,
                    ExecutionTimedOut: false));
            }

            if (invocation.Arguments[0].Equals("build", StringComparison.Ordinal)
                && invocation.Arguments[1].Contains("Filtrace.Mcp", StringComparison.Ordinal))
            {
                string configuration = invocation.Arguments[3];
                string output = Path.Join(source, "src", "Filtrace.Mcp", "bin", configuration, "net10.0");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Join(output, "Filtrace.Mcp.dll"), "mcp");
            }

            if (invocation.Arguments[0].Equals("pack", StringComparison.Ordinal))
            {
                int outputIndex = invocation.Arguments.ToList().IndexOf("--output");
                string output = invocation.Arguments[outputIndex + 1];
                if (packageCount > 0)
                {
                    string package = LocalTestingInstallTestData.CreateMetadataPackage(output);
                    if (invalidPackage)
                    {
                        File.WriteAllText(package, "invalid package");
                    }

                    if (packageCount > 1)
                    {
                        File.Copy(
                            package,
                            Path.Join(output, "KlutzyNinja.Filtrace.9.9.9.nupkg"));
                    }
                }
            }

            return Task.FromResult(new ProcessResult(
                0,
                string.Empty,
                string.Empty,
                StandardOutputTruncated: truncatedOutput,
                StandardErrorTruncated: truncatedOutput,
                ExecutionTimedOut: false));
        }
    }

    private sealed class ThrowingWriteTextWriter : StringWriter
    {
        public override void Write(string? value)
        {
            throw new IOException("Injected output write failure.");
        }

        public override void WriteLine(string? value)
        {
        }
    }

    private sealed class ThrowingWriteLineTextWriter : StringWriter
    {
        public override void WriteLine(string? value)
        {
            throw new IOException("Injected cleanup warning write failure.");
        }
    }
}