// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filtrace.LocalTesting;

[JsonConverter(typeof(LocalTestingStatusJsonConverter))]
internal enum LocalTestingStatus
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("installing")]
    Installing,

    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("restoring")]
    Restoring,

    [JsonStringEnumMemberName("cleanup")]
    Cleanup
}

internal sealed record LocalTestingState
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    public required LocalTestingStatus Status { get; init; }

    public required string SourceCheckout { get; init; }

    public required LocalTestingBaseline Baseline { get; init; }

    public CliInstallation? Cli { get; init; }
}

internal sealed record LocalTestingBaseline
{
    public required McpBaseline Mcp { get; init; }

    public required SkillBaseline Skill { get; init; }

    public required CreatedDirectoryBaseline CreatedDirectories { get; init; }
}

internal sealed record McpBaseline
{
    public bool FileExisted { get; init; }

    public bool ServersExisted { get; init; }

    public bool ServerExisted { get; init; }

    public JsonElement? Server { get; init; }
}

internal sealed record SkillBaseline
{
    public bool Existed { get; init; }

    public string? BackupSha256 { get; init; }
}

internal sealed record CreatedDirectoryBaseline
{
    public bool Vscode { get; init; }

    public bool Agents { get; init; }

    public bool Skills { get; init; }
}

internal sealed record CliInstallation
{
    public required string PackageVersion { get; init; }

    public required string PackageSha256 { get; init; }
}
