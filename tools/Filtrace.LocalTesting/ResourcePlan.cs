// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Defines canonical target, shared git-state, tool, backup, MCP, and skill paths for one local-testing operation.
/// </summary>
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
        SkillBackupPath = Path.Join(ArtifactsDirectory, "skill-baseline");
        McpConfigurationPath = Path.Join(targetRoot, ".vscode", "mcp.json");
        SkillDestination = Path.Join(targetRoot, ".agents", "skills", "filtrace");
        string agents = Path.Join(targetRoot, ".agents");
        SkillStagingPath = Path.Join(agents, ".filtrace-skill-staging");
        SkillRetiredPath = Path.Join(agents, ".filtrace-skill-retired");
    }

    /// <summary>
    ///  Gets the canonical target worktree root.
    /// </summary>
    public string TargetRoot { get; }

    /// <summary>
    ///  Gets the canonical git directory shared by worktrees that coordinate local-testing state.
    /// </summary>
    public string GitDirectory { get; }

    /// <summary>
    ///  Gets the directory containing durable state and isolated installation artifacts.
    /// </summary>
    public string StateRoot { get; }

    /// <summary>
    ///  Gets the durable recovery-state JSON path.
    /// </summary>
    public string StatePath { get; }

    /// <summary>
    ///  Gets the shared file-lock path used to serialize target mutations.
    /// </summary>
    public string LockPath { get; }

    /// <summary>
    ///  Gets the isolated <c>dotnet tool</c> installation directory.
    /// </summary>
    public string CliDirectory { get; }

    /// <summary>
    ///  Gets the directory containing restorable baseline artifacts.
    /// </summary>
    public string ArtifactsDirectory { get; }

    /// <summary>
    ///  Gets the verified backup path for a pre-existing Filtrace skill.
    /// </summary>
    public string SkillBackupPath { get; }

    /// <summary>
    ///  Gets the target worktree's VS Code MCP configuration path.
    /// </summary>
    public string McpConfigurationPath { get; }

    /// <summary>
    ///  Gets the target worktree's installed Filtrace skill directory.
    /// </summary>
    public string SkillDestination { get; }

    /// <summary>
    ///  Gets the fixed hidden path used to stage a replacement skill.
    /// </summary>
    public string SkillStagingPath { get; }

    /// <summary>
    ///  Gets the fixed hidden path used to retire the prior skill during replacement.
    /// </summary>
    public string SkillRetiredPath { get; }

    /// <summary>
    ///  Canonicalizes repository paths and derives every resource managed by local testing.
    /// </summary>
    /// <param name="targetRoot">The target worktree root.</param>
    /// <param name="gitDirectory">The shared or worktree-specific git directory used for state.</param>
    /// <returns>A normalized immutable path plan.</returns>
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
