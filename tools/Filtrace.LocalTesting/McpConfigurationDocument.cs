// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.LocalTesting;

/// <summary>
///  Owns a validated MCP JSON document and exposes the unique <c>servers</c> and <c>filtrace</c> values it contained.
/// </summary>
/// <param name="document">The owned JSON document that keeps all exposed elements alive.</param>
/// <param name="root">The validated root object.</param>
/// <param name="serversExisted">Whether the root contained a unique <c>servers</c> object.</param>
/// <param name="servers">The <c>servers</c> object when present.</param>
/// <param name="serverExisted">Whether <c>servers</c> contained a unique object-valued <c>filtrace</c> entry.</param>
/// <param name="server">The <c>filtrace</c> server object when present.</param>
internal sealed class McpConfigurationDocument(
    JsonDocument document,
    JsonElement root,
    bool serversExisted,
    JsonElement servers,
    bool serverExisted,
    JsonElement server) : IDisposable
{
    /// <summary>
    ///  The maximum accepted MCP configuration length, in bytes.
    /// </summary>
    internal const int MaxBytes = 1024 * 1024;

    /// <summary>
    ///  Gets the validated configuration root object.
    /// </summary>
    public JsonElement Root { get; } = root;

    /// <summary>
    ///  Gets whether the root contained a <c>servers</c> object.
    /// </summary>
    public bool ServersExisted { get; } = serversExisted;

    /// <summary>
    ///  Gets the <c>servers</c> object; meaningful only when <see cref="ServersExisted"/> is true.
    /// </summary>
    public JsonElement Servers { get; } = servers;

    /// <summary>
    ///  Gets whether the server collection contained a <c>filtrace</c> object.
    /// </summary>
    public bool ServerExisted { get; } = serverExisted;

    /// <summary>
    ///  Gets the prior <c>filtrace</c> object; meaningful only when <see cref="ServerExisted"/> is true.
    /// </summary>
    public JsonElement Server { get; } = server;

    /// <summary>
    ///  Captures existence flags and a detached copy of the current Filtrace server value for later restoration.
    /// </summary>
    /// <param name="path">The VS Code MCP configuration path.</param>
    /// <returns>A baseline describing the file, server collection, and Filtrace entry.</returns>
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

    /// <summary>
    ///  Reads a bounded regular file, accepts JSON comments and trailing commas, and rejects duplicate managed properties.
    /// </summary>
    /// <param name="path">The VS Code MCP configuration path.</param>
    /// <returns>An owned validated document, or <see langword="null"/> when the file is absent.</returns>
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

    /// <summary>
    ///  Releases the underlying JSON document and invalidates its borrowed elements.
    /// </summary>
    public void Dispose()
    {
        document.Dispose();
    }
}
