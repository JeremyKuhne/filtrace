// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed partial class LocalTestingMcpConfigurationTests
{
    [TestMethod]
    public void Publish_MissingConfiguration_CreatesLocalServer()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");

        new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        using JsonDocument document = ReadConfiguration(plan);
        JsonElement root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name).Should().Equal("servers");
        AssertLocalServer(root.GetProperty("servers").GetProperty("filtrace"), mcpDllPath);
        Directory.EnumerateFiles(
            Path.GetDirectoryName(plan.McpConfigurationPath)!,
            ".mcp.json.*.tmp").Should().BeEmpty();
    }

    [TestMethod]
    public void Publish_ExistingJsonc_PreservesUnrelatedConfiguration()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(
            plan,
            """
            {
              // Retained input configuration.
              "inputs": [{ "id": "trace" }],
              "servers": {
                "other": { "command": "other" },
              },
            }
            """);

        new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        using JsonDocument document = ReadConfiguration(plan);
        JsonElement root = document.RootElement;
        root.GetProperty("inputs")[0].GetProperty("id").GetString().Should().Be("trace");
        JsonElement servers = root.GetProperty("servers");
        servers.GetProperty("other").GetProperty("command").GetString().Should().Be("other");
        AssertLocalServer(servers.GetProperty("filtrace"), mcpDllPath);
    }

    [TestMethod]
    public void Publish_DuplicateUnrelatedProperties_PreservesTheirOrderAndValues()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(
            plan,
            """
            {"duplicate":1,"duplicate":2,"servers":{"other":3,"other":4}}
            """);

        new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        using JsonDocument document = ReadConfiguration(plan);
        document.RootElement.EnumerateObject()
            .Where(property => property.NameEquals("duplicate"))
            .Select(property => property.Value.GetInt32())
            .Should().Equal(1, 2);

        document.RootElement.GetProperty("servers").EnumerateObject()
            .Where(property => property.NameEquals("other"))
            .Select(property => property.Value.GetInt32())
            .Should().Equal(3, 4);
    }

    [TestMethod]
    public void Publish_ExistingFiltraceServer_ReplacesOnlyThatServer()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(
            plan,
            """
            {"servers":{"filtrace":{"command":"old"},"other":{"url":"https://example.test"}}}
            """);

        new LocalTestingMcpConfiguration().Publish(plan, mcpDllPath);

        using JsonDocument document = ReadConfiguration(plan);
        JsonElement servers = document.RootElement.GetProperty("servers");
        AssertLocalServer(servers.GetProperty("filtrace"), mcpDllPath);
        servers.GetProperty("other").GetProperty("url").GetString()
            .Should().Be("https://example.test");
    }

    [TestMethod]
    public void Publish_RepeatedWithNewBuild_ConvergesToLatestServer()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string firstMcpDllPath = CreateMcpDll(directory.Path, "first");
        string secondMcpDllPath = CreateMcpDll(directory.Path, "second");

        LocalTestingMcpConfiguration configuration = new();
        configuration.Publish(plan, firstMcpDllPath);
        configuration.Publish(plan, secondMcpDllPath);
        string afterSecondPublish = File.ReadAllText(plan.McpConfigurationPath);
        configuration.Publish(plan, secondMcpDllPath);

        File.ReadAllText(plan.McpConfigurationPath).Should().Be(afterSecondPublish);
        using JsonDocument document = ReadConfiguration(plan);
        AssertLocalServer(
            document.RootElement.GetProperty("servers").GetProperty("filtrace"),
            secondMcpDllPath);
    }

    [TestMethod]
    public void Restore_ExistingServer_RestoresBaselineAndPreservesLaterEntries()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(
            plan,
            """
            {"servers":{"filtrace":{"command":"old","args":["old.dll"]},"other":{}}}
            """);

        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        LocalTestingMcpConfiguration configuration = new();
        configuration.Publish(plan, mcpDllPath);
        JsonObject active = ReadConfigurationNode(plan);
        active["later"] = true;
        active["servers"]!["added"] = new JsonObject { ["command"] = "added" };
        WriteConfiguration(plan, active.ToJsonString());

        configuration.Restore(plan, baseline);

        using JsonDocument document = ReadConfiguration(plan);
        JsonElement root = document.RootElement;
        root.GetProperty("later").GetBoolean().Should().BeTrue();
        JsonElement servers = root.GetProperty("servers");
        servers.GetProperty("added").GetProperty("command").GetString().Should().Be("added");
        servers.GetProperty("other").ValueKind.Should().Be(JsonValueKind.Object);
        JsonElement restored = servers.GetProperty("filtrace");
        restored.GetProperty("command").GetString().Should().Be("old");
        restored.GetProperty("args")[0].GetString().Should().Be("old.dll");
    }

    [TestMethod]
    public void Restore_ExistingServerMissingFromActiveConfiguration_RecreatesBaseline()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        WriteConfiguration(
            plan,
            "{\"servers\":{\"filtrace\":{\"command\":\"old\"}}}");

        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        WriteConfiguration(plan, "{\"servers\":{\"other\":{}}}");

        new LocalTestingMcpConfiguration().Restore(plan, baseline);

        using JsonDocument document = ReadConfiguration(plan);
        JsonElement servers = document.RootElement.GetProperty("servers");
        servers.GetProperty("filtrace").GetProperty("command").GetString().Should().Be("old");
        servers.GetProperty("other").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [TestMethod]
    public void Restore_PreexistingFileWithoutServers_RemovesCreatedContainer()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(plan, "{\"inputs\":[]}");
        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        LocalTestingMcpConfiguration configuration = new();
        configuration.Publish(plan, mcpDllPath);

        configuration.Restore(plan, baseline);

        using JsonDocument document = ReadConfiguration(plan);
        document.RootElement.TryGetProperty("servers", out _).Should().BeFalse();
        document.RootElement.GetProperty("inputs").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [TestMethod]
    public void Restore_PreexistingServersContainer_PreservesEmptyContainer()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(plan, "{\"servers\":{}}");
        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        LocalTestingMcpConfiguration configuration = new();
        configuration.Publish(plan, mcpDllPath);

        configuration.Restore(plan, baseline);

        using JsonDocument document = ReadConfiguration(plan);
        document.RootElement.GetProperty("servers").EnumerateObject().Should().BeEmpty();
    }

    [TestMethod]
    public void Restore_PreexistingServersMissingFromActiveConfiguration_RecreatesContainer()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        WriteConfiguration(plan, "{\"servers\":{}}");
        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        WriteConfiguration(plan, "{\"inputs\":[]}");

        new LocalTestingMcpConfiguration().Restore(plan, baseline);

        using JsonDocument document = ReadConfiguration(plan);
        document.RootElement.GetProperty("inputs").ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetProperty("servers").EnumerateObject().Should().BeEmpty();
    }

    [TestMethod]
    public void Restore_AbsentFile_RemovesManagedConfiguration()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        LocalTestingMcpConfiguration configuration = new();
        configuration.Publish(plan, mcpDllPath);

        configuration.Restore(plan, baseline);
        configuration.Restore(plan, baseline);

        File.Exists(plan.McpConfigurationPath).Should().BeFalse();
    }

    [TestMethod]
    public void Restore_AbsentFile_PreservesEntriesAddedAfterPublish()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        LocalTestingMcpConfiguration configuration = new();
        configuration.Publish(plan, mcpDllPath);
        JsonObject active = ReadConfigurationNode(plan);
        active["inputs"] = new JsonArray("later");
        active["servers"]!["other"] = new JsonObject { ["command"] = "other" };
        WriteConfiguration(plan, active.ToJsonString());

        configuration.Restore(plan, baseline);

        using JsonDocument document = ReadConfiguration(plan);
        JsonElement root = document.RootElement;
        root.GetProperty("inputs")[0].GetString().Should().Be("later");
        JsonElement servers = root.GetProperty("servers");
        servers.TryGetProperty("filtrace", out _).Should().BeFalse();
        servers.GetProperty("other").GetProperty("command").GetString().Should().Be("other");
    }

    [TestMethod]
    public void Restore_ExistingFile_IsIdempotent()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        string mcpDllPath = CreateMcpDll(directory.Path, "source");
        WriteConfiguration(plan, "{\"servers\":{\"other\":{}}}");
        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        LocalTestingMcpConfiguration configuration = new();
        configuration.Publish(plan, mcpDllPath);

        configuration.Restore(plan, baseline);
        string afterFirstRestore = File.ReadAllText(plan.McpConfigurationPath);
        configuration.Restore(plan, baseline);

        File.ReadAllText(plan.McpConfigurationPath).Should().Be(afterFirstRestore);
    }

    [TestMethod]
    public void Restore_MissingPreexistingFile_ThrowsWithoutRecreatingPartialBaseline()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        WriteConfiguration(plan, "{\"inputs\":[\"original\"]}");
        McpBaseline baseline = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        File.Delete(plan.McpConfigurationPath);

        Action restore = () => new LocalTestingMcpConfiguration().Restore(plan, baseline);

        restore.Should().Throw<FileNotFoundException>()
            .WithMessage("*disappeared before it could be restored*");

        File.Exists(plan.McpConfigurationPath).Should().BeFalse();
    }

    private static ResourcePlan CreatePlan(TemporaryDirectory directory)
    {
        string targetRoot = Path.Join(directory.Path, "target");
        string gitDirectory = Path.Join(targetRoot, ".git");
        Directory.CreateDirectory(gitDirectory);
        return ResourcePlan.Create(targetRoot, gitDirectory);
    }

    private static string CreateMcpDll(string root, string name)
    {
        string path = Path.Join(root, name, "Filtrace.Mcp.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name);
        return path;
    }

    private static void WriteConfiguration(ResourcePlan plan, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.McpConfigurationPath)!);
        File.WriteAllText(plan.McpConfigurationPath, json);
    }

    private static JsonDocument ReadConfiguration(ResourcePlan plan)
    {
        return JsonDocument.Parse(File.ReadAllText(plan.McpConfigurationPath));
    }

    private static JsonObject ReadConfigurationNode(ResourcePlan plan)
    {
        return JsonNode.Parse(File.ReadAllText(plan.McpConfigurationPath))?.AsObject()
            ?? throw new InvalidDataException("Expected an MCP configuration object.");
    }

    private static void AssertLocalServer(JsonElement server, string mcpDllPath)
    {
        server.GetProperty("type").GetString().Should().Be("stdio");
        server.GetProperty("command").GetString().Should().Be("dotnet");
        JsonElement.ArrayEnumerator arguments = server.GetProperty("args").EnumerateArray();
        arguments.Select(argument => argument.GetString()).Should().Equal(mcpDllPath);
    }
}
