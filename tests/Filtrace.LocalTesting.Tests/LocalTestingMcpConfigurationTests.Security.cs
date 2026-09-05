// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Filtrace.LocalTesting.Tests;

public sealed partial class LocalTestingMcpConfigurationTests
{
    [TestMethod]
    [DataRow("{not-json")]
    [DataRow("[]")]
    [DataRow("{\"servers\":null}")]
    [DataRow("{\"servers\":{},\"servers\":{}}")]
    [DataRow("{\"servers\":{\"filtrace\":{},\"filtrace\":{}}}")]
    [DataRow("{\"servers\":{\"filtrace\":false}}")]
    public void Publish_InvalidConfiguration_ThrowsWithoutChangingIt(string json)
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(plan, json);

        Action publish = () => new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        publish.Should().Throw<InvalidDataException>();
        File.ReadAllText(plan.McpConfigurationPath).Should().Be(json);
        AssertNoTemporaryFiles(plan);
    }

    [TestMethod]
    public void Publish_ConfigurationAtInputLimitAndOverOutputLimit_LeavesOriginal()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        string json = CreatePaddedConfiguration(McpConfigurationDocument.MaxBytes);
        WriteConfiguration(plan, json);

        Action publish = () => new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        publish.Should().Throw<InvalidDataException>()
            .WithMessage("*Updated*exceeds*byte safety limit*");

        File.ReadAllText(plan.McpConfigurationPath).Should().Be(json);
        AssertNoTemporaryFiles(plan);
    }

    [TestMethod]
    public void Publish_ConfigurationBelowOutputLimit_SucceedsWithinLimit()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(
            plan,
            CreatePaddedConfiguration(McpConfigurationDocument.MaxBytes - 1024));

        new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        new FileInfo(plan.McpConfigurationPath).Length
            .Should().BeLessThanOrEqualTo(McpConfigurationDocument.MaxBytes);

        using JsonDocument document = ReadConfiguration(plan);
        AssertLocalServer(
            document.RootElement.GetProperty("servers").GetProperty("filtrace"),
            mcpDllPath);
    }

    [TestMethod]
    public void Publish_ConfigurationOverInputLimit_ThrowsWithoutReadingIt()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(
            plan,
            CreatePaddedConfiguration(McpConfigurationDocument.MaxBytes + 1));

        Action publish = () => new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        publish.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*byte safety limit*");

        new FileInfo(plan.McpConfigurationPath).Length
            .Should().Be(McpConfigurationDocument.MaxBytes + 1);

        AssertNoTemporaryFiles(plan);
    }

    [TestMethod]
    public void Restore_CombinedOutputOverLimit_LeavesActiveConfiguration()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string baselineJson = CreatePaddedServerConfiguration(600_000);
        WriteConfiguration(plan, baselineJson);
        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        string activeJson = CreatePaddedConfiguration(600_000);
        WriteConfiguration(plan, activeJson);

        Action restore = () => new LocalTestingMcpConfiguration().Restore(plan, baseline);

        restore.Should().Throw<InvalidDataException>()
            .WithMessage("*Updated*exceeds*byte safety limit*");

        File.ReadAllText(plan.McpConfigurationPath).Should().Be(activeJson);
        AssertNoTemporaryFiles(plan);
    }

    [TestMethod]
    public void Publish_RelativeMcpPath_ThrowsBeforeCreatingConfiguration()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);

        Action publish = () => new LocalTestingMcpConfiguration().Publish(
            plan,
            Path.Join("relative", "Filtrace.Mcp.dll"));

        publish.Should().Throw<ArgumentException>()
            .WithMessage("*must be absolute*");

        File.Exists(plan.McpConfigurationPath).Should().BeFalse();
    }

    [TestMethod]
    public void Publish_MissingMcpDll_ThrowsBeforeCreatingConfiguration()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string missing = Path.Join(directory.Path, "missing", "Filtrace.Mcp.dll");

        Action publish = () => new LocalTestingMcpConfiguration().Publish(plan, missing);

        publish.Should().Throw<FileNotFoundException>();
        File.Exists(plan.McpConfigurationPath).Should().BeFalse();
    }

    [TestMethod]
    public void Publish_LinkedManagedAncestor_ThrowsWithoutWritingThroughLink()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
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

        Action publish = () => new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        publish.Should().Throw<InvalidDataException>()
            .WithMessage("*must not contain links*");

        File.Exists(Path.Join(external, "mcp.json")).Should().BeFalse();
    }

    [TestMethod]
    [DoNotParallelize]
    public void Publish_LinkAddedBeforeReplacement_ThrowsWithoutWritingThroughLink()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(plan, "{\"inputs\":[]}");
        string external = Path.Join(directory.Path, "external.json");
        File.WriteAllText(external, "{\"external\":true}");
        string probe = Path.Join(directory.Path, "symlink-probe.json");
        try
        {
            File.CreateSymbolicLink(probe, external);
            File.Delete(probe);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }

        LocalTestingMcpConfiguration configuration = new(() =>
        {
            File.Delete(plan.McpConfigurationPath);
            File.CreateSymbolicLink(plan.McpConfigurationPath, external);
        });

        try
        {
            Action publish = () => configuration.Publish(plan, mcpDllPath);

            publish.Should().Throw<InvalidDataException>()
                .WithMessage("*must not contain links*");

            File.ReadAllText(external).Should().Be("{\"external\":true}");
            AssertNoTemporaryFiles(plan);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable: {exception.Message}");
        }
    }

    [TestMethod]
    [Timeout(5_000)]
    public void Publish_FifoConfiguration_ThrowsWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        UnixTestFile.CreateFifo(plan.McpConfigurationPath);

        Action publish = () => new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        publish.Should().Throw<InvalidDataException>()
            .WithMessage("*regular file*");
    }

    [TestMethod]
    public void Publish_NewConfiguration_UsesPrivateUnixMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");

        new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        File.GetUnixFileMode(plan.McpConfigurationPath).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [TestMethod]
    public void Publish_ExistingConfiguration_PreservesUnixMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(plan, "{}");
        UnixFileMode mode = UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead;

        File.SetUnixFileMode(plan.McpConfigurationPath, mode);

        new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        File.GetUnixFileMode(plan.McpConfigurationPath).Should().Be(mode);
    }

    [TestMethod]
    public void Publish_ExistingConfiguration_PreservesWindowsAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(plan, "{}");
        FileSecurity security = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(plan.McpConfigurationPath));

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        SecurityIdentifier identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity has no SID.");

        security.AddAccessRule(new(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        FileSystemAclExtensions.SetAccessControl(
            new FileInfo(plan.McpConfigurationPath),
            security);

        string before = ReadWindowsSddl(plan.McpConfigurationPath);

        new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        ReadWindowsSddl(plan.McpConfigurationPath).Should().Be(before);
    }

    [TestMethod]
    public void Restore_InvalidBaseline_ThrowsWithoutChangingConfiguration()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        WriteConfiguration(plan, "{\"servers\":{\"filtrace\":{}}}");
        McpBaseline baseline = new()
        {
            ServerExisted = true,
            ServersExisted = true,
            FileExisted = false
        };

        Action restore = () => new LocalTestingMcpConfiguration().Restore(plan, baseline);

        restore.Should().Throw<InvalidDataException>();
        File.ReadAllText(plan.McpConfigurationPath)
            .Should().Be("{\"servers\":{\"filtrace\":{}}}");
    }

    [TestMethod]
    public void Publish_LockedWindowsConfiguration_LeavesOriginalAndRemovesTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        const string original = "{\"inputs\":[]}";
        WriteConfiguration(plan, original);
        using FileStream locked = new(
            plan.McpConfigurationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        Action publish = () => new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        publish.Should().Throw<IOException>();
        File.ReadAllText(plan.McpConfigurationPath).Should().Be(original);
        AssertNoTemporaryFiles(plan);
    }

    private static string CreatePaddedConfiguration(int byteCount)
    {
        const string prefix = "{\"padding\":\"";
        const string suffix = "\"}";
        return prefix + new string('x', byteCount - prefix.Length - suffix.Length) + suffix;
    }

    private static string CreatePaddedServerConfiguration(int byteCount)
    {
        const string prefix = "{\"servers\":{\"filtrace\":{\"padding\":\"";
        const string suffix = "\"}}}";
        return prefix + new string('x', byteCount - prefix.Length - suffix.Length) + suffix;
    }

    private static void AssertNoTemporaryFiles(ResourcePlan plan)
    {
        Directory.EnumerateFiles(
            Path.GetDirectoryName(plan.McpConfigurationPath)!,
            ".mcp.json.*.tmp").Should().BeEmpty();
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindowsSddl(string path)
    {
        return FileSystemAclExtensions.GetAccessControl(new FileInfo(path))
            .GetSecurityDescriptorSddlForm(AccessControlSections.All);
    }
}
