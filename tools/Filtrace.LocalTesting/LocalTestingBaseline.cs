// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Records all target resources that must be restored after local testing.
/// </summary>
internal sealed record LocalTestingBaseline
{
    /// <summary>
    ///  Gets the prior VS Code MCP configuration state.
    /// </summary>
    public required McpBaseline Mcp { get; init; }

    /// <summary>
    ///  Gets the prior installed-skill state and backup identity.
    /// </summary>
    public required SkillBaseline Skill { get; init; }

    /// <summary>
    ///  Gets the managed directories that did not exist before installation.
    /// </summary>
    public required CreatedDirectoryBaseline CreatedDirectories { get; init; }
}
