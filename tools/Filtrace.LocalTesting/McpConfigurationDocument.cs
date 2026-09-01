// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.LocalTesting;

internal sealed class McpConfigurationDocument(
    JsonDocument document,
    JsonElement root,
    bool serversExisted,
    JsonElement servers,
    bool serverExisted,
    JsonElement server) : IDisposable
{
    internal const int MaxBytes = 1024 * 1024;

    public JsonElement Root { get; } = root;

    public bool ServersExisted { get; } = serversExisted;

    public JsonElement Servers { get; } = servers;

    public bool ServerExisted { get; } = serverExisted;

    public JsonElement Server { get; } = server;

    public static McpBaseline Capture(string path)
    {
        using McpConfigurationDocument? configuration = Read(path);
        return configuration is null
            ? new()
            : new()
            {
                FileExisted = true,
                ServersExisted = configuration.ServersExisted,
                ServerExisted = configuration.ServerExisted,
                Server = configuration.ServerExisted
                    ? configuration.Server.Clone()
                    : null
            };
    }

    public static McpConfigurationDocument? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                $"VS Code MCP configuration is a directory, not a file: '{path}'.");
        }
        if (!RegularFileGuard.Exists(path, "VS Code MCP configuration"))
        {
            return null;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes)
        {
            throw new InvalidDataException(
                $"VS Code MCP configuration exceeds the {MaxBytes} byte safety limit: '{path}'.");
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(stream, new()
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"VS Code MCP configuration is not valid JSON: '{path}'.",
                exception);
        }

        try
        {
            JsonElement parsedRoot = parsed.RootElement;
            if (parsedRoot.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"VS Code MCP configuration root must be a JSON object: '{path}'.");
            }

            bool parsedServersExisted = TryGetUniqueProperty(
                parsedRoot,
                "servers",
                path,
                out JsonElement parsedServers);
            if (parsedServersExisted && parsedServers.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"VS Code MCP configuration property 'servers' must be a JSON object: '{path}'.");
            }

            JsonElement parsedServer = default;
            bool parsedServerExisted = parsedServersExisted
                && TryGetUniqueProperty(parsedServers, "filtrace", path, out parsedServer);
            if (parsedServerExisted && parsedServer.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"VS Code MCP server 'filtrace' must be a JSON object: '{path}'.");
            }

            return new(
                parsed,
                parsedRoot,
                parsedServersExisted,
                parsedServers,
                parsedServerExisted,
                parsedServer);
        }
        catch
        {
            parsed.Dispose();
            throw;
        }
    }

    private static bool TryGetUniqueProperty(
        JsonElement parent,
        string propertyName,
        string path,
        out JsonElement value)
    {
        bool found = false;
        value = default;
        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (!property.NameEquals(propertyName))
            {
                continue;
            }
            if (found)
            {
                throw new InvalidDataException(
                    $"VS Code MCP configuration contains duplicate '{propertyName}' properties: '{path}'.");
            }

            found = true;
            value = property.Value;
        }

        return found;
    }

    public void Dispose()
    {
        document.Dispose();
    }
}
