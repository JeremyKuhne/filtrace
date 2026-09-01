// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Captures the pre-install MCP and skill state needed to restore a local-testing target safely.
/// </summary>
internal sealed class LocalTestingBaselineCapturer
{
    /// <summary>
    ///  The maximum supported size of a VS Code MCP configuration file, in bytes.
    /// </summary>
    internal const int MaxMcpConfigurationBytes = McpConfigurationDocument.MaxBytes;

    /// <summary>
    ///  The maximum number of files and directories retained in a skill backup.
    /// </summary>
    internal const int MaxSkillEntries = 2048;

    /// <summary>
    ///  The maximum aggregate size of files retained in a skill backup, in bytes.
    /// </summary>
    internal const long MaxSkillBytes = 16 * 1024 * 1024;

    /// <summary>
    ///  Captures the managed resources that local testing may replace and records directories it may create.
    /// </summary>
    /// <param name="plan">The validated paths for the target checkout and its shared local-testing state.</param>
    /// <returns>A baseline that can restore the target to its current state.</returns>
    public LocalTestingBaseline Capture(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureDirectory(plan.TargetRoot, "Target repository");
        EnsureDirectory(plan.GitDirectory, "Git directory");
        EnsureDirectory(plan.ArtifactsDirectory, "Local-testing artifacts directory");

        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.McpConfigurationPath);
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.SkillDestination);
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.SkillStagingPath);
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.SkillRetiredPath);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.ArtifactsDirectory);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.SkillBackupPath);
        EnsurePathAbsent(plan.SkillStagingPath, "Skill staging path");
        EnsurePathAbsent(plan.SkillRetiredPath, "Retired skill path");

        McpBaseline mcp = McpConfigurationDocument.Capture(plan.McpConfigurationPath);
        SkillBaseline skill = CaptureSkill(plan.SkillDestination, plan.SkillBackupPath);

        return new()
        {
            Mcp = mcp,
            Skill = skill,
            CreatedDirectories = CaptureCreatedDirectories(plan)
        };
    }

    private static SkillBaseline CaptureSkill(string source, string backup)
    {
        if (File.Exists(backup) || Directory.Exists(backup))
        {
            throw new InvalidDataException($"Skill backup already exists: '{backup}'.");
        }

        if (File.Exists(source))
        {
            throw new InvalidDataException(
                $"Skill destination is a file, not a directory: '{source}'.");
        }

        if (!Directory.Exists(source))
        {
            return new();
        }

        DirectorySnapshot sourceSnapshot = DirectorySnapshot.Create(
            source,
            MaxSkillEntries,
            MaxSkillBytes);

        string staging = $"{backup}.{Guid.NewGuid():N}.tmp";
        try
        {
            sourceSnapshot.CopyTo(source, staging);
            DirectorySnapshot backupSnapshot = DirectorySnapshot.Create(
                staging,
                MaxSkillEntries,
                MaxSkillBytes);

            if (!sourceSnapshot.Fingerprint.Equals(
                backupSnapshot.Fingerprint,
                StringComparison.Ordinal))
            {
                throw new IOException("Skill backup did not match the source snapshot.");
            }

            Directory.Move(staging, backup);
            return new()
            {
                Existed = true,
                BackupSha256 = backupSnapshot.Fingerprint
            };
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static CreatedDirectoryBaseline CaptureCreatedDirectories(ResourcePlan plan)
    {
        string vscode = Path.GetDirectoryName(plan.McpConfigurationPath)
            ?? throw new InvalidDataException("MCP configuration has no parent directory.");

        string agents = Path.Join(plan.TargetRoot, ".agents");
        string skills = Path.Join(agents, "skills");

        return new()
        {
            Vscode = !Directory.Exists(vscode),
            Agents = !Directory.Exists(agents),
            Skills = !Directory.Exists(skills)
        };
    }

    private static void EnsureDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{description} does not exist: '{path}'.");
        }
    }

    private static void EnsurePathAbsent(string path, string description)
    {
        if (Directory.Exists(path) || RegularFileGuard.Exists(path, description))
        {
            throw new InvalidDataException($"{description} already exists: '{path}'.");
        }
    }
}
