// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Records whether a Filtrace skill existed and identifies its verified backup.
/// </summary>
internal sealed record SkillBaseline
{
    /// <summary>
    ///  Gets whether the skill directory existed before installation.
    /// </summary>
    public bool Existed { get; init; }

    /// <summary>
    ///  Gets the SHA-256 snapshot fingerprint of the backup, when a skill existed.
    /// </summary>
    public string? BackupSha256 { get; init; }
}
