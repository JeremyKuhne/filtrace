// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.LocalTesting;

/// <summary>
///  Records the MCP file, server collection, and Filtrace server entry present before installation.
/// </summary>
internal sealed record McpBaseline
{
    /// <summary>
    ///  Gets whether the MCP configuration file existed.
    /// </summary>
    public bool FileExisted { get; init; }

    /// <summary>
    ///  Gets whether the configuration contained a <c>servers</c> property.
    /// </summary>
    public bool ServersExisted { get; init; }

    /// <summary>
    ///  Gets whether the server collection contained a <c>filtrace</c> entry.
    /// </summary>
    public bool ServerExisted { get; init; }

    /// <summary>
    ///  Gets the original <c>filtrace</c> server value when one existed.
    /// </summary>
    public JsonElement? Server { get; init; }
}
