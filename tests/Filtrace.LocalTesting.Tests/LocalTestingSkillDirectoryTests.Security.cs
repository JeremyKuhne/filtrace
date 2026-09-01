// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

public sealed partial class LocalTestingSkillDirectoryTests
{
    [TestMethod]
    public void Publish_NullPlan_Throws()
    {
        Action publish = () => new LocalTestingSkillDirectory().Publish(
            plan: null!,
            Path.GetFullPath("skill"));

        publish.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void Publish_NullSourceArgument_Throws()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);

        Action publish = () => new LocalTestingSkillDirectory().Publish(
            plan,
            sourceSkillDirectory: null!);

        publish.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Publish_EmptyOrWhitespaceSourceArgument_Throws()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);

        Action empty = () => new LocalTestingSkillDirectory().Publish(plan, string.Empty);
        Action whitespace = () => new LocalTestingSkillDirectory().Publish(plan, " ");

        empty.Should().Throw<ArgumentException>();
        whitespace.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Restore_NullArguments_Throw()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingSkillDirectory publisher = new();

        Action nullPlan = () => publisher.Restore(plan: null!, new());
        Action nullBaseline = () => publisher.Restore(plan, baseline: null!);

        nullPlan.Should().Throw<ArgumentNullException>();
        nullBaseline.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void Publish_RelativeSource_ThrowsBeforeTargetMutation()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, "relative-skill");

        publish.Should().Throw<ArgumentException>().WithMessage("*must be absolute*");
        Directory.Exists(plan.SkillDestination).Should().BeFalse();
    }

    [TestMethod]
    public void Publish_MissingSource_ThrowsBeforeTargetMutation()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = Path.Join(directory.Path, "missing");

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<DirectoryNotFoundException>();
        Directory.Exists(plan.SkillDestination).Should().BeFalse();
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_SourceFile_ThrowsBeforeTargetMutation()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = Path.Join(directory.Path, "source");
        File.WriteAllText(source, "not a directory");

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>().WithMessage("*must be a directory*");
        Directory.Exists(plan.SkillDestination).Should().BeFalse();
    }

    [TestMethod]
    public void Publish_LinkedSource_ThrowsWithoutReadingTarget()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string actual = CreateSkill(directory.Path, "actual", "skill");
        string source = Path.Join(directory.Path, "linked");
        TryCreateDirectoryLink(source, actual);

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>().WithMessage("*must not be a link*");
        Directory.Exists(plan.SkillDestination).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Publish_SourceOverlapsReservedPath_ThrowsBeforeRecovery(int variation)
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = variation switch
        {
            0 => plan.SkillStagingPath,
            1 => Path.Join(plan.SkillRetiredPath, "nested"),
            _ => Path.GetDirectoryName(plan.SkillStagingPath)!
        };

        CreateSkillAt(source, "source skill");
        if (variation is 2)
        {
            CreateSkillAt(plan.SkillStagingPath, "reserved staging");
        }

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>()
            .WithMessage("*must not overlap a reserved operation path*");

        File.ReadAllText(Path.Join(source, "SKILL.md")).Should().Be("source skill");
        if (variation is 2)
        {
            File.ReadAllText(Path.Join(plan.SkillStagingPath, "SKILL.md"))
                .Should().Be("reserved staging");
        }
    }

    [TestMethod]
    public void Publish_LinkInsideSource_ThrowsWithoutTargetMutation()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "skill");
        string external = Path.Join(directory.Path, "external.txt");
        File.WriteAllText(external, "external");
        TryCreateFileLink(Path.Join(source, "linked.txt"), external);

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>()
            .WithMessage("Filtrace skill source must not contain links*");

        Directory.Exists(plan.SkillDestination).Should().BeFalse();
    }

    [TestMethod]
    public void Publish_DestinationFile_ThrowsWithoutReplacingIt()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "skill");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.SkillDestination)!);
        File.WriteAllText(plan.SkillDestination, "consumer file");

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>().WithMessage("*must be a directory*");
        File.ReadAllText(plan.SkillDestination).Should().Be("consumer file");
    }

    [TestMethod]
    [Timeout(5_000)]
    public void Publish_FifoDestination_ThrowsWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "skill");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.SkillDestination)!);
        UnixTestFile.CreateFifo(plan.SkillDestination);

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>().WithMessage("*regular file*");
    }

    [TestMethod]
    public void Publish_SourceAtByteLimit_Succeeds()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = Path.Join(directory.Path, "source");
        Directory.CreateDirectory(source);
        using (FileStream stream = File.Create(Path.Join(source, "SKILL.md")))
        {
            stream.SetLength(LocalTestingBaselineCapturer.MaxSkillBytes);
        }

        new LocalTestingSkillDirectory().Publish(plan, source);

        new FileInfo(Path.Join(plan.SkillDestination, "SKILL.md")).Length
            .Should().Be(LocalTestingBaselineCapturer.MaxSkillBytes);

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_SourceOverByteLimit_LeavesDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = Path.Join(directory.Path, "source");
        Directory.CreateDirectory(source);
        using (FileStream stream = File.Create(Path.Join(source, "SKILL.md")))
        {
            stream.SetLength(LocalTestingBaselineCapturer.MaxSkillBytes + 1);
        }

        CreateDestination(plan, "consumer skill", overlay: null);

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>()
            .WithMessage("Filtrace skill source exceeds*byte safety limit*");

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("consumer skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_SourceAtEntryLimit_Succeeds()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "skill");
        CreateEmptyDirectories(
            source,
            LocalTestingBaselineCapturer.MaxSkillEntries - 1);

        new LocalTestingSkillDirectory().Publish(plan, source);

        Snapshot(plan.SkillDestination).Fingerprint.Should().NotBeNullOrWhiteSpace();
        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_SourceOverEntryLimit_LeavesDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "skill");
        CreateEmptyDirectories(
            source,
            LocalTestingBaselineCapturer.MaxSkillEntries);

        CreateDestination(plan, "consumer skill", overlay: null);

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>().WithMessage("*entry safety limit*");
        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("consumer skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_OverlayPushesStageOverEntryLimit_LeavesDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "skill");
        CreateEmptyDirectories(
            source,
            LocalTestingBaselineCapturer.MaxSkillEntries - 1);

        CreateDestination(plan, "consumer skill", "overlay");

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>().WithMessage("*entry safety limit*");
        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("consumer skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_OverlayPushesStageOverByteLimit_LeavesDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = Path.Join(directory.Path, "source");
        Directory.CreateDirectory(source);
        using (FileStream stream = File.Create(Path.Join(source, "SKILL.md")))
        {
            stream.SetLength(LocalTestingBaselineCapturer.MaxSkillBytes);
        }

        CreateDestination(plan, "consumer skill", "x");

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        publish.Should().Throw<InvalidDataException>()
            .WithMessage("Skill staging directory exceeds*byte safety limit*");

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("consumer skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_InterruptedOversizedOverlayStage_IsRecovered()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "latest skill");
        Directory.CreateDirectory(plan.SkillStagingPath);
        using (FileStream stream = File.Create(Path.Join(plan.SkillStagingPath, "SKILL.md")))
        {
            stream.SetLength(LocalTestingBaselineCapturer.MaxSkillBytes + 1);
        }

        new LocalTestingSkillDirectory().Publish(plan, source);

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("latest skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_InterruptedOverEntryStage_IsRecovered()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "latest skill");
        CreateSkillAt(plan.SkillStagingPath, "abandoned staging");
        CreateEmptyDirectories(
            plan.SkillStagingPath,
            LocalTestingBaselineCapturer.MaxSkillEntries);

        new LocalTestingSkillDirectory().Publish(plan, source);

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("latest skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_OverlayCollisionDuringStaging_CleansStageAndLeavesDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "skill");
        Directory.CreateDirectory(Path.Join(source, "overlay.md"));
        CreateDestination(plan, "consumer skill", "consumer overlay");

        Action publish = () => new LocalTestingSkillDirectory().Publish(plan, source);

        Exception exception = publish.Should().Throw<Exception>().Which;
        (exception is IOException or UnauthorizedAccessException).Should().BeTrue();
        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("consumer skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Publish_StagingCorruptedAfterCopy_CleansStageAndLeavesDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "local skill");
        CreateDestination(plan, "consumer skill", overlay: null);
        LocalTestingSkillDirectory publisher = new(
            afterCopy: () => File.WriteAllText(
                Path.Join(plan.SkillStagingPath, "SKILL.md"),
                "corrupt staging"));

        Action publish = () => publisher.Publish(plan, source);

        publish.Should().Throw<IOException>().WithMessage("*did not match*");
        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("consumer skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Restore_CorruptBackup_LeavesActiveDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        CreateDestination(plan, "prior skill", overlay: null);
        SkillBaseline baseline = CaptureSkillBaseline(plan);
        File.WriteAllText(Path.Join(plan.SkillBackupPath, "SKILL.md"), "corrupt");
        File.WriteAllText(Path.Join(plan.SkillDestination, "SKILL.md"), "active skill");

        Action restore = () => new LocalTestingSkillDirectory().Restore(plan, baseline);

        restore.Should().Throw<InvalidDataException>().WithMessage("*does not match*");
        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("active skill");

        AssertNoOperationDirectories(plan);
    }

    [TestMethod]
    public void Restore_LinkInsideBackup_ReportsBackupAndLeavesActiveDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        CreateDestination(plan, "prior skill", overlay: null);
        SkillBaseline baseline = CaptureSkillBaseline(plan);
        string external = Path.Join(directory.Path, "external.txt");
        File.WriteAllText(external, "external");
        TryCreateFileLink(Path.Join(plan.SkillBackupPath, "linked.txt"), external);
        File.WriteAllText(Path.Join(plan.SkillDestination, "SKILL.md"), "active skill");

        Action restore = () => new LocalTestingSkillDirectory().Restore(plan, baseline);

        restore.Should().Throw<InvalidDataException>()
            .WithMessage("Skill backup must not contain links*");

        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("active skill");
    }

    [TestMethod]
    public void Restore_AbsentBaselineWithUnexpectedBackup_LeavesActiveDestination()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        CreateDestination(plan, "active skill", overlay: null);
        CreateSkillAt(plan.SkillBackupPath, "unexpected backup");

        Action restore = () => new LocalTestingSkillDirectory().Restore(plan, new());

        restore.Should().Throw<InvalidDataException>().WithMessage("*unexpected backup*");
        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("active skill");
    }

    [TestMethod]
    public void Publish_LinkAddedAtMutationBoundary_RejectsExternalTarget()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string source = CreateSkill(directory.Path, "source", "local skill");
        CreateDestination(plan, "consumer skill", overlay: null);
        string external = CreateSkill(directory.Path, "external", "external skill");
        string probe = Path.Join(directory.Path, "probe");
        TryCreateDirectoryLink(probe, external);
        Directory.Delete(probe);
        LocalTestingSkillDirectory publisher = new(() =>
        {
            Directory.Delete(plan.SkillDestination, recursive: true);
            Directory.CreateSymbolicLink(plan.SkillDestination, external);
        });

        Action publish = () => publisher.Publish(plan, source);

        publish.Should().Throw<InvalidDataException>().WithMessage("*must not contain links*");
        File.ReadAllText(Path.Join(external, "SKILL.md")).Should().Be("external skill");
        Directory.Exists(plan.SkillStagingPath).Should().BeTrue();
        Directory.Exists(plan.SkillRetiredPath).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void Capture_ReservedOperationPath_ThrowsBeforeBackup(int pathKind)
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string reserved = pathKind is 0 ? plan.SkillStagingPath : plan.SkillRetiredPath;
        Directory.CreateDirectory(reserved);

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>().WithMessage("*already exists*");
        Directory.Exists(plan.SkillBackupPath).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void Capture_ReservedOperationFile_ThrowsBeforeBackup(int pathKind)
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string reserved = pathKind is 0 ? plan.SkillStagingPath : plan.SkillRetiredPath;
        Directory.CreateDirectory(Path.GetDirectoryName(reserved)!);
        File.WriteAllText(reserved, "consumer file");

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>().WithMessage("*already exists*");
        File.ReadAllText(reserved).Should().Be("consumer file");
        Directory.Exists(plan.SkillBackupPath).Should().BeFalse();
    }

    [TestMethod]
    public void Restore_LinkedStateRoot_ThrowsWithoutTargetMutation()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        CreateDestination(plan, "active skill", overlay: null);
        Directory.Delete(plan.StateRoot, recursive: true);
        string external = Path.Join(directory.Path, "external-state");
        Directory.CreateDirectory(external);
        TryCreateDirectoryLink(plan.StateRoot, external);

        Action restore = () => new LocalTestingSkillDirectory().Restore(plan, new());

        restore.Should().Throw<InvalidDataException>().WithMessage("*must not contain links*");
        File.ReadAllText(Path.Join(plan.SkillDestination, "SKILL.md"))
            .Should().Be("active skill");
    }

    private static void TryCreateDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }
    }

    private static void TryCreateFileLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }
    }

    private static void CreateEmptyDirectories(string root, int count)
    {
        for (int index = 0; index < count; index++)
        {
            Directory.CreateDirectory(Path.Join(root, $"entry-{index:D4}"));
        }
    }
}
