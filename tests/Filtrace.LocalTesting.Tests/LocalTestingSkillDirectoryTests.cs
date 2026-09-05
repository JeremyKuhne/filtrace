// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed partial class LocalTestingSkillDirectoryTests
{
    [TestMethod]
    public void Publish_MissingDestination_CopiesSourceAndEmptyDirectory()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "local skill");
        Directory.CreateDirectory(Path.Join(source, "empty"));

        new LocalTestingSkillDirectory().Publish(plan, source);

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("local skill");

        Directory.Exists(Path.Join(plan.SkillDestination, "empty")).Should().BeTrue();
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_ExistingDestination_ReplacesContentAndPreservesOverlay()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "local skill");
        File.WriteAllText(Path.Join(source, "overlay.md"), "source overlay");
        CreateDestination(plan, "prior skill", "consumer overlay");
        File.WriteAllText(Path.Join(plan.SkillDestination, "prior-only.txt"), "remove");

        new LocalTestingSkillDirectory().Publish(plan, source);

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("local skill");

        File.ReadAllText(Path.Join(plan.SkillDestination, "overlay.md"))
            .Should().Be("consumer overlay");

        File.Exists(Path.Join(plan.SkillDestination, "prior-only.txt")).Should().BeFalse();
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_RepeatedWithNewSource_PreservesEditedOverlay()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string first = CreateSkill(directory.Path, "first", "first skill");
        string second = CreateSkill(directory.Path, "second", "second skill");
        LocalTestingSkillDirectory publisher = new();
        publisher.Publish(plan, first);
        File.WriteAllText(Path.Join(plan.SkillDestination, "overlay.md"), "edited overlay");

        publisher.Publish(plan, second);

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("second skill");

        File.ReadAllText(Path.Join(plan.SkillDestination, "overlay.md"))
            .Should().Be("edited overlay");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_SourceEqualsDestination_PreservesSkillAndOverlay()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        CreateDestination(plan, "source skill", "consumer overlay");

        new LocalTestingSkillDirectory().Publish(plan, plan.SkillDestination);

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("source skill");

        File.ReadAllText(Path.Join(plan.SkillDestination, "overlay.md"))
            .Should().Be("consumer overlay");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Restore_ExistingBaseline_RestoresExactBackup()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        CreateDestination(plan, "prior skill", "prior overlay");
        Directory.CreateDirectory(Path.Join(plan.SkillDestination, "empty"));
        SkillBaseline baseline = CaptureSkillBaseline(plan);
        string source = CreateSkill(directory.Path, "source", "local skill");
        LocalTestingSkillDirectory publisher = new();
        publisher.Publish(plan, source);
        File.WriteAllText(Path.Join(plan.SkillDestination, "overlay.md"), "active edit");
        File.WriteAllText(Path.Join(plan.SkillDestination, "active-only.txt"), "remove");

        publisher.Restore(plan, baseline);

        DirectorySnapshot restored = DirectorySnapshot.Create(
            plan.SkillDestination,
            "Skill destination",
            LocalTestingBaselineCapturer.MaxSkillEntries,
            LocalTestingBaselineCapturer.MaxSkillBytes);

        restored.Fingerprint.Should().Be(baseline.BackupSha256);
        File.ReadAllText(Path.Join(plan.SkillDestination, "overlay.md"))
            .Should().Be("prior overlay");

        File.Exists(Path.Join(plan.SkillDestination, "active-only.txt")).Should().BeFalse();
        Directory.Exists(Path.Join(plan.SkillDestination, "empty")).Should().BeTrue();
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Restore_AbsentBaseline_RemovesDestinationIdempotently()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "local skill");
        SkillBaseline baseline = new();
        LocalTestingSkillDirectory publisher = new();
        publisher.Publish(plan, source);

        publisher.Restore(plan, baseline);
        publisher.Restore(plan, baseline);

        Directory.Exists(plan.SkillDestination).Should().BeFalse();
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_InterruptedBeforeStagingMove_RecoversThenPublishes()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "latest skill");
        CreateDestination(plan, "prior skill", "overlay");
        Directory.Move(plan.SkillDestination, plan.SkillRetiredPath);
        CreateSkillAt(plan.SkillStagingPath, "abandoned staging");

        new LocalTestingSkillDirectory().Publish(plan, source);

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("latest skill");

        File.ReadAllText(Path.Join(plan.SkillDestination, "overlay.md")).Should().Be("overlay");
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_InterruptedAfterStagingMove_RemovesRetiredThenPublishes()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "latest skill");
        CreateDestination(plan, "interrupted new skill", "overlay");
        CreateSkillAt(plan.SkillRetiredPath, "prior skill");

        new LocalTestingSkillDirectory().Publish(plan, source);

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("latest skill");

        File.ReadAllText(Path.Join(plan.SkillDestination, "overlay.md")).Should().Be("overlay");
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Restore_InterruptedRemoval_RecoversThenCompletes()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        CreateSkillAt(plan.SkillRetiredPath, "active skill");

        new LocalTestingSkillDirectory().Restore(plan, new());

        Directory.Exists(plan.SkillDestination).Should().BeFalse();
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Restore_ExistingBaseline_RepeatedRestoreConverges()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        CreateDestination(plan, "prior skill", overlay: null);
        SkillBaseline baseline = CaptureSkillBaseline(plan);
        string source = CreateSkill(directory.Path, "source", "local skill");
        LocalTestingSkillDirectory publisher = new();
        publisher.Publish(plan, source);

        publisher.Restore(plan, baseline);
        string firstFingerprint = Snapshot(plan.SkillDestination).Fingerprint;
        publisher.Restore(plan, baseline);

        Snapshot(plan.SkillDestination).Fingerprint.Should().Be(firstFingerprint);
        AssertNoOperationDirectories(plan);
    }

    private static ResourcePlan CreatePlan(TemporaryDirectory directory)
    {
        string targetRoot = Path.Join(directory.Path, "target");
        string gitDirectory = Path.Join(targetRoot, ".git");
        Directory.CreateDirectory(gitDirectory);
        ResourcePlan plan = ResourcePlan.Create(targetRoot, gitDirectory);
        Directory.CreateDirectory(plan.ArtifactsDirectory);
        return plan;
    }

    private static string CreateSkill(string root, string name, string contents)
    {
        string path = Path.Join(root, name);
        CreateSkillAt(path, contents);
        return path;
    }

    private static void CreateSkillAt(string path, string contents)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Join(path, "SKILL.md"), contents);
    }

    private static void CreateDestination(ResourcePlan plan, string contents, string? overlay)
    {
        CreateSkillAt(plan.SkillDestination, contents);
        if (overlay is not null)
        {
            File.WriteAllText(Path.Join(plan.SkillDestination, "overlay.md"), overlay);
        }
    }

    private static SkillBaseline CaptureSkillBaseline(ResourcePlan plan)
    {
        return new LocalTestingBaselineCapturer().Capture(plan).Skill;
    }

    private static DirectorySnapshot Snapshot(string path)
    {
        return DirectorySnapshot.Create(
            path,
            "Skill destination",
            LocalTestingBaselineCapturer.MaxSkillEntries,
            LocalTestingBaselineCapturer.MaxSkillBytes);
    }

    private static void AssertNoOperationDirectories(ResourcePlan plan)
    {
        Directory.Exists(plan.SkillStagingPath).Should().BeFalse();
        Directory.Exists(plan.SkillRetiredPath).Should().BeFalse();
    }
}
