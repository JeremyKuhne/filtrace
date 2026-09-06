// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingToolTests
{
    [TestMethod]
    [DataRow("--unknown", "Unknown option '--unknown'.")]
    [DataRow("--action", "Option '--action' requires a value.")]
    [DataRow("--action|Install|--action|Restore", "Option '--action' was specified more than once.")]
    [DataRow("--action|Install", "Missing required option '--target-repository'.")]
    public async Task RunAsync_InvalidArguments_FailBeforeStartingAProcess(
        string joinedArguments,
        string expectedError)
    {
        ScriptedProcessRunner runner = new();
        StringWriter error = new();
        LocalTestingTool tool = new(runner, TextWriter.Null, error);

        int exitCode = await tool.RunAsync(joinedArguments.Split('|'));

        exitCode.Should().Be(2);
        runner.Invocations.Should().BeEmpty();
        error.ToString().Should().Contain(expectedError).And.Contain("Usage:");
    }

    [TestMethod]
    [DataRow("install", "debug")]
    [DataRow("INSTALL", "RELEASE")]
    [DataRow("restore", "DEBUG")]
    [DataRow("RESTORE", "release")]
    public async Task RunAsync_MixedCaseActionAndConfiguration_AreAccepted(
        string action,
        string configuration)
    {
        ScriptedProcessRunner runner = new();
        LocalTestingTool tool = new(runner, TextWriter.Null, TextWriter.Null);

        int exitCode = await tool.RunAsync(
        [
            "--action", action,
            "--target-repository", "target",
            "--configuration", configuration,
            "--source-checkout", "source",
            "--dotnet-path", "dotnet",
            "--git-path", "git"
        ]);

        exitCode.Should().Be(1);
        runner.Invocations.Should().ContainSingle();
    }

    [TestMethod]
    public async Task RunAsync_Help_WritesUsageWithoutStartingAProcess()
    {
        ScriptedProcessRunner runner = new();
        StringWriter output = new();
        LocalTestingTool tool = new(runner, output, TextWriter.Null);

        int exitCode = await tool.RunAsync(["--help"]);

        exitCode.Should().Be(0);
        runner.Invocations.Should().BeEmpty();
        output.ToString().Should().Contain("Usage: Filtrace.LocalTesting");
    }

    [TestMethod]
    public async Task RunAsync_Help_SubprocessSeparatesOutputAndExitCode()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        ProcessResult result = await new BoundedProcessRunner().RunAsync(new(
            dotnet,
            [typeof(LocalTestingTool).Assembly.Location, "--help"],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(20)));

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("Usage: Filtrace.LocalTesting");
        result.StandardError.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("Install", "state.json")]
    [DataRow("Install", "state.json.workspace")]
    [DataRow("Install", "repositories/repo/direct.workspace")]
    [DataRow("Restore", "state.json")]
    [DataRow("Restore", "state.json.workspace")]
    [DataRow("Restore", "repositories/repo/direct.workspace")]
    public async Task RunAsync_LegacyDefaultState_FailsBeforeConsumerMutation(
        string action,
        string relativeLegacyPath)
    {
        using TemporaryDirectory directory = new();
        string target = Path.Join(directory.Path, "target");
        string source = Path.Join(directory.Path, "source");
        string targetGit = Path.Join(target, ".git");
        string sourceGit = Path.Join(source, ".git");
        Directory.CreateDirectory(targetGit);
        Directory.CreateDirectory(sourceGit);
        string legacyPath = Path.Join(
            source,
            "artifacts",
            "local-testing",
            relativeLegacyPath.Replace('/', Path.DirectorySeparatorChar));

        if (Path.HasExtension(legacyPath) && !legacyPath.EndsWith(".workspace", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, "legacy");
        }
        else
        {
            Directory.CreateDirectory(legacyPath);
        }

        string marker = Path.Join(target, "consumer.txt");
        File.WriteAllText(marker, "unchanged");
        ScriptedProcessRunner runner = new(invocation => RepositoryResult(
            invocation.Arguments[1].Equals(target, StringComparison.Ordinal) ? target : source,
            invocation.Arguments[1].Equals(target, StringComparison.Ordinal) ? targetGit : sourceGit));

        LocalTestingTool tool = new(
            runner,
            TextWriter.Null,
            TextWriter.Null,
            install: static (_, _) => throw new InvalidOperationException("Install must not run."),
            restore: static _ => throw new InvalidOperationException("Restore must not run."));

        int exitCode = await tool.RunAsync(CreateArguments(action, target, source));

        exitCode.Should().Be(1);
        File.ReadAllText(marker).Should().Be("unchanged");
        Directory.Exists(Path.Join(targetGit, "filtrace-local-testing")).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(data: false)]
    [DataRow(data: true)]
    public async Task RunAsync_Restore_DoesNotRequireOrPrepareSourceCheckout(bool includeMissingSource)
    {
        using TemporaryDirectory directory = new();
        string target = Path.Join(directory.Path, "target");
        string targetGit = Path.Join(target, ".git");
        Directory.CreateDirectory(targetGit);
        ScriptedProcessRunner runner = new(_ => RepositoryResult(target, targetGit));
        bool restored = false;
        LocalTestingTool tool = new(
            runner,
            TextWriter.Null,
            TextWriter.Null,
            restore: _ => restored = true);

        List<string> arguments =
        [
            "--action", "Restore",
            "--target-repository", target,
            "--configuration", "Debug",
            "--git-path", "git"
        ];

        if (includeMissingSource)
        {
            arguments.AddRange(["--source-checkout", Path.Join(directory.Path, "missing-source")]);
        }

        int exitCode = await tool.RunAsync([.. arguments]);

        exitCode.Should().Be(0);
        restored.Should().BeTrue();
        runner.Invocations.Should().ContainSingle();
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task RunAsync_AmbientGitState_ResolvesExplicitRepositoryWithoutMutatingEnvironment()
    {
        using TemporaryDirectory directory = new();
        string repositoryA = Path.Join(directory.Path, "repository-a");
        string repositoryB = Path.Join(directory.Path, "repository-b");
        Directory.CreateDirectory(repositoryA);
        Directory.CreateDirectory(repositoryB);
        await RunGitAsync(repositoryA, "init");
        await RunGitAsync(repositoryB, "init");
        Dictionary<string, string?> ambient = new(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_DIR"] = Path.Join(repositoryA, ".git"),
            ["GIT_WORK_TREE"] = repositoryA,
            ["GIT_COMMON_DIR"] = Path.Join(repositoryA, ".git"),
            ["GIT_INDEX_FILE"] = Path.Join(repositoryA, ".git", "index"),
            ["GIT_CEILING_DIRECTORIES"] = repositoryB,
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.worktree",
            ["GIT_CONFIG_VALUE_0"] = repositoryA
        };

        Dictionary<string, string?> previous = ambient.Keys.ToDictionary(
            static name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach ((string name, string? value) in ambient)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            bool restored = false;
            LocalTestingTool tool = new(
                new BoundedProcessRunner(),
                TextWriter.Null,
                TextWriter.Null,
                restore: plan =>
                {
                    plan.TargetRoot.Should().Be(Path.GetFullPath(repositoryB));
                    restored = true;
                });

            int exitCode = await tool.RunAsync(CreateArguments("Restore", repositoryB, source: null));

            exitCode.Should().Be(0);
            restored.Should().BeTrue();
            foreach ((string name, string? value) in ambient)
            {
                Environment.GetEnvironmentVariable(name).Should().Be(value);
            }
        }
        finally
        {
            foreach ((string name, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_PreparedInputsRemainLockedThroughInstallAndCleanupWarningFailureIsNonfatal()
    {
        using TemporaryDirectory directory = new();
        PreparationProcessRunner runner = new(directory.Path);
        (int exitCode, ThrowOnceTextWriter error) = await RunWithCleanupFailureAsync(
            runner,
            (_, inputs) =>
            {
                Action competingPreparation = () => SourcePreparationOperation.Acquire(
                    runner.SourceGitDirectory,
                    static _ =>
                    {
                    });

                competingPreparation.Should().Throw<InvalidOperationException>()
                    .WithMessage("*Another source preparation may still own its artifacts*");

                return CreateActiveState(inputs);
            });

        exitCode.Should().Be(0);
        error.ThrowCount.Should().Be(1);
    }

    [TestMethod]
    public async Task RunAsync_PrimaryInstallFailureSurvivesCleanupWarningWriterFailure()
    {
        using TemporaryDirectory directory = new();
        PreparationProcessRunner runner = new(directory.Path);
        (int exitCode, ThrowOnceTextWriter error) = await RunWithCleanupFailureAsync(
            runner,
            static (_, _) => throw new InvalidOperationException("Injected primary failure."));

        exitCode.Should().Be(1);
        error.ThrowCount.Should().Be(1);
        error.WrittenText.Should().Contain("Injected primary failure.");
    }

    private static async Task<(int ExitCode, ThrowOnceTextWriter Error)> RunWithCleanupFailureAsync(
        PreparationProcessRunner runner,
        Func<ResourcePlan, LocalTestingInstallInputs, LocalTestingState> install)
    {
        string? retainedOperation = null;
        ThrowOnceTextWriter error = new();
        LocalTestingTool tool = new(
            runner,
            TextWriter.Null,
            error,
            install,
            deleteOperationDirectory: path =>
            {
                retainedOperation = path;
                throw new IOException("Injected cleanup failure.");
            });

        try
        {
            int exitCode = await tool.RunAsync(CreateArguments("Install", runner.Target, runner.Source));
            return (exitCode, error);
        }
        finally
        {
            if (retainedOperation is not null && Directory.Exists(retainedOperation))
            {
                Directory.Delete(retainedOperation, recursive: true);
            }
        }
    }

    private static string[] CreateArguments(string action, string target, string? source)
    {
        List<string> arguments =
        [
            "--action", action,
            "--target-repository", target,
            "--configuration", "Debug",
            "--dotnet-path", "dotnet",
            "--git-path", "git"
        ];

        if (source is not null)
        {
            arguments.AddRange(["--source-checkout", source]);
        }

        return [.. arguments];
    }

    private static ProcessResult RepositoryResult(string root, string gitDirectory)
    {
        return Success($"{root}{Environment.NewLine}{gitDirectory}{Environment.NewLine}");
    }

    private static ProcessResult Success(string output)
    {
        return new(
            0,
            output,
            string.Empty,
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            ExecutionTimedOut: false);
    }

    private static LocalTestingState CreateActiveState(LocalTestingInstallInputs inputs)
    {
        return TestState.Create(LocalTestingStatus.Active) with
        {
            SourceCheckout = inputs.SourceCheckout,
            Cli = new()
            {
                PackageVersion = "1.2.3",
                PackageSha256 = TestState.Hash
            }
        };
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        ProcessResult result = await new BoundedProcessRunner().RunAsync(new(
            "git",
            arguments,
            workingDirectory,
            TimeSpan.FromSeconds(20)));

        result.ExitCode.Should().Be(0, result.StandardError);
    }

    private sealed class ScriptedProcessRunner(
        Func<ProcessInvocation, ProcessResult>? script = null) : IProcessRunner
    {
        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessInvocation invocation)
        {
            Invocations.Add(invocation);
            return Task.FromResult(script?.Invoke(invocation) ?? new ProcessResult(
                9,
                string.Empty,
                "Expected test failure.",
                StandardOutputTruncated: false,
                StandardErrorTruncated: false,
                ExecutionTimedOut: false));
        }
    }

    private sealed class PreparationProcessRunner : IProcessRunner
    {
        public PreparationProcessRunner(string root)
        {
            Target = Path.Join(root, "target");
            Source = Path.Join(root, "source");
            TargetGitDirectory = Path.Join(Target, ".git");
            SourceGitDirectory = Path.Join(Source, ".git");
            Directory.CreateDirectory(TargetGitDirectory);
            Directory.CreateDirectory(SourceGitDirectory);
            Directory.CreateDirectory(Path.Join(Source, ".agents", "skills", "filtrace"));
            File.WriteAllText(Path.Join(Source, ".agents", "skills", "filtrace", "SKILL.md"), "skill");
        }

        public string Target { get; }

        public string Source { get; }

        public string TargetGitDirectory { get; }

        public string SourceGitDirectory { get; }

        public Task<ProcessResult> RunAsync(ProcessInvocation invocation)
        {
            if (invocation.Arguments[0].Equals("-C", StringComparison.Ordinal))
            {
                bool target = invocation.Arguments[1].Equals(Target, StringComparison.Ordinal);
                return Task.FromResult(RepositoryResult(
                    target ? Target : Source,
                    target ? TargetGitDirectory : SourceGitDirectory));
            }

            if (invocation.Arguments[0].Equals("build", StringComparison.Ordinal)
                && invocation.Arguments[1].Contains("Filtrace.Mcp", StringComparison.Ordinal))
            {
                string output = Path.Join(Source, "src", "Filtrace.Mcp", "bin", "Debug", "net10.0");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Join(output, "Filtrace.Mcp.dll"), "mcp");
            }

            if (invocation.Arguments[0].Equals("pack", StringComparison.Ordinal))
            {
                int outputIndex = invocation.Arguments.ToList().IndexOf("--output");
                LocalTestingInstallTestData.CreateMetadataPackage(invocation.Arguments[outputIndex + 1]);
            }

            return Task.FromResult(Success(string.Empty));
        }
    }

    private sealed class ThrowOnceTextWriter : StringWriter
    {
        private bool _thrown;

        public int ThrowCount { get; private set; }

        public string WrittenText => ToString();

        public override void WriteLine(string? value)
        {
            if (!_thrown)
            {
                _thrown = true;
                ThrowCount++;
                throw new IOException("Injected writer failure.");
            }

            base.WriteLine(value);
        }
    }
}