// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class ResourcePlanTests
{
    [TestMethod]
    public void Create_FixedPaths_DerivesRepositoryLocalPlan()
    {
        string targetRoot = Path.Join(Path.GetTempPath(), "consumer");
        string gitDirectory = Path.Join(targetRoot, ".git");

        ResourcePlan plan = ResourcePlan.Create(targetRoot, gitDirectory);

        plan.TargetRoot.Should().Be(Path.GetFullPath(targetRoot));
        plan.GitDirectory.Should().Be(Path.GetFullPath(gitDirectory));
        plan.StateRoot.Should().Be(Path.Join(gitDirectory, "filtrace-local-testing"));
        plan.StatePath.Should().Be(Path.Join(plan.StateRoot, "state.json"));
        plan.LockPath.Should().Be(Path.Join(gitDirectory, "filtrace-local-testing.lock"));
        plan.CliDirectory.Should().Be(Path.Join(plan.StateRoot, "tools"));
        plan.ArtifactsDirectory.Should().Be(Path.Join(plan.StateRoot, "artifacts"));
        plan.SkillBackupPath.Should().Be(Path.Join(plan.ArtifactsDirectory, "skill-baseline"));
        plan.McpConfigurationPath.Should().Be(Path.Join(targetRoot, ".vscode", "mcp.json"));
        plan.SkillDestination.Should().Be(
            Path.Join(targetRoot, ".agents", "skills", "filtrace"));
    }

    [TestMethod]
    public void Create_LinkedWorktrees_UseIndependentGitDirectories()
    {
        string common = Path.Join(Path.GetTempPath(), "repository", ".git", "worktrees");
        ResourcePlan first = ResourcePlan.Create(
            Path.Join(Path.GetTempPath(), "worktree-one"),
            Path.Join(common, "one"));
        ResourcePlan second = ResourcePlan.Create(
            Path.Join(Path.GetTempPath(), "worktree-two"),
            Path.Join(common, "two"));

        first.StatePath.Should().NotBe(second.StatePath);
        first.LockPath.Should().NotBe(second.LockPath);
    }

    [TestMethod]
    public void Create_TrailingSeparators_NormalizesPaths()
    {
        string targetRoot = Path.Join(Path.GetTempPath(), "consumer");
        string gitDirectory = Path.Join(targetRoot, ".git");

        ResourcePlan plan = ResourcePlan.Create(
            $"{targetRoot}{Path.DirectorySeparatorChar}",
            $"{gitDirectory}{Path.DirectorySeparatorChar}");

        plan.TargetRoot.Should().Be(Path.GetFullPath(targetRoot));
        plan.GitDirectory.Should().Be(Path.GetFullPath(gitDirectory));
    }
}
