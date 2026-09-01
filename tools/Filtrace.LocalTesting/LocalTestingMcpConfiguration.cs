// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.LocalTesting;

/// <summary>
///  Publishes and restores only the Filtrace entry in a bounded VS Code MCP configuration.
/// </summary>
/// <param name="beforeReplace">
///  An optional test hook invoked after a temporary file is flushed and before publication.
/// </param>
internal sealed class LocalTestingMcpConfiguration(Action? beforeReplace = null)
{
    /// <summary>
    ///  Atomically writes a local <c>dotnet</c>-hosted Filtrace server while preserving unrelated JSON properties.
    /// </summary>
    /// <param name="plan">The target's normalized local-testing resource paths.</param>
    /// <param name="mcpDllPath">The absolute, regular-file path to the locally built MCP assembly.</param>
    public void Publish(ResourcePlan plan, string mcpDllPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpDllPath);
        if (!Path.IsPathFullyQualified(mcpDllPath))
        {
            throw new ArgumentException("MCP server path must be absolute.", nameof(mcpDllPath));
        }

        string fullMcpDllPath = Path.GetFullPath(mcpDllPath);
        if (Directory.Exists(fullMcpDllPath)
            || !RegularFileGuard.Exists(fullMcpDllPath, "Filtrace MCP server"))
        {
            throw new FileNotFoundException("Filtrace MCP server does not exist.", fullMcpDllPath);
        }

        ValidateManagedPath(plan);
        using McpConfigurationDocument? configuration = McpConfigurationDocument.Read(
            plan.McpConfigurationPath);

        string directory = Path.GetDirectoryName(plan.McpConfigurationPath)
            ?? throw new InvalidDataException("MCP configuration has no parent directory.");

        Directory.CreateDirectory(directory);
        Write(
            plan,
            plan.McpConfigurationPath,
            writer => WriteConfiguration(
                writer,
                configuration,
                keepServers: true,
                serverWriter: target => WriteLocalServer(target, fullMcpDllPath)));
    }

    /// <summary>
    ///  Restores the captured Filtrace server value and removes files or objects created solely for local testing.
    /// </summary>
    /// <param name="plan">The target's normalized local-testing resource paths.</param>
    /// <param name="baseline">The MCP state captured before publication.</param>
    public void Restore(ResourcePlan plan, McpBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseline);
        LocalTestingStateStore.ValidateMcpBaseline(baseline);
        ValidateManagedPath(plan);
        using McpConfigurationDocument? configuration = McpConfigurationDocument.Read(
            plan.McpConfigurationPath);

        if (configuration is null)
        {
            if (baseline.FileExisted)
            {
                throw new FileNotFoundException(
                    "VS Code MCP configuration disappeared before it could be restored.",
                    plan.McpConfigurationPath);
            }

            return;
        }

        bool keepServers = baseline.ServersExisted
            || HasOtherProperty(configuration.Servers, "filtrace");

        bool keepFile = baseline.FileExisted
            || HasOtherProperty(configuration.Root, "servers")
            || keepServers;

        if (!keepFile)
        {
            File.Delete(plan.McpConfigurationPath);
            return;
        }

        Write(
            plan,
            plan.McpConfigurationPath,
            writer => WriteConfiguration(
                writer,
                configuration,
                keepServers,
                baseline.ServerExisted
                    ? target => WriteBaselineServer(target, baseline)
                    : null));
    }

    private void Write(
        ResourcePlan plan,
        string path,
        Action<Utf8JsonWriter> writeConfiguration)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("MCP configuration has no parent directory.");

        string temporaryPath = Path.Join(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (FileStream stream = new(temporaryPath, options))
            {
                using (Utf8JsonWriter writer = new(stream, new() { Indented = true }))
                {
                    writeConfiguration(writer);
                }

                if (stream.Length > McpConfigurationDocument.MaxBytes)
                {
                    throw new InvalidDataException(
                        $"Updated VS Code MCP configuration exceeds the {McpConfigurationDocument.MaxBytes} byte safety limit: '{path}'.");
                }

                stream.Flush(flushToDisk: true);
            }

            beforeReplace?.Invoke();
            ValidateManagedPath(plan);
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            else
            {
                if (File.Exists(path))
                {
                    File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(path));
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void WriteConfiguration(
        Utf8JsonWriter writer,
        McpConfigurationDocument? configuration,
        bool keepServers,
        Action<Utf8JsonWriter>? serverWriter)
    {
        writer.WriteStartObject();
        bool foundServers = false;
        if (configuration is not null)
        {
            foreach (JsonProperty property in configuration.Root.EnumerateObject())
            {
                if (property.NameEquals("servers"))
                {
                    foundServers = true;
                    if (keepServers)
                    {
                        writer.WritePropertyName("servers");
                        WriteServers(writer, configuration, serverWriter);
                    }
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
        }

        if (keepServers && !foundServers)
        {
            writer.WritePropertyName("servers");
            WriteServers(writer, configuration, serverWriter);
        }

        writer.WriteEndObject();
    }

    private static void WriteServers(
        Utf8JsonWriter writer,
        McpConfigurationDocument? configuration,
        Action<Utf8JsonWriter>? serverWriter)
    {
        writer.WriteStartObject();
        bool foundServer = false;
        if (configuration?.ServersExisted is true)
        {
            foreach (JsonProperty property in configuration.Servers.EnumerateObject())
            {
                if (property.NameEquals("filtrace"))
                {
                    foundServer = true;
                    serverWriter?.Invoke(writer);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
        }

        if (!foundServer)
        {
            serverWriter?.Invoke(writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteLocalServer(Utf8JsonWriter writer, string mcpDllPath)
    {
        writer.WritePropertyName("filtrace");
        writer.WriteStartObject();
        writer.WriteString("type", "stdio");
        writer.WriteString("command", "dotnet");
        writer.WriteStartArray("args");
        writer.WriteStringValue(mcpDllPath);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteBaselineServer(Utf8JsonWriter writer, McpBaseline baseline)
    {
        writer.WritePropertyName("filtrace");
        baseline.Server!.Value.WriteTo(writer);
    }

    private static bool HasOtherProperty(JsonElement parent, string propertyName)
    {
        return parent.ValueKind is JsonValueKind.Object
            && parent.EnumerateObject().Any(property => !property.NameEquals(propertyName));
    }

    private static void ValidateManagedPath(ResourcePlan plan)
    {
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.McpConfigurationPath);
    }

}
