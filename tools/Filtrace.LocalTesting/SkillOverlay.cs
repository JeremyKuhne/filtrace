// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

internal static class SkillOverlay
{
    internal const int MaxBytes = 1024 * 1024;

    public static byte[]? Read(string skillDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillDirectory);
        if (File.Exists(skillDirectory))
        {
            throw new InvalidDataException(
                $"Skill destination is a file, not a directory: '{skillDirectory}'.");
        }
        if (!Directory.Exists(skillDirectory))
        {
            return null;
        }
        DirectoryInfo directory = new(skillDirectory);
        if ((directory.Attributes & FileAttributes.ReparsePoint) is not 0
            || directory.LinkTarget is not null)
        {
            throw new InvalidDataException(
                $"Skill destination must not be a link: '{skillDirectory}'.");
        }

        string path = Path.Join(skillDirectory, "overlay.md");
        if (Directory.Exists(path))
        {
            throw new InvalidDataException($"Consumer overlay is a directory: '{path}'.");
        }
        FileInfo overlay = new(path);
        if (overlay.LinkTarget is not null)
        {
            throw new InvalidDataException($"Consumer overlay must not be a link: '{path}'.");
        }
        if (!overlay.Exists)
        {
            return null;
        }
        if ((overlay.Attributes & FileAttributes.ReparsePoint) is not 0)
        {
            throw new InvalidDataException($"Consumer overlay must not be a link: '{path}'.");
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes)
        {
            throw new InvalidDataException(
                $"Consumer overlay exceeds the {MaxBytes} byte safety limit: '{path}'.");
        }

        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}