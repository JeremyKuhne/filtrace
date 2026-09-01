// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Identifies managed directories that installation may create and restoration may remove.
/// </summary>
internal sealed record CreatedDirectoryBaseline
{
    /// <summary>
    ///  Gets whether the target's <c>.vscode</c> directory was absent before installation.
    /// </summary>
    public bool Vscode { get; init; }

    /// <summary>
    ///  Gets whether the target's <c>.agents</c> directory was absent before installation.
    /// </summary>
    public bool Agents { get; init; }

    /// <summary>
    ///  Gets whether the target's <c>.agents/skills</c> directory was absent before installation.
    /// </summary>
    public bool Skills { get; init; }
}
