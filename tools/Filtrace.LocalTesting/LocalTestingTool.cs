// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Parses the internal wrapper protocol and invokes the local-testing engine.
/// </summary>
internal sealed class LocalTestingTool
{
    private const int MaxLegacyRepositoryDirectories = 256;
    private const string Usage = """
        Usage: Filtrace.LocalTesting --action <Install|Restore> --target-repository <path>
          --configuration <Debug|Release> --source-checkout <path>
          --dotnet-path <path> --git-path <path>
        """;

    private static readonly TimeSpan s_gitTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] s_gitLocalEnvironmentVariables =
    [
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_CEILING_DIRECTORIES",
        "GIT_CONFIG",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
        "GIT_OBJECT_DIRECTORY",
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_IMPLICIT_WORK_TREE",
        "GIT_GRAFT_FILE",
        "GIT_INDEX_FILE",
        "GIT_NO_REPLACE_OBJECTS",
        "GIT_REPLACE_REF_BASE",
        "GIT_PREFIX",
        "GIT_SHALLOW_FILE",
        "GIT_COMMON_DIR",
        "GIT_CONFIG_SYSTEM",
        "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_NOSYSTEM"
    ];

    private readonly IProcessRunner _processRunner;
    private readonly SourceArtifactPreparer _artifactPreparer;
    private readonly Func<ResourcePlan, LocalTestingInstallInputs, LocalTestingState> _install;
    private readonly Action<ResourcePlan> _restore;
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;

    /// <summary>
    ///  Creates a tool backed by real bounded processes and the concrete coordinator.
    /// </summary>
    public LocalTestingTool()
        : this(new BoundedProcessRunner(), Console.Out, Console.Error)
    {
    }

    /// <summary>
    ///  Creates a tool with testable process and stream dependencies.
    /// </summary>
    /// <param name="processRunner">The bounded child-process boundary.</param>
    /// <param name="standardOutput">The process standard output stream.</param>
    /// <param name="standardError">The process standard error stream.</param>
    /// <param name="install">An optional coordinator install operation.</param>
    /// <param name="restore">An optional coordinator restore operation.</param>
    /// <param name="deleteOperationDirectory">An optional private-operation cleanup operation.</param>
    internal LocalTestingTool(
        IProcessRunner processRunner,
        TextWriter standardOutput,
        TextWriter standardError,
        Func<ResourcePlan, LocalTestingInstallInputs, LocalTestingState>? install = null,
        Action<ResourcePlan>? restore = null,
        Action<string>? deleteOperationDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        _processRunner = processRunner;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _install = install ?? Install;
        _restore = restore ?? Restore;
        _artifactPreparer = new(
            processRunner,
            standardOutput,
            standardError,
            deleteOperationDirectory);
    }

    /// <summary>
    ///  Runs one Install or Restore request.
    /// </summary>
    /// <param name="arguments">The internal structured arguments supplied by the wrapper.</param>
    /// <returns>Zero on success, two for usage errors, or one for an operation failure.</returns>
    public async Task<int> RunAsync(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length is 1
            && arguments[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
        {
            _standardOutput.WriteLine(Usage);
            return 0;
        }

        if (!TryParse(arguments, out Dictionary<string, string> options, out string? error))
        {
            _standardError.WriteLine(error);
            _standardError.WriteLine(Usage);
            return 2;
        }

        try
        {
            (string targetRoot, string gitDirectory) = await ResolveRepositoryAsync(
                options["--target-repository"],
                options["--git-path"]);

            ResourcePlan plan = ResourcePlan.Create(targetRoot, gitDirectory);
            if (options["--action"].Equals("Restore", StringComparison.OrdinalIgnoreCase))
            {
                ThrowIfLegacyStateExists(options.GetValueOrDefault("--source-checkout"));
                _restore(plan);
                _standardOutput.WriteLine($"Restored local Filtrace resources in '{targetRoot}'.");
                return 0;
            }

            (string sourceRoot, string sourceGitDirectory) = await ResolveRepositoryAsync(
                options["--source-checkout"],
                options["--git-path"]);

            ThrowIfLegacyStateExists(sourceRoot);
            PreparedInstallInputs prepared = await _artifactPreparer.PrepareAsync(
                sourceRoot,
                sourceGitDirectory,
                options["--configuration"],
                options["--dotnet-path"]);

            LocalTestingState state;
            bool installSucceeded = false;
            try
            {
                state = _install(plan, prepared.Inputs);
                installSucceeded = true;
            }
            finally
            {
                prepared.Dispose();
                if (prepared.CleanupFailure is not null)
                {
                    TryWriteCleanupWarning(prepared, targetRoot, installSucceeded);
                }
            }

            _standardOutput.WriteLine(
                $"Local Filtrace {state.Cli!.PackageVersion} is active in '{targetRoot}'.");

            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _standardError.WriteLine($"Local Filtrace {options["--action"]} failed: {exception.Message}");
            return 1;
        }
    }

    private static LocalTestingState Install(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs)
    {
        return new LocalTestingCoordinator().Install(plan, inputs);
    }

    private static void Restore(ResourcePlan plan)
    {
        new LocalTestingCoordinator().Restore(plan);
    }

    private static void ThrowIfLegacyStateExists(string? sourceCheckout)
    {
        if (string.IsNullOrWhiteSpace(sourceCheckout) || !Directory.Exists(sourceCheckout))
        {
            return;
        }

        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceCheckout));
        string legacyRoot = Path.Join(source, "artifacts", "local-testing");
        string? legacyPath = FindLegacyWorkspace(legacyRoot);
        if (legacyPath is null)
        {
            string repositoriesPath = Path.Join(legacyRoot, "repositories");
            DirectoryInfo repositories = new(repositoriesPath);
            repositories.Refresh();
            if (repositories.Exists
                && repositories.LinkTarget is null
                && (repositories.Attributes & FileAttributes.ReparsePoint) is 0)
            {
                int inspected = 0;
                foreach (DirectoryInfo repository in repositories.EnumerateDirectories())
                {
                    if (++inspected > MaxLegacyRepositoryDirectories)
                    {
                        throw new InvalidDataException(
                            $"Legacy PR #94 repository-state discovery exceeded "
                                + $"{MaxLegacyRepositoryDirectories} directories under '{repositoriesPath}'. "
                                + "Inspect and restore the old local-testing state manually before retrying.");
                    }

                    repository.Refresh();
                    if (repository.LinkTarget is not null
                        || (repository.Attributes & FileAttributes.ReparsePoint) is not 0)
                    {
                        continue;
                    }

                    legacyPath = FindLegacyWorkspace(repository.FullName);
                    if (legacyPath is not null)
                    {
                        break;
                    }
                }
            }
        }

        if (legacyPath is not null)
        {
            throw new InvalidOperationException(
                $"Legacy PR #94 local-testing state exists at '{legacyPath}'. "
                    + "Restore it from the exact PR #94 Filtrace checkout and -StatePath that created "
                    + "this state before using the replacement command.");
        }
    }

    private static string? FindLegacyWorkspace(string directory)
    {
        string[] names = ["state.json", "state.json.workspace", "direct.workspace"];
        foreach (string name in names)
        {
            string path = Path.Join(directory, name);
            if (File.Exists(path) || Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private void TryWriteCleanupWarning(
        PreparedInstallInputs prepared,
        string targetRoot,
        bool installSucceeded)
    {
        string outcome = installSucceeded
            ? $"Local Filtrace is active in '{targetRoot}', but"
            : "Local Filtrace installation did not complete, and";

        try
        {
            _standardError.WriteLine(
                $"Warning: {outcome} private operation cleanup failed for "
                    + $"'{prepared.OperationDirectory}': {prepared.CleanupFailure!.Message} "
                    + "The retained operation blocks another preparation. "
                    + "The package was not uploaded to a feed.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private async Task<(string Root, string GitDirectory)> ResolveRepositoryAsync(
        string path,
        string gitPath)
    {
        ProcessResult result = await _processRunner.RunAsync(new(
            gitPath,
            ["-C", path, "rev-parse", "--show-toplevel", "--absolute-git-dir"],
            Environment.CurrentDirectory,
            s_gitTimeout,
            CreateGitEnvironmentOverrides()));

        if (result.ExecutionTimedOut)
        {
            throw new TimeoutException($"Git repository discovery exceeded 30 seconds for '{path}'.");
        }

        if (result.OutputCaptureIncomplete)
        {
            throw new InvalidOperationException(
                $"Git repository discovery did not finish capturing output for '{path}' "
                    + $"from root process {result.RootProcessId?.ToString() ?? "unknown"}.");
        }

        if (result.ExitCode is not 0)
        {
            string detail = result.StandardError.Trim();
            throw new InvalidOperationException(
                $"Git repository discovery failed for '{path}' with exit code "
                    + $"{result.ExitCode?.ToString() ?? "unknown"}: {detail}");
        }

        string[] lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length is not 2)
        {
            throw new InvalidDataException(
                $"Git repository discovery returned {lines.Length} paths for '{path}'; expected two.");
        }

        return (lines[0], lines[1]);
    }

    private static IReadOnlyDictionary<string, string?> CreateGitEnvironmentOverrides()
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in s_gitLocalEnvironmentVariables)
        {
            environment.Add(name, value: null);
        }

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = (string)entry.Key;
            if (name.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase))
            {
                environment[name] = null;
            }
        }

        return environment;
    }

    private static bool TryParse(
        string[] arguments,
        out Dictionary<string, string> options,
        out string? error)
    {
        options = new(StringComparer.OrdinalIgnoreCase);
        error = null;
        HashSet<string> supported = new(StringComparer.OrdinalIgnoreCase)
        {
            "--action",
            "--target-repository",
            "--configuration",
            "--source-checkout",
            "--dotnet-path",
            "--git-path"
        };

        for (int index = 0; index < arguments.Length; index += 2)
        {
            string name = arguments[index];
            if (!supported.Contains(name))
            {
                error = $"Unknown option '{name}'.";
                return false;
            }

            if (index + 1 >= arguments.Length)
            {
                error = $"Option '{name}' requires a value.";
                return false;
            }

            if (!options.TryAdd(name, arguments[index + 1]))
            {
                error = $"Option '{name}' was specified more than once.";
                return false;
            }
        }

        string[] required =
        [
            "--action",
            "--target-repository",
            "--configuration",
            "--git-path"
        ];

        foreach (string requiredOption in required)
        {
            if (!options.ContainsKey(requiredOption))
            {
                error = $"Missing required option '{requiredOption}'.";
                return false;
            }
        }

        if (!TryNormalizeOption(options, "--action", "Install", "Restore"))
        {
            error = "Action must be Install or Restore.";
            return false;
        }

        if (!TryNormalizeOption(options, "--configuration", "Debug", "Release"))
        {
            error = "Configuration must be Debug or Release.";
            return false;
        }

        if (options["--action"].Equals("Install", StringComparison.Ordinal)
            && (!options.ContainsKey("--source-checkout")
                || !options.ContainsKey("--dotnet-path")))
        {
            error = "Install requires --source-checkout and --dotnet-path.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeOption(
        Dictionary<string, string> options,
        string optionName,
        string firstValue,
        string secondValue)
    {
        string value = options[optionName];
        if (value.Equals(firstValue, StringComparison.OrdinalIgnoreCase))
        {
            options[optionName] = firstValue;
            return true;
        }

        if (value.Equals(secondValue, StringComparison.OrdinalIgnoreCase))
        {
            options[optionName] = secondValue;
            return true;
        }

        return false;
    }
}