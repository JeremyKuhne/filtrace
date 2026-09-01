// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Publishes a local Filtrace skill and restores the exact prior skill baseline.
/// </summary>
/// <param name="beforeMutation">An optional test hook invoked immediately before a destination mutation.</param>
/// <param name="afterCopy">An optional test hook invoked after staging has copied the source snapshot.</param>
internal sealed class LocalTestingSkillDirectory(
    Action? beforeMutation = null,
    Action? afterCopy = null)
{
    /// <summary>
    ///  Stages and publishes the local skill while carrying a consumer-owned overlay forward.
    /// </summary>
    /// <param name="plan">The target's normalized local-testing resource paths.</param>
    /// <param name="sourceSkillDirectory">The absolute path to the locally built skill directory.</param>
    public void Publish(ResourcePlan plan, string sourceSkillDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSkillDirectory);
        if (!Path.IsPathFullyQualified(sourceSkillDirectory))
        {
            throw new ArgumentException(
                "Filtrace skill source must be absolute.",
                nameof(sourceSkillDirectory));
        }

        string source = Path.GetFullPath(sourceSkillDirectory);
    EnsureSourceDoesNotOverlapOperationPaths(source, plan);
        RecoverInterruptedMutation(plan);
        DirectorySnapshot sourceSnapshot = ReadDirectory(source, "Filtrace skill source")
            ?? throw new DirectoryNotFoundException($"Filtrace skill source does not exist: '{source}'.");

        _ = ReadDirectory(plan.SkillDestination, "Skill destination");
        byte[]? overlay = SkillOverlay.Read(plan.SkillDestination);
        PrepareStaging(plan, source, sourceSnapshot, overlay);
        PublishStaging(plan);
    }

    /// <summary>
    ///  Restores the captured skill directory or removes the local skill when none previously existed.
    /// </summary>
    /// <param name="plan">The target's normalized local-testing resource paths.</param>
    /// <param name="baseline">The skill state captured before publication.</param>
    public void Restore(ResourcePlan plan, SkillBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseline);
        LocalTestingStateStore.ValidateSkillBaseline(baseline);
        RecoverInterruptedMutation(plan);
        if (!baseline.Existed)
        {
            if (ReadDirectory(plan.SkillBackupPath, "Skill backup") is not null)
            {
                throw new InvalidDataException(
                    $"Absent skill baseline has an unexpected backup: '{plan.SkillBackupPath}'.");
            }

            RemoveDestination(plan);
            return;
        }

        DirectorySnapshot backup = ReadDirectory(plan.SkillBackupPath, "Skill backup")
            ?? throw new DirectoryNotFoundException($"Skill backup does not exist: '{plan.SkillBackupPath}'.");

        if (!backup.Fingerprint.Equals(baseline.BackupSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Skill backup does not match its baseline: '{plan.SkillBackupPath}'.");
        }

        PrepareStaging(plan, plan.SkillBackupPath, backup, overlay: null);
        PublishStaging(plan);
    }

    private void PrepareStaging(
        ResourcePlan plan,
        string source,
        DirectorySnapshot snapshot,
        byte[]? overlay)
    {
        bool prepared = false;
        try
        {
            snapshot.CopyTo(source, plan.SkillStagingPath);
            afterCopy?.Invoke();
            DirectorySnapshot staged = ReadDirectory(
                plan.SkillStagingPath,
                "Skill staging directory")!;

            if (!staged.Fingerprint.Equals(snapshot.Fingerprint, StringComparison.Ordinal))
            {
                throw new IOException("Staged skill did not match the source snapshot.");
            }

            if (overlay is not null)
            {
                File.WriteAllBytes(Path.Join(plan.SkillStagingPath, "overlay.md"), overlay);
            }

            _ = ReadDirectory(plan.SkillStagingPath, "Skill staging directory");
            Directory.CreateDirectory(Path.GetDirectoryName(plan.SkillDestination)!);
            prepared = true;
        }
        finally
        {
            if (!prepared && Directory.Exists(plan.SkillStagingPath))
            {
                DeleteOperationDirectory(plan.SkillStagingPath);
            }
        }
    }

    private void PublishStaging(ResourcePlan plan)
    {
        // A boundary failure retains staging; the next locked operation recovers it.
        beforeMutation?.Invoke();
        ValidateManagedPaths(plan);
        _ = ReadDirectory(plan.SkillStagingPath, "Skill staging directory")
            ?? throw new DirectoryNotFoundException(
                $"Skill staging directory does not exist: '{plan.SkillStagingPath}'.");

        bool destinationExisted = ReadDirectory(plan.SkillDestination, "Skill destination") is not null;
        if (ReadDirectory(plan.SkillRetiredPath, "Retired skill directory") is not null)
        {
            throw new InvalidDataException(
                $"Retired skill directory was not recovered: '{plan.SkillRetiredPath}'.");
        }

        if (destinationExisted)
        {
            Directory.Move(plan.SkillDestination, plan.SkillRetiredPath);
        }

        try
        {
            Directory.Move(plan.SkillStagingPath, plan.SkillDestination);
        }
        catch
        {
            if (destinationExisted && !Directory.Exists(plan.SkillDestination))
            {
                Directory.Move(plan.SkillRetiredPath, plan.SkillDestination);
            }

            throw;
        }

        if (destinationExisted)
        {
            DeleteOperationDirectory(plan.SkillRetiredPath);
        }
    }

    private void RemoveDestination(ResourcePlan plan)
    {
        beforeMutation?.Invoke();
        ValidateManagedPaths(plan);
        if (ReadDirectory(plan.SkillDestination, "Skill destination") is null)
        {
            return;
        }

        Directory.Move(plan.SkillDestination, plan.SkillRetiredPath);
        try
        {
            DeleteOperationDirectory(plan.SkillRetiredPath);
        }
        catch
        {
            if (!Directory.Exists(plan.SkillDestination))
            {
                Directory.Move(plan.SkillRetiredPath, plan.SkillDestination);
            }

            throw;
        }
    }

    private static void RecoverInterruptedMutation(ResourcePlan plan)
    {
        ValidateManagedPaths(plan);
        DirectorySnapshot? retired = ReadDirectory(plan.SkillRetiredPath, "Retired skill directory");
        DirectorySnapshot? destination = ReadDirectory(plan.SkillDestination, "Skill destination");
        if (retired is not null)
        {
            if (destination is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(plan.SkillDestination)!);
                Directory.Move(plan.SkillRetiredPath, plan.SkillDestination);
            }
            else
            {
                DeleteOperationDirectory(plan.SkillRetiredPath);
            }
        }

        if (ReadDirectory(
            plan.SkillStagingPath,
            "Skill staging directory",
            LocalTestingBaselineCapturer.MaxSkillEntries + 1,
            LocalTestingBaselineCapturer.MaxSkillBytes + SkillOverlay.MaxBytes) is not null)
        {
            DeleteOperationDirectory(plan.SkillStagingPath);
        }
    }

    private static void DeleteOperationDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                path,
                "*",
                SearchOption.AllDirectories))
            {
                ClearReadOnly(entry);
            }

            ClearReadOnly(path);
        }

        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnly(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) is not 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static DirectorySnapshot? ReadDirectory(
        string path,
        string description,
        int maxEntries = LocalTestingBaselineCapturer.MaxSkillEntries,
        long maxBytes = LocalTestingBaselineCapturer.MaxSkillBytes)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (directory.LinkTarget is not null
            || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) is not 0))
        {
            throw new InvalidDataException($"{description} must not be a link: '{path}'.");
        }

        if (!directory.Exists)
        {
            if (RegularFileGuard.Exists(path, description))
            {
                throw new InvalidDataException($"{description} must be a directory: '{path}'.");
            }

            return null;
        }

        return DirectorySnapshot.Create(
            path,
            description,
            maxEntries,
            maxBytes);
    }

    private static void ValidateManagedPaths(ResourcePlan plan)
    {
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.SkillDestination);
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.SkillStagingPath);
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.SkillRetiredPath);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.SkillBackupPath);
    }

    private static void EnsureSourceDoesNotOverlapOperationPaths(
        string source,
        ResourcePlan plan)
    {
        if (PathsOverlap(source, plan.SkillStagingPath)
            || PathsOverlap(source, plan.SkillRetiredPath))
        {
            throw new InvalidDataException(
                $"Filtrace skill source must not overlap a reserved operation path: '{source}'.");
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        return Contains(first, second) || Contains(second, first);
    }

    private static bool Contains(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return relative.Equals(".", StringComparison.Ordinal)
            || (!Path.IsPathFullyQualified(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
