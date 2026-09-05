// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingInstallInputsTests
{
    [TestMethod]
    public void Create_ValidArtifacts_NormalizesAndRetainsInputs()
    {
        using TemporaryDirectory directory = new();
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);

        Path.IsPathFullyQualified(inputs.SourceCheckout).Should().BeTrue();
        Path.IsPathFullyQualified(inputs.CliPackagePath).Should().BeTrue();
        Path.IsPathFullyQualified(inputs.McpDllPath).Should().BeTrue();
        Path.IsPathFullyQualified(inputs.SkillDirectory).Should().BeTrue();
        inputs.DotnetPath.Should().Be("dotnet");
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("package")]
    [DataRow("mcp")]
    [DataRow("skill")]
    public void Create_MissingArtifact_ThrowsBeforeCoordination(string artifact)
    {
        using TemporaryDirectory directory = new();
        string source = Path.Join(directory.Path, "source");
        string packageDirectory = Path.Join(source, "packages");
        string packagePath = LocalTestingInstallTestData.CreateMetadataPackage(packageDirectory);
        string mcpPath = Path.Join(source, "Filtrace.Mcp.dll");
        string skillDirectory = Path.Join(source, "skill");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(mcpPath, "mcp");
        File.WriteAllText(Path.Join(skillDirectory, "SKILL.md"), "skill");

        switch (artifact)
        {
            case "source":
                Directory.Delete(source, recursive: true);
                break;
            case "package":
                File.Delete(packagePath);
                break;
            case "mcp":
                File.Delete(mcpPath);
                break;
            case "skill":
                Directory.Delete(skillDirectory, recursive: true);
                break;
        }

        Action create = () => LocalTestingInstallInputs.Create(
            source,
            packagePath,
            "dotnet",
            mcpPath,
            skillDirectory);

        create.Should().Throw<Exception>().Which.Should().BeAssignableTo<IOException>();
    }

    [TestMethod]
    public void Create_LinkedSkillSource_Throws()
    {
        using TemporaryDirectory directory = new();
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        string link = Path.Join(directory.Path, "linked-skill");
        try
        {
            Directory.CreateSymbolicLink(link, inputs.SkillDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        Action create = () => LocalTestingInstallInputs.Create(
            inputs.SourceCheckout,
            inputs.CliPackagePath,
            inputs.DotnetPath,
            inputs.McpDllPath,
            link);

        create.Should().Throw<InvalidDataException>()
            .WithMessage("*skill source must not be a link*");
    }

    [TestMethod]
    public void Create_LinkedSourceCheckout_UsesFinalTargetIdentity()
    {
        using TemporaryDirectory directory = new();
        LocalTestingInstallInputs original = LocalTestingInstallTestData.CreateInputs(directory.Path);
        string link = Path.Join(directory.Path, "linked-source");
        try
        {
            Directory.CreateSymbolicLink(link, original.SourceCheckout);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        LocalTestingInstallInputs aliased = LocalTestingInstallInputs.Create(
            link,
            original.CliPackagePath,
            original.DotnetPath,
            original.McpDllPath,
            original.SkillDirectory);

        aliased.SourceCheckout.Should().Be(original.SourceCheckout);
    }

    [TestMethod]
    public void Create_LinkedSourceCheckoutAncestor_UsesFinalTargetIdentity()
    {
        using TemporaryDirectory directory = new();
        string physicalParent = Path.Join(directory.Path, "physical-parent");
        LocalTestingInstallInputs original = LocalTestingInstallTestData.CreateInputs(
            physicalParent);

        string linkedParent = Path.Join(directory.Path, "linked-parent");
        try
        {
            Directory.CreateSymbolicLink(linkedParent, physicalParent);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        LocalTestingInstallInputs aliased = LocalTestingInstallInputs.Create(
            Path.Join(linkedParent, "source"),
            original.CliPackagePath,
            original.DotnetPath,
            original.McpDllPath,
            original.SkillDirectory);

        aliased.SourceCheckout.Should().Be(original.SourceCheckout);
    }

    [TestMethod]
    public void Create_OversizedSkillSource_Throws()
    {
        using TemporaryDirectory directory = new();
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        using (FileStream stream = File.Create(Path.Join(inputs.SkillDirectory, "oversized.bin")))
        {
            stream.SetLength(LocalTestingBaselineCapturer.MaxSkillBytes + 1);
        }

        Action create = () => LocalTestingInstallInputs.Create(
            inputs.SourceCheckout,
            inputs.CliPackagePath,
            inputs.DotnetPath,
            inputs.McpDllPath,
            inputs.SkillDirectory);

        create.Should().Throw<InvalidDataException>()
            .WithMessage("*safety limit*");
    }
}
