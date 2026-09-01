// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingBaselineCapturerTests
{
    [TestMethod]
    public void Capture_AbsentResources_RecordsCreatedParents()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);

        LocalTestingBaseline baseline = new LocalTestingBaselineCapturer().Capture(plan);

        baseline.Mcp.FileExisted.Should().BeFalse();
        baseline.Mcp.ServersExisted.Should().BeFalse();
        baseline.Mcp.ServerExisted.Should().BeFalse();
        baseline.Skill.Existed.Should().BeFalse();
        baseline.CreatedDirectories.Should().BeEquivalentTo(new CreatedDirectoryBaseline
        {
            Vscode = true,
            Agents = true,
            Skills = true
        });

        Directory.Exists(plan.SkillBackupPath).Should().BeFalse();
    }

    [TestMethod]
    public void Capture_ExistingResources_PreservesServerAndCopiesSkill()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        File.WriteAllText(
            plan.McpConfigurationPath,
            """
            {
              "servers": {
                "other": { "command": "other" },
                "filtrace": { "command": "dotnet", "args": ["old.dll"] }
              }
            }
            """);

        Directory.CreateDirectory(plan.SkillDestination);
        File.WriteAllText(Path.Join(plan.SkillDestination, "SKILL.md"), "prior skill");
        Directory.CreateDirectory(Path.Join(plan.SkillDestination, "empty"));

        LocalTestingBaseline baseline = new LocalTestingBaselineCapturer().Capture(plan);

        baseline.Mcp.FileExisted.Should().BeTrue();
        baseline.Mcp.ServersExisted.Should().BeTrue();
        baseline.Mcp.ServerExisted.Should().BeTrue();
        baseline.Mcp.Server!.Value.GetProperty("command").GetString().Should().Be("dotnet");
        baseline.Skill.Existed.Should().BeTrue();
        baseline.Skill.BackupSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        File.ReadAllText(Path.Join(plan.SkillBackupPath, "SKILL.md")).Should().Be("prior skill");
        Directory.Exists(Path.Join(plan.SkillBackupPath, "empty")).Should().BeTrue();
        baseline.CreatedDirectories.Should().BeEquivalentTo(new CreatedDirectoryBaseline());

        File.WriteAllText(Path.Join(plan.SkillDestination, "SKILL.md"), "changed later");
        File.ReadAllText(Path.Join(plan.SkillBackupPath, "SKILL.md")).Should().Be("prior skill");
    }

    [TestMethod]
    public void Capture_ExistingBackup_RefusesToReplaceOriginalBaseline()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(plan.SkillDestination);
        File.WriteAllText(Path.Join(plan.SkillDestination, "SKILL.md"), "original");
        new LocalTestingBaselineCapturer().Capture(plan);
        File.WriteAllText(Path.Join(plan.SkillDestination, "SKILL.md"), "replacement");

        Action recapture = () => new LocalTestingBaselineCapturer().Capture(plan);

        recapture.Should().Throw<InvalidDataException>()
            .WithMessage("*backup already exists*");

        File.ReadAllText(Path.Join(plan.SkillBackupPath, "SKILL.md")).Should().Be("original");
    }

    [TestMethod]
    public void Capture_ExistingBackupWithMissingSource_RefusesToIgnoreOriginalBaseline()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(plan.SkillDestination);
        File.WriteAllText(Path.Join(plan.SkillDestination, "SKILL.md"), "original");
        new LocalTestingBaselineCapturer().Capture(plan);
        Directory.Delete(plan.SkillDestination, recursive: true);

        Action recapture = () => new LocalTestingBaselineCapturer().Capture(plan);

        recapture.Should().Throw<InvalidDataException>()
            .WithMessage("*backup already exists*");

        File.ReadAllText(Path.Join(plan.SkillBackupPath, "SKILL.md")).Should().Be("original");
    }

    [TestMethod]
    public void Capture_ExistingBackupFileWithMissingSource_RefusesToIgnoreBackup()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        File.WriteAllText(plan.SkillBackupPath, "unexpected backup");

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>()
            .WithMessage("*backup already exists*");

        File.ReadAllText(plan.SkillBackupPath).Should().Be("unexpected backup");
    }

    [TestMethod]
    public void Capture_ExistingMcpWithoutServers_RecordsContainerAsAbsent()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        File.WriteAllText(plan.McpConfigurationPath, "{\"inputs\":[]}");

        LocalTestingBaseline baseline = new LocalTestingBaselineCapturer().Capture(plan);

        baseline.Mcp.FileExisted.Should().BeTrue();
        baseline.Mcp.ServersExisted.Should().BeFalse();
        baseline.Mcp.ServerExisted.Should().BeFalse();
    }

    [TestMethod]
    public void Capture_ExistingMcpServersWithoutFiltrace_RecordsServerAsAbsent()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        File.WriteAllText(plan.McpConfigurationPath, "{\"servers\":{\"other\":{}}}");

        LocalTestingBaseline baseline = new LocalTestingBaselineCapturer().Capture(plan);

        baseline.Mcp.FileExisted.Should().BeTrue();
        baseline.Mcp.ServersExisted.Should().BeTrue();
        baseline.Mcp.ServerExisted.Should().BeFalse();
        baseline.Mcp.Server.Should().BeNull();
    }

    [TestMethod]
    [DataRow("{\"servers\":{\n// line comment\n}}")]
    [DataRow("{/* block comment */\"servers\":{}}")]
    [DataRow("{\"servers\":{},}")]
    public void Capture_ValidMcpJsonc_RecordsBaseline(string json)
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        File.WriteAllText(plan.McpConfigurationPath, json);

        LocalTestingBaseline baseline = new LocalTestingBaselineCapturer().Capture(plan);

        baseline.Mcp.FileExisted.Should().BeTrue();
        baseline.Mcp.ServersExisted.Should().BeTrue();
        baseline.Mcp.ServerExisted.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("{not-json")]
    [DataRow("[]")]
    [DataRow("{\"servers\":null}")]
    [DataRow("{\"servers\":{},\"servers\":{}}")]
    [DataRow("{\"servers\":{\"filtrace\":{},\"filtrace\":{}}}")]
    public void Capture_InvalidMcpConfiguration_Throws(string json)
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        File.WriteAllText(plan.McpConfigurationPath, json);

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void Capture_OversizedMcpConfiguration_Throws()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        using (FileStream stream = File.Create(plan.McpConfigurationPath))
        {
            stream.SetLength(LocalTestingBaselineCapturer.MaxMcpConfigurationBytes + 1);
        }

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>()
            .WithMessage("*safety limit*");
    }

    [TestMethod]
    public void Capture_MaximumMcpConfiguration_Succeeds()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        const string prefix = "{\"padding\":\"";
        const string suffix = "\"}";
        File.WriteAllText(
            plan.McpConfigurationPath,
            prefix + new string(
                'x',
                LocalTestingBaselineCapturer.MaxMcpConfigurationBytes
                    - prefix.Length
                    - suffix.Length) + suffix);

        LocalTestingBaseline baseline = new LocalTestingBaselineCapturer().Capture(plan);

        baseline.Mcp.FileExisted.Should().BeTrue();
        baseline.Mcp.ServersExisted.Should().BeFalse();
    }

    [TestMethod]
    public void Capture_MaximumSkillBytes_Succeeds()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(plan.SkillDestination);
        string skillFile = Path.Join(plan.SkillDestination, "SKILL.md");
        using (FileStream stream = File.Create(skillFile))
        {
            stream.SetLength(LocalTestingBaselineCapturer.MaxSkillBytes);
        }

        LocalTestingBaseline baseline = new LocalTestingBaselineCapturer().Capture(plan);

        baseline.Skill.Existed.Should().BeTrue();
        new FileInfo(Path.Join(plan.SkillBackupPath, "SKILL.md")).Length
            .Should().Be(LocalTestingBaselineCapturer.MaxSkillBytes);
    }

    [TestMethod]
    public void Capture_OversizedSkill_ThrowsWithoutBackup()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(plan.SkillDestination);
        using (FileStream stream = File.Create(Path.Join(plan.SkillDestination, "SKILL.md")))
        {
            stream.SetLength(LocalTestingBaselineCapturer.MaxSkillBytes + 1);
        }

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>()
            .WithMessage("*safety limit*");

        Directory.Exists(plan.SkillBackupPath).Should().BeFalse();
    }

    [TestMethod]
    public void Capture_MaximumSkillEntries_Succeeds()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(plan.SkillDestination);
        CreateEmptyDirectories(
            plan.SkillDestination,
            LocalTestingBaselineCapturer.MaxSkillEntries);

        LocalTestingBaseline baseline = new LocalTestingBaselineCapturer().Capture(plan);

        baseline.Skill.Existed.Should().BeTrue();
    }

    [TestMethod]
    public void Capture_TooManySkillEntries_ThrowsWithoutBackup()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(plan.SkillDestination);
        CreateEmptyDirectories(
            plan.SkillDestination,
            LocalTestingBaselineCapturer.MaxSkillEntries + 1);

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>()
            .WithMessage("*entry safety limit*");

        Directory.Exists(plan.SkillBackupPath).Should().BeFalse();
    }

    [TestMethod]
    public void Capture_LinkInsideSkill_ThrowsWithoutBackup()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(plan.SkillDestination);
        string external = Path.Join(directory.Path, "external.txt");
        File.WriteAllText(external, "external");
        try
        {
            File.CreateSymbolicLink(Path.Join(plan.SkillDestination, "linked.txt"), external);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>()
            .WithMessage("*must not contain links*");

        Directory.Exists(plan.SkillBackupPath).Should().BeFalse();
    }

    [TestMethod]
    public void Capture_LinkedManagedAncestor_Throws()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string external = Path.Join(directory.Path, "external");
        Directory.CreateDirectory(external);
        try
        {
            Directory.CreateSymbolicLink(
                Path.GetDirectoryName(plan.McpConfigurationPath)!,
                external);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>()
            .WithMessage("*must not contain links*");
    }

    [TestMethod]
    [Timeout(5_000)]
    public void Capture_FifoMcpConfiguration_ThrowsWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        UnixTestFile.CreateFifo(plan.McpConfigurationPath);

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>()
            .WithMessage("*regular file*");
    }

    [TestMethod]
    [Timeout(5_000)]
    public void Capture_FifoSkillEntry_ThrowsWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        Directory.CreateDirectory(plan.SkillDestination);
        UnixTestFile.CreateFifo(Path.Join(plan.SkillDestination, "fifo"));

        Action capture = () => new LocalTestingBaselineCapturer().Capture(plan);

        capture.Should().Throw<InvalidDataException>()
            .WithMessage("*regular file*");

        Directory.Exists(plan.SkillBackupPath).Should().BeFalse();
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

    private static void CreateEmptyDirectories(string root, int count)
    {
        for (int index = 0; index < count; index++)
        {
            Directory.CreateDirectory(Path.Join(root, $"entry-{index:D4}"));
        }
    }
}
