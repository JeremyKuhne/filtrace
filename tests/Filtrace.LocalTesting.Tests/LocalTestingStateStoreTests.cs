// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Nodes;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingStateStoreTests
{
    [TestMethod]
    public void Read_MissingState_ReturnsNull()
    {
        using TemporaryDirectory directory = new();
        LocalTestingStateStore store = new();

        store.Read(Path.Join(directory.Path, "state.json")).Should().BeNull();
    }

    [TestMethod]
    public void Write_ValidState_RoundTripsCamelCaseJson()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Join(directory.Path, "state.json");
        LocalTestingStateStore store = new();
        LocalTestingState state = TestState.Create(LocalTestingStatus.Active);

        store.Write(path, state);

        store.Read(path).Should().BeEquivalentTo(state);
        string json = File.ReadAllText(path);
        json.Should().Contain("\"schemaVersion\": 1");
        json.Should().Contain("\"status\": \"active\"");
    }

    [TestMethod]
    public void Write_ExistingState_ReplacesAtomicallyWithoutTemporaryFiles()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Join(directory.Path, "state.json");
        LocalTestingStateStore store = new();
        store.Write(path, TestState.Create(LocalTestingStatus.Installing));
        LocalTestingState replacement = TestState.Create(LocalTestingStatus.Restoring);

        store.Write(path, replacement);

        store.Read(path).Should().BeEquivalentTo(replacement);
        Directory.EnumerateFiles(directory.Path, ".state.json.*.tmp").Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public void Read_UnsupportedSchema_Throws(int schemaVersion)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Join(directory.Path, "state.json");
        LocalTestingStateStore store = new();
        store.Write(path, TestState.Create(LocalTestingStatus.Installing));
        ModifyState(path, state => state["schemaVersion"] = schemaVersion);

        Action read = () => store.Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage($"*schema version '{schemaVersion}'*");
    }

    [TestMethod]
    public void Read_MissingSchema_Throws()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Join(directory.Path, "state.json");
        LocalTestingStateStore store = new();
        store.Write(path, TestState.Create(LocalTestingStatus.Installing));
        ModifyState(path, state => state.Remove("schemaVersion"));

        Action read = () => store.Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*not valid JSON*");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(99)]
    public void Read_NumericStatus_Throws(int statusValue)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Join(directory.Path, "state.json");
        LocalTestingStateStore store = new();
        store.Write(path, TestState.Create(LocalTestingStatus.Installing));
        ModifyState(path, state => state["status"] = statusValue);

        Action read = () => store.Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*not valid JSON*");
    }

    [TestMethod]
    public void Read_MalformedState_Throws()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Join(directory.Path, "state.json");
        File.WriteAllText(path, "{not-json");

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*not valid JSON*");
    }

    [TestMethod]
    public void Read_OversizedState_Throws()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Join(directory.Path, "state.json");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(LocalTestingStateStore.MaxStateBytes + 1);
        }

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*safety limit*");
    }

    [TestMethod]
    public void Write_ActiveStateWithoutCli_Throws()
    {
        using TemporaryDirectory directory = new();
        LocalTestingState invalid = TestState.Create(LocalTestingStatus.Active) with
        {
            Cli = null
        };

        Action write = () => new LocalTestingStateStore().Write(
            Path.Join(directory.Path, "state.json"),
            invalid);

        write.Should().Throw<InvalidDataException>()
            .WithMessage("*must record the CLI package*");
    }

    [TestMethod]
    [DataRow("baseline")]
    [DataRow("mcp")]
    [DataRow("skill")]
    [DataRow("createdDirectories")]
    public void Read_NullBaselineMember_ThrowsInvalidData(string memberName)
    {
        using TemporaryDirectory directory = new();
        string path = WriteValidState(directory);
        ModifyState(path, state =>
        {
            if (memberName is "baseline")
            {
                state[memberName] = null;
            }
            else
            {
                GetObject(state, "baseline")[memberName] = null;
            }
        });

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*baseline is missing*");
    }

    [TestMethod]
    [DataRow("missingFile")]
    [DataRow("missingServer")]
    [DataRow("nonObjectServer")]
    public void Read_InvalidExistingMcpBaseline_Throws(string variation)
    {
        using TemporaryDirectory directory = new();
        string path = WriteValidState(directory);
        ModifyState(path, state =>
        {
            JsonObject mcp = GetObject(GetObject(state, "baseline"), "mcp");
            mcp["serverExisted"] = true;
            mcp["serversExisted"] = true;
            mcp["fileExisted"] = variation is not "missingFile";
            mcp["server"] = variation switch
            {
                "missingServer" => null,
                "nonObjectServer" => "dotnet",
                _ => new JsonObject()
            };
        });

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*existing 'servers' object*");
    }

    [TestMethod]
    public void Read_AbsentMcpServerWithValue_Throws()
    {
        using TemporaryDirectory directory = new();
        string path = WriteValidState(directory);
        ModifyState(path, state =>
        {
            JsonObject mcp = GetObject(GetObject(state, "baseline"), "mcp");
            mcp["server"] = new JsonObject();
        });

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*absent MCP server baseline*");
    }

    [TestMethod]
    public void Read_ServersWithoutMcpFile_Throws()
    {
        using TemporaryDirectory directory = new();
        string path = WriteValidState(directory);
        ModifyState(path, state =>
        {
            JsonObject mcp = GetObject(GetObject(state, "baseline"), "mcp");
            mcp["serversExisted"] = true;
        });

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*requires an existing file*");
    }

    [TestMethod]
    public void Read_CreatedAgentsWithoutCreatedSkills_Throws()
    {
        using TemporaryDirectory directory = new();
        string path = WriteValidState(directory);
        ModifyState(path, state =>
        {
            JsonObject created = GetObject(GetObject(state, "baseline"), "createdDirectories");
            created["agents"] = true;
        });

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*requires a created skills directory*");
    }

    [TestMethod]
    [DataRow(stringArrayData: null)]
    [DataRow("invalid")]
    public void Read_ExistingSkillWithInvalidHash_Throws(string? hash)
    {
        using TemporaryDirectory directory = new();
        string path = WriteValidState(directory);
        ModifyState(path, state =>
        {
            JsonObject skill = GetObject(GetObject(state, "baseline"), "skill");
            skill["existed"] = true;
            skill["backupSha256"] = hash;
        });

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*Skill backup SHA-256*");
    }

    [TestMethod]
    public void Read_AbsentSkillWithHash_Throws()
    {
        using TemporaryDirectory directory = new();
        string path = WriteValidState(directory);
        ModifyState(path, state =>
        {
            JsonObject skill = GetObject(GetObject(state, "baseline"), "skill");
            skill["backupSha256"] = TestState.Hash;
        });

        Action read = () => new LocalTestingStateStore().Read(path);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*absent skill baseline*");
    }

    [TestMethod]
    public void Write_RelativeSourceCheckout_Throws()
    {
        using TemporaryDirectory directory = new();
        LocalTestingState invalid = TestState.Create(LocalTestingStatus.Installing) with
        {
            SourceCheckout = "relative-source"
        };

        Action write = () => new LocalTestingStateStore().Write(
            Path.Join(directory.Path, "state.json"),
            invalid);

        write.Should().Throw<InvalidDataException>()
            .WithMessage("*must be an absolute path*");
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("hash")]
    public void Write_InvalidCliMetadata_Throws(string invalidProperty)
    {
        using TemporaryDirectory directory = new();
        LocalTestingState state = TestState.Create(LocalTestingStatus.Active);
        LocalTestingState invalid = state with
        {
            Cli = invalidProperty is "version"
                ? state.Cli! with { PackageVersion = " " }
                : state.Cli! with { PackageSha256 = "invalid" }
        };

        Action write = () => new LocalTestingStateStore().Write(
            Path.Join(directory.Path, "state.json"),
            invalid);

        write.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void Write_MissingStateDirectory_ThrowsWithoutCreatingIt()
    {
        using TemporaryDirectory directory = new();
        string stateDirectory = Path.Join(directory.Path, "missing");

        Action write = () => new LocalTestingStateStore().Write(
            Path.Join(stateDirectory, "state.json"),
            TestState.Create(LocalTestingStatus.Installing));

        write.Should().Throw<DirectoryNotFoundException>();
        Directory.Exists(stateDirectory).Should().BeFalse();
    }

    [TestMethod]
    public void Write_MoveFailure_RemovesTemporaryFile()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Join(directory.Path, "state.json");
        Directory.CreateDirectory(path);

        Action write = () => new LocalTestingStateStore().Write(
            path,
            TestState.Create(LocalTestingStatus.Installing));

        Exception exception = write.Should().Throw<Exception>().Which;
        (exception is IOException or UnauthorizedAccessException).Should().BeTrue();
        Directory.EnumerateFiles(directory.Path, ".state.json.*.tmp").Should().BeEmpty();
    }

    private static string WriteValidState(TemporaryDirectory directory)
    {
        string path = Path.Join(directory.Path, "state.json");
        new LocalTestingStateStore().Write(
            path,
            TestState.Create(LocalTestingStatus.Installing));

        return path;
    }

    private static void ModifyState(string path, Action<JsonObject> modification)
    {
        JsonObject state = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("Expected a JSON object.");

        modification(state);
        File.WriteAllText(path, state.ToJsonString());
    }

    private static JsonObject GetObject(JsonObject parent, string propertyName)
    {
        return parent[propertyName]?.AsObject()
            ?? throw new InvalidDataException($"Expected '{propertyName}' to be an object.");
    }
}
