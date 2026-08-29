// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class SkillOverlayTests
{
    [TestMethod]
    public void Read_MissingSkill_ReturnsNull()
    {
        using TemporaryDirectory directory = new();

        SkillOverlay.Read(Path.Join(directory.Path, "missing")).Should().BeNull();
    }

    [TestMethod]
    public void Read_ExistingOverlay_ReturnsExactBytes()
    {
        using TemporaryDirectory directory = new();
        string skill = Path.Join(directory.Path, "skill");
        Directory.CreateDirectory(skill);
        byte[] expected = [0, 1, 2, 255];
        File.WriteAllBytes(Path.Join(skill, "overlay.md"), expected);

        byte[]? actual = SkillOverlay.Read(skill);

        actual.Should().Equal(expected);
    }

    [TestMethod]
    public void Read_OversizedOverlay_Throws()
    {
        using TemporaryDirectory directory = new();
        string skill = Path.Join(directory.Path, "skill");
        Directory.CreateDirectory(skill);
        using (FileStream stream = File.Create(Path.Join(skill, "overlay.md")))
        {
            stream.SetLength(SkillOverlay.MaxBytes + 1);
        }

        Action read = () => SkillOverlay.Read(skill);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*safety limit*");
    }

    [TestMethod]
    public void Read_MaximumOverlay_ReturnsExactBytes()
    {
        using TemporaryDirectory directory = new();
        string skill = Path.Join(directory.Path, "skill");
        Directory.CreateDirectory(skill);
        byte[] expected = new byte[SkillOverlay.MaxBytes];
        expected[^1] = 42;
        File.WriteAllBytes(Path.Join(skill, "overlay.md"), expected);

        byte[]? actual = SkillOverlay.Read(skill);

        actual.Should().Equal(expected);
    }

    [TestMethod]
    public void Read_DanglingOverlayLink_Throws()
    {
        using TemporaryDirectory directory = new();
        string skill = Path.Join(directory.Path, "skill");
        Directory.CreateDirectory(skill);
        try
        {
            File.CreateSymbolicLink(
                Path.Join(skill, "overlay.md"),
                Path.Join(directory.Path, "missing.md"));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        Action read = () => SkillOverlay.Read(skill);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*must not be a link*");
    }

    [TestMethod]
    public void Read_DanglingSkillDirectoryLink_Throws()
    {
        using TemporaryDirectory directory = new();
        string skill = Path.Join(directory.Path, "skill");
        try
        {
            Directory.CreateSymbolicLink(skill, Path.Join(directory.Path, "missing"));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        Action read = () => SkillOverlay.Read(skill);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*must not be a link*");
    }

    [TestMethod]
    [Timeout(5_000)]
    public void Read_FifoOverlay_ThrowsWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string skill = Path.Join(directory.Path, "skill");
        Directory.CreateDirectory(skill);
        UnixTestFile.CreateFifo(Path.Join(skill, "overlay.md"));

        Action read = () => SkillOverlay.Read(skill);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*regular file*");
    }
}
