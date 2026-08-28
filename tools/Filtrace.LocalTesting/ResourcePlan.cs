// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

internal sealed record ResourcePlan
{
    private const string StateDirectoryName = "filtrace-local-testing";

    private ResourcePlan(string targetRoot, string gitDirectory)
    {
        TargetRoot = targetRoot;
        GitDirectory = gitDirectory;
        StateRoot = Path.Join(gitDirectory, StateDirectoryName);
        StatePath = Path.Join(StateRoot, "state.json");
        LockPath = Path.Join(gitDirectory, $"{StateDirectoryName}.lock");
        CliDirectory = Path.Join(StateRoot, "tools");
        ArtifactsDirectory = Path.Join(StateRoot, "artifacts");
        McpConfigurationPath = Path.Join(targetRoot, ".vscode", "mcp.json");
        SkillDestination = Path.Join(targetRoot, ".agents", "skills", "filtrace");
    }

    public string TargetRoot { get; }

    public string GitDirectory { get; }

    public string StateRoot { get; }

    public string StatePath { get; }

    public string LockPath { get; }

    public string CliDirectory { get; }

    public string ArtifactsDirectory { get; }

    public string McpConfigurationPath { get; }

    public string SkillDestination { get; }

    public static ResourcePlan Create(string targetRoot, string gitDirectory)
    {
        return new(
            NormalizeDirectory(targetRoot, nameof(targetRoot)),
            NormalizeDirectory(gitDirectory, nameof(gitDirectory)));
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}