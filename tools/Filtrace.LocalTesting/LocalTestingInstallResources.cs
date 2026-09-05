// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Connects coordinator operations to the concrete baseline, CLI, MCP, and skill mutators.
/// </summary>
internal sealed class LocalTestingInstallResources : ILocalTestingInstallResources
{
    private readonly LocalTestingBaselineCapturer _baselineCapturer = new();
    private readonly LocalTestingCliInstaller _cliInstaller = new();
    private readonly LocalTestingMcpConfiguration _mcpConfiguration = new();
    private readonly LocalTestingSkillDirectory _skillDirectory = new();

    /// <inheritdoc />
    public LocalTestingBaseline CaptureBaseline(ResourcePlan plan)
    {
        return _baselineCapturer.Capture(plan);
    }

    /// <inheritdoc />
    public void ValidateBaseline(ResourcePlan plan, LocalTestingBaseline baseline)
    {
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.McpConfigurationPath);
        LocalTestingStateStore.ValidateMcpBaseline(baseline.Mcp);
        _ = LocalTestingSkillDirectory.ValidateBaseline(plan, baseline.Skill);
    }

    /// <inheritdoc />
    public CliInstallation InstallCli(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs,
        bool replaceExisting)
    {
        return replaceExisting
            ? _cliInstaller.InstallOrReplace(plan, inputs.CliPackagePath, inputs.DotnetPath)
            : _cliInstaller.InstallFresh(plan, inputs.CliPackagePath, inputs.DotnetPath);
    }

    /// <inheritdoc />
    public void PublishMcp(ResourcePlan plan, LocalTestingInstallInputs inputs)
    {
        _mcpConfiguration.Publish(plan, inputs.McpDllPath);
    }

    /// <inheritdoc />
    public void PublishSkill(ResourcePlan plan, LocalTestingInstallInputs inputs)
    {
        _skillDirectory.Publish(plan, inputs.SkillDirectory);
    }

    /// <inheritdoc />
    public void RestoreCli(ResourcePlan plan)
    {
        _cliInstaller.Restore(plan);
    }

    /// <inheritdoc />
    public void RestoreMcp(ResourcePlan plan, McpBaseline baseline)
    {
        _mcpConfiguration.Restore(plan, baseline);
    }

    /// <inheritdoc />
    public void RestoreSkill(ResourcePlan plan, SkillBaseline baseline)
    {
        _skillDirectory.Restore(plan, baseline);
    }

    /// <inheritdoc />
    public void RestoreCreatedDirectories(
        ResourcePlan plan,
        CreatedDirectoryBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseline);
        string vscode = Path.GetDirectoryName(plan.McpConfigurationPath)
            ?? throw new InvalidDataException("MCP configuration has no parent directory.");

        string agents = Path.Join(plan.TargetRoot, ".agents");
        string skills = Path.Join(agents, "skills");
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, vscode);
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, skills);
        if (baseline.Skills)
        {
            DeleteIfEmpty(skills);
        }

        if (baseline.Agents)
        {
            DeleteIfEmpty(agents);
        }

        if (baseline.Vscode)
        {
            DeleteIfEmpty(vscode);
        }
    }

    /// <inheritdoc />
    public void CleanupPrivateArtifacts(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.StateRoot);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.ArtifactsDirectory);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.SkillBackupPath);
        if (RegularFileGuard.Exists(plan.ArtifactsDirectory, "Local-testing artifacts path"))
        {
            throw new InvalidDataException(
                $"Local-testing artifacts path is a file, not a directory: '{plan.ArtifactsDirectory}'.");
        }

        if (Directory.Exists(plan.ArtifactsDirectory))
        {
            LocalTestingDirectory.DeleteTree(plan.ArtifactsDirectory);
        }
    }

    private static void DeleteIfEmpty(string path)
    {
        if (Directory.Exists(path)
            && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, recursive: false);
        }
    }
}
